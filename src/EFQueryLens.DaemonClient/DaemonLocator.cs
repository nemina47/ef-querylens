using System.Text.Json;

namespace EFQueryLens.DaemonClient;

public static partial class DaemonLocator
{
    private const string DaemonExecutableFileName = "EFQueryLens.Daemon";
    private const string DaemonAssemblyFileName = "EFQueryLens.Daemon.dll";
    private static readonly JsonSerializerOptions s_pidJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string? ResolveWorkspacePath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("QUERYLENS_WORKSPACE"),
            Environment.GetEnvironmentVariable("QUERYLENS_DAEMON_WORKSPACE"),
            Environment.GetEnvironmentVariable("QUERYLENS_REPOSITORY_ROOT"),
            Environment.CurrentDirectory,
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (Directory.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
                // Ignore invalid path candidates.
            }
        }

        return null;
    }

    public static string? ResolveDaemonAssemblyPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var explicitFullPath = Path.GetFullPath(explicitPath);
            if (File.Exists(explicitFullPath))
            {
                return explicitFullPath;
            }
        }

        var envPath = Environment.GetEnvironmentVariable("QUERYLENS_DAEMON_DLL");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var envFullPath = Path.GetFullPath(envPath);
            if (File.Exists(envFullPath))
            {
                return envFullPath;
            }
        }

        var candidateInBaseDir = Path.Combine(AppContext.BaseDirectory, DaemonAssemblyFileName);
        if (File.Exists(candidateInBaseDir))
        {
            return candidateInBaseDir;
        }

        return null;
    }

    public static string? ResolveDaemonExecutablePath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var explicitFullPath = Path.GetFullPath(explicitPath);
            if (File.Exists(explicitFullPath))
            {
                return explicitFullPath;
            }
        }

        var envPath = Environment.GetEnvironmentVariable("QUERYLENS_DAEMON_EXE");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var envFullPath = Path.GetFullPath(envPath);
            if (File.Exists(envFullPath))
            {
                return envFullPath;
            }
        }

        var candidateFileNames = new[]
        {
            DaemonExecutableFileName,
            $"{DaemonExecutableFileName}.exe",
        };

        foreach (var fileName in candidateFileNames)
        {
            var candidateInBaseDir = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(candidateInBaseDir))
            {
                return candidateInBaseDir;
            }
        }

        return null;
    }

}
