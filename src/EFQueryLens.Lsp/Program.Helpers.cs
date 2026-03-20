using System.Text;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp;
using EFQueryLens.Lsp.Engine;

internal static class LspProgramHelpers
{
    internal static TextWriter? ConfigureLspLogWriter()
    {
        var rawPath = Environment.GetEnvironmentVariable("QUERYLENS_LSP_LOG_FILE");
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(rawPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var fileWriter = new StreamWriter(stream)
            {
                AutoFlush = true,
            };

            var originalError = Console.Error;
            Console.SetError(new TeeTextWriter(originalError, fileWriter));
            Console.Error.WriteLine($"[QL-LSP] log-sink enabled path={fullPath}");
            return fileWriter;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[QL-LSP] log-sink failed path={rawPath} reason={ex.GetType().Name} message={ex.Message}");
            return null;
        }
    }

    internal static async Task<IQueryLensEngine> CreateEngineAsync(bool debugEnabled)
    {
        Action<string>? log = debugEnabled
            ? message => Console.Error.WriteLine($"[QL-LSP] {message}")
            : null;

        var workspacePath = EngineDiscovery.ResolveWorkspacePath();
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new InvalidOperationException(
                "Could not resolve workspace path for engine startup. " +
                "Set QUERYLENS_WORKSPACE to the solution root.");
        }

        var engineAssemblyPath = EngineDiscovery.ResolveEngineAssemblyPath();

        if (debugEnabled)
        {
            var dllEnv = Environment.GetEnvironmentVariable("QUERYLENS_DAEMON_DLL");
            Console.Error.WriteLine("[QL-LSP] engine env QUERYLENS_DAEMON_DLL=" + (string.IsNullOrWhiteSpace(dllEnv) ? "(not set)" : dllEnv));
            Console.Error.WriteLine("[QL-LSP] engine resolved dll=" + (engineAssemblyPath ?? "(null)"));
            Console.Error.WriteLine("[QL-LSP] workspace=" + workspacePath);
        }

        var startTimeoutMs = LspEnvironment.ReadInt(
            variableName: "QUERYLENS_DAEMON_START_TIMEOUT_MS",
            fallback: 8000,
            min: 250,
            max: 120_000);

        var connectTimeoutMs = LspEnvironment.ReadInt(
            variableName: "QUERYLENS_DAEMON_CONNECT_TIMEOUT_MS",
            fallback: 2500,
            min: 100,
            max: 120_000);

        // Try to find or start the engine process.
        int port;

        if (LspEnvironment.TryReadOptionalInt("QUERYLENS_DAEMON_PORT", min: 1, max: 65535) is { } fixedPort)
        {
            // Port explicitly provided — trust it and skip discovery.
            port = fixedPort;
            log?.Invoke($"engine-port override port={port}");
        }
        else
        {
            port = await EngineDiscovery.GetOrStartEngineAsync(
                workspacePath,
                engineAssemblyPath ?? string.Empty,
                startTimeoutMs,
                debugEnabled,
                log);
        }

        // Verify we can reach the engine.
        var client = new EngineHttpClient(port, workspacePath, engineAssemblyPath, startTimeoutMs, debugEnabled);
        try
        {
            using var cts = new CancellationTokenSource(connectTimeoutMs);
            await client.PingAsync(cts.Token);

            if (debugEnabled)
                Console.Error.WriteLine($"[QL-LSP] engine-connection success port={port} workspace={workspacePath}");

            return client;
        }
        catch (Exception ex)
        {
            await client.DisposeAsync();
            throw new InvalidOperationException(
                $"Failed to connect to QueryLens engine on port '{port}'.", ex);
        }
    }

    internal static bool TryRunCacheStatusCommand(string[] args)
    {
        if (!args.Contains("--cache-status", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "EFQueryLens", "shadow");
            var bundleDir = Path.Combine(rootDir, "bundles");
            var stagingDir = Path.Combine(rootDir, "staging");

            long bundleSize = 0;
            int bundleCount = 0;
            int stagingCount = 0;

            if (Directory.Exists(bundleDir))
            {
                var bundleFiles = Directory.EnumerateFiles(bundleDir, "*.dll", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .ToArray();
                bundleCount = bundleFiles.Length;
                bundleSize = bundleFiles.Sum(file => file.Length);
            }

            if (Directory.Exists(stagingDir))
            {
                stagingCount = Directory.EnumerateFiles(stagingDir, "*.dll", SearchOption.AllDirectories).Count();
            }

            var totalMb = bundleSize / 1024d / 1024d;
            var maxMb = LspEnvironment.ReadInt("QUERYLENS_SHADOW_CACHE_MAX_MB", 500, 10, 10_000);

            Console.WriteLine($"cache-root: {rootDir}");
            Console.WriteLine($"bundle-count: {bundleCount}");
            Console.WriteLine($"staging-count: {stagingCount}");
            Console.WriteLine($"bundle-size-mb: {totalMb:F2}");
            Console.WriteLine($"max-size-mb: {maxMb}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cache-status-error: {ex.GetType().Name}: {ex.Message}");
        }

        return true;
    }

    internal static bool TryRunCacheCleanupCommand(string[] args)
    {
        if (!args.Contains("--cache-cleanup", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "EFQueryLens", "shadow");
            var stagingDir = Path.Combine(rootDir, "staging");

            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
                Directory.CreateDirectory(stagingDir);
                Console.WriteLine($"cache-cleanup: staging-reset {stagingDir}");
            }
            else
            {
                Console.WriteLine($"cache-cleanup: no staging directory at {stagingDir}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cache-cleanup-error: {ex.GetType().Name}: {ex.Message}");
        }

        return true;
    }

    internal static bool TryRunCacheClearCommand(string[] args)
    {
        if (!args.Contains("--cache-clear", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "EFQueryLens", "shadow");
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
                Console.WriteLine($"cache-clear: removed {rootDir}");
            }
            else
            {
                Console.WriteLine($"cache-clear: no directory at {rootDir}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cache-clear-error: {ex.GetType().Name}: {ex.Message}");
        }

        return true;
    }
}

internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _first;
    private readonly TextWriter _second;

    public TeeTextWriter(TextWriter first, TextWriter second)
    {
        _first = first;
        _second = second;
    }

    public override Encoding Encoding => _first.Encoding;

    public override void Write(char value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _first.WriteLine(value);
        _second.WriteLine(value);
    }

    public override Task WriteAsync(char value)
    {
        var first = _first.WriteAsync(value);
        var second = _second.WriteAsync(value);
        return Task.WhenAll(first, second);
    }

    public override Task WriteLineAsync(string? value)
    {
        var first = _first.WriteLineAsync(value);
        var second = _second.WriteLineAsync(value);
        return Task.WhenAll(first, second);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _second.Dispose();
        }

        base.Dispose(disposing);
    }
}
