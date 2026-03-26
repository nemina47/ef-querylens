using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Engine;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Caching.Memory;

namespace EFQueryLens.Daemon;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!TryParseArgs(args, out var workspacePath, out var requestedPort))
        {
            Console.Error.WriteLine("Usage: querylens-daemon --workspace <absolute-or-relative-path> [--port <tcp-port>]");
            return 1;
        }

        workspacePath = Path.GetFullPath(workspacePath);
        var listenPort = requestedPort ?? 0;

        Console.Error.WriteLine($"[QL-Engine] startup workspace={workspacePath} requestedPort={listenPort}");

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            Console.Error.WriteLine($"[QL-Engine] unhandled-exception terminating={eventArgs.IsTerminating} exception={eventArgs.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Console.Error.WriteLine($"[QL-Engine] unobserved-task-exception exception={eventArgs.Exception}");
        };

        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Critical);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, listenPort, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

        var jsonOptions = QueryLensJsonOptions.Create();

        builder.Services.ConfigureHttpJsonOptions(opts =>
        {
            opts.SerializerOptions.PropertyNamingPolicy = jsonOptions.PropertyNamingPolicy;
            opts.SerializerOptions.PropertyNameCaseInsensitive = jsonOptions.PropertyNameCaseInsensitive;
            foreach (var converter in jsonOptions.Converters)
            {
                opts.SerializerOptions.Converters.Add(converter);
            }
        });

        builder.Services.AddSingleton<IQueryLensEngine, QueryLensEngine>();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton(jsonOptions);

        var portFilePath = string.Empty;
        var app = builder.Build();

        // Shared state
        var lastActivity = DateTime.UtcNow;
        var inflight = new ConcurrentDictionary<string, Lazy<Task<QueryTranslationResult>>>(StringComparer.Ordinal);
        var cache = app.Services.GetRequiredService<IMemoryCache>();
        var engine = app.Services.GetRequiredService<IQueryLensEngine>();

        // GET /ping
        app.MapGet("/ping", () =>
        {
            lastActivity = DateTime.UtcNow;
            return Results.Ok("pong");
        });

        // POST /translate
        app.MapPost("/translate", async (TranslationRequest request) =>
        {
            lastActivity = DateTime.UtcNow;

            var cacheKey = ComputeCacheKey(request);

            if (cache.TryGetValue<QueryTranslationResult>(cacheKey, out var cached) && cached is not null)
            {
                return Results.Ok(cached);
            }

            var lazy = inflight.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<QueryTranslationResult>>(
                    () => engine.TranslateAsync(request, CancellationToken.None),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            QueryTranslationResult result;
            try
            {
                result = await lazy.Value;
            }
            finally
            {
                inflight.TryRemove(cacheKey, out _);
            }

            if (result.Success)
            {
                cache.Set(cacheKey, result, TimeSpan.FromSeconds(60));
            }

            lastActivity = DateTime.UtcNow;
            return Results.Ok(result);
        });

        // POST /translate/warm
        // Returns 202 immediately; starts a background translation so hover can hit cache.
        // Uses the same inflight dict as /translate — deduplicates concurrent requests naturally.
        app.MapPost("/translate/warm", (TranslationRequest request) =>
        {
            lastActivity = DateTime.UtcNow;
            var cacheKey = ComputeCacheKey(request);

            if (cache.TryGetValue<QueryTranslationResult>(cacheKey, out _))
            {
                // Already cached — nothing to do.
                return Results.Accepted();
            }

            inflight.GetOrAdd(
                cacheKey,
                key => new Lazy<Task<QueryTranslationResult>>(
                    () => Task.Run(async () =>
                    {
                        var r = await engine.TranslateAsync(request, CancellationToken.None);
                        if (r.Success)
                            cache.Set(key, r, TimeSpan.FromSeconds(60));
                        inflight.TryRemove(key, out _);
                        return r;
                    }),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return Results.Accepted();
        });

        // POST /inspect-model
        app.MapPost("/inspect-model", async (ModelInspectionRequest request) =>
        {
            lastActivity = DateTime.UtcNow;
            var snapshot = await engine.InspectModelAsync(request, CancellationToken.None);
            lastActivity = DateTime.UtcNow;
            return Results.Ok(snapshot);
        });

        // POST /invalidate
        app.MapPost("/invalidate", () =>
        {
            lastActivity = DateTime.UtcNow;
            if (cache is MemoryCache mc)
            {
                mc.Clear();
            }
            return Results.Ok();
        });

        // POST /shutdown
        app.MapPost("/shutdown", async () =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                await app.StopAsync();
            });
            return Results.Accepted();
        });

        try
        {
            await app.StartAsync();
            var boundPort = ResolveBoundPort(app, requestedPort);

            portFilePath = Path.Combine(Path.GetTempPath(), $"querylens-{WorkspaceHash(workspacePath)}.port");
            await File.WriteAllTextAsync(portFilePath, boundPort.ToString(CultureInfo.InvariantCulture));

            Console.WriteLine($"QUERYLENS_PORT={boundPort}");
            Console.Out.Flush();

            Console.Error.WriteLine($"[QL-Engine] listening workspace={workspacePath} port={boundPort}");

            // Idle shutdown timer
            var idleMinutes = int.TryParse(
                Environment.GetEnvironmentVariable("QUERYLENS_IDLE_SHUTDOWN_MINUTES"),
                out var parsed) ? parsed : 10;

            using var idleTimer = new System.Timers.Timer(TimeSpan.FromSeconds(60).TotalMilliseconds);
            idleTimer.Elapsed += (_, _) =>
            {
                if (DateTime.UtcNow - lastActivity > TimeSpan.FromMinutes(idleMinutes))
                {
                    Console.Error.WriteLine($"[QL-Engine] idle-shutdown after {idleMinutes}m of inactivity");
                    _ = app.StopAsync();
                }
            };
            idleTimer.AutoReset = true;
            idleTimer.Start();

            await app.WaitForShutdownAsync();
            await app.StopAsync();

            return 0;
        }
        catch (Exception ex)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var crashPath = Path.Combine(Path.GetTempPath(), $"querylens-daemon-crash-{timestamp}-pid{Environment.ProcessId}.log");
            await File.WriteAllTextAsync(crashPath, ex.ToString());
            Console.Error.WriteLine($"[QL-Engine] fatal-exit crashLog={crashPath} type={ex.GetType().Name} message={ex.Message}");
            throw;
        }
        finally
        {
            if (!string.IsNullOrEmpty(portFilePath) && File.Exists(portFilePath))
            {
                try { File.Delete(portFilePath); } catch { /* best-effort */ }
            }
        }
    }

    private static int ResolveBoundPort(WebApplication app, int? requestedPort)
    {
        if (requestedPort is > 0)
        {
            return requestedPort.Value;
        }

        var server = app.Services.GetRequiredService<IServer>();
        var addressesFeature = server.Features.Get<IServerAddressesFeature>();
        var addresses = addressesFeature?.Addresses ?? [];

        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (uri.Port > 0)
            {
                return uri.Port;
            }
        }

        throw new InvalidOperationException("Kestrel started but no bound HTTP port was discovered.");
    }

    private static bool TryParseArgs(string[] args, out string workspacePath, out int? port)
    {
        workspacePath = string.Empty;
        port = null;

        for (var i = 0; i < args.Length; i++)
        {
            var current = args[i];
            if (current.Equals("--workspace", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                workspacePath = args[++i];
                continue;
            }

            if (current.Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var parsedPort) && parsedPort is >= 0 and <= 65535)
                {
                    port = parsedPort;
                }
            }
        }

        return !string.IsNullOrWhiteSpace(workspacePath);
    }

    /// <summary>
    /// Returns the first 12 hex characters of the SHA256 of the normalized workspace path.
    /// </summary>
    private static string WorkspaceHash(string workspacePath)
    {
        var normalized = workspacePath.Replace('\\', '/').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    /// <summary>
    /// Returns the first 16 hex characters of the SHA256 of all <see cref="TranslationRequest"/> fields
    /// that affect the compiled eval assembly or its stub declarations.
    /// </summary>
    private static string ComputeCacheKey(TranslationRequest r)
    {
        var sb = new StringBuilder();
        sb.Append(r.Expression).Append('\0');
        sb.Append(r.AssemblyPath ?? string.Empty).Append('\0');
        sb.Append(r.DbContextTypeName ?? string.Empty).Append('\0');
        sb.Append(r.ContextVariableName).Append('\0');
        foreach (var ns in r.AdditionalImports.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(ns).Append('\0');
        foreach (var kv in r.UsingAliases.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append(':').Append(kv.Value).Append('\0');
        foreach (var st in r.UsingStaticTypes.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(st).Append('\0');
        foreach (var kv in r.LocalVariableTypes.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append(':').Append(kv.Value).Append('\0');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
