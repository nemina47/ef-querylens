using System.Diagnostics;
using System.Text.Json;
using EFQueryLens.Core;
using EFQueryLens.Core.Daemon;
using StreamJsonRpc;

namespace EFQueryLens.DaemonClient;

/// <summary>
/// <see cref="IQueryLensEngine"/> backed by a QueryLens daemon over a named pipe.
/// The pipe connection is owned by this instance and disposed when the engine is disposed.
/// </summary>
public sealed class DaemonBackedEngine : IQueryLensEngine, IAsyncDisposable
{
    private readonly JsonRpc _rpc;
    private readonly IDaemonService _proxy;
    private readonly string _contextName;
    private readonly bool _debugEnabled;

    /// <summary>
    /// Creates a new <see cref="DaemonBackedEngine"/> wrapping an already-connected
    /// full-duplex <paramref name="pipeStream"/>. Takes ownership of the stream.
    /// </summary>
    public DaemonBackedEngine(Stream pipeStream, string contextName = "default")
    {
        _contextName = contextName;
        _debugEnabled = ReadBoolEnvironmentVariable("QUERYLENS_DEBUG", fallback: false);
        _rpc = new JsonRpc(BuildMessageHandler(pipeStream));
        _proxy = _rpc.Attach<IDaemonService>();
        _rpc.StartListening();
    }

    public async Task<QueryTranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken ct = default)
    {
        var payload = new DaemonTranslateRequest { ContextName = _contextName, Request = request };
        var sw = Stopwatch.StartNew();
        LogDebug(
            $"translate-rpc-start context={_contextName} assembly={request.AssemblyPath} " +
            $"exprLen={request.Expression?.Length ?? 0}");

        try
        {
            var response = await _proxy.TranslateAsync(payload, ct);
            sw.Stop();
            LogDebug(
                $"translate-rpc-finished context={_contextName} success={response.Result.Success} " +
                $"elapsedMs={sw.ElapsedMilliseconds} commands={response.Result.Commands.Count} " +
                $"sqlLen={(response.Result.Sql?.Length ?? 0)}");
            return response.Result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogDebug(
                $"translate-rpc-failed context={_contextName} elapsedMs={sw.ElapsedMilliseconds} " +
                $"type={ex.GetType().Name} message={ex.Message}");
            throw;
        }
    }

    public async Task<QueuedTranslationResult> TranslateQueuedAsync(
        TranslationRequest request,
        CancellationToken ct = default)
    {
        var payload = new DaemonQueuedTranslateRequest
        {
            ContextName = _contextName,
            SemanticKey = BuildSemanticKey(request),
            Request = request,
        };

        var response = await _proxy.TranslateQueuedAsync(payload, ct);
        return new QueuedTranslationResult
        {
            Status = response.Status,
            JobId = response.JobId,
            AverageTranslationMs = response.AverageTranslationMs,
            Result = response.Result,
        };
    }

    public Task<ExplainResult> ExplainAsync(
        ExplainRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("ExplainAsync is not yet exposed by the daemon protocol.");

    public async Task<ModelSnapshot> InspectModelAsync(
        ModelInspectionRequest request, CancellationToken ct = default)
    {
        var payload = new DaemonInspectRequest { ContextName = _contextName, Request = request };
        var response = await _proxy.InspectModelAsync(payload, ct);
        return response.Result;
    }

    /// <summary>
    /// Requests graceful daemon shutdown over RPC.
    /// </summary>
    public async Task ShutdownDaemonAsync(CancellationToken ct = default)
    {
        await _proxy.ShutdownAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        return ValueTask.CompletedTask;
    }

    private static bool ReadBoolEnvironmentVariable(string variableName, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private void LogDebug(string message)
    {
        if (!_debugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[QL-DAEMON-CLIENT] {message}");
    }

    /// <summary>
    /// Builds the StreamJsonRpc message handler for the daemon channel.
    /// Uses 4-byte length-prefix framing with System.Text.Json camelCase serialization.
    /// Must be identical on both client and server.
    /// </summary>
    internal static IJsonRpcMessageHandler BuildMessageHandler(Stream stream)
    {
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
        };
        return new LengthHeaderMessageHandler(stream, stream, formatter);
    }

    private static string BuildSemanticKey(TranslationRequest request)
    {
        static string NormalizeWhitespace(string value)
        {
            var buffer = new char[value.Length];
            var index = 0;
            var previousWasWhitespace = false;

            foreach (var current in value)
            {
                if (char.IsWhiteSpace(current))
                {
                    if (previousWasWhitespace)
                    {
                        continue;
                    }

                    buffer[index++] = ' ';
                    previousWasWhitespace = true;
                }
                else
                {
                    buffer[index++] = current;
                    previousWasWhitespace = false;
                }
            }

            return new string(buffer, 0, index).Trim();
        }

        static string ResolveProjectKey(string? assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return "unknown";
            }

            var normalizedAssemblyPath = Path.GetFullPath(assemblyPath);
            var currentDir = Path.GetDirectoryName(normalizedAssemblyPath);
            while (!string.IsNullOrWhiteSpace(currentDir))
            {
                var hasProject = Directory.EnumerateFiles(currentDir, "*.csproj", SearchOption.TopDirectoryOnly)
                    .Any();
                if (hasProject)
                {
                    return DaemonWorkspaceIdentity.ComputeWorkspaceHash(currentDir);
                }

                currentDir = Directory.GetParent(currentDir)?.FullName;
            }

            return DaemonWorkspaceIdentity.ComputeWorkspaceHash(Path.GetDirectoryName(normalizedAssemblyPath) ?? normalizedAssemblyPath);
        }

        var projectKey = ResolveProjectKey(request.AssemblyPath);
        var contextName = request.ContextVariableName?.Trim().ToLowerInvariant() ?? string.Empty;
        var expression = NormalizeWhitespace(request.Expression ?? string.Empty);
        return $"{projectKey}|{contextName}|{expression}";
    }
}
