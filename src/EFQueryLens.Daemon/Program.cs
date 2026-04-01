using System.Globalization;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Engine;

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
            Console.Error.WriteLine($"[QL-Engine] unhandled-exception terminating={eventArgs.IsTerminating} exception={eventArgs.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            Console.Error.WriteLine($"[QL-Engine] unobserved-task-exception exception={eventArgs.Exception}");

        var jsonOptions = QueryLensJsonOptions.Create();
        var builder = WebApplication.CreateBuilder(args);

        DaemonStartup.ConfigureLogging(builder);
        DaemonStartup.ConfigureKestrel(builder, listenPort);
        DaemonStartup.AddDaemonServices(builder, jsonOptions);

        var app = builder.Build();
        var engine = app.Services.GetRequiredService<IQueryLensEngine>();
        var runtime = app.Services.GetRequiredService<DaemonRuntime>();

        DaemonEndpoints.Map(app, engine, runtime);

        var portFilePath = string.Empty;

        try
        {
            await app.StartAsync();
            var boundPort = DaemonStartup.ResolveBoundPort(app, requestedPort);

            portFilePath = PortFile.GetPath(workspacePath);
            await PortFile.WriteAsync(portFilePath, boundPort);

            Console.WriteLine($"QUERYLENS_PORT={boundPort}");
            Console.Out.Flush();

            Console.Error.WriteLine($"[QL-Engine] listening workspace={workspacePath} port={boundPort}");

            var idleMinutes = int.TryParse(
                Environment.GetEnvironmentVariable("QUERYLENS_IDLE_SHUTDOWN_MINUTES"),
                out var parsed) ? parsed : 10;

            using var _ = IdleShutdownTimer.Start(app, runtime, idleMinutes);

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
            PortFile.TryDelete(portFilePath);
        }
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
                    port = parsedPort;
            }
        }

        return !string.IsNullOrWhiteSpace(workspacePath);
    }
}

