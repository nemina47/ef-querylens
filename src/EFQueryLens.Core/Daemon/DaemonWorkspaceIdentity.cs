using System.Security.Cryptography;
using System.Text;

namespace EFQueryLens.Core.Daemon;

public static class DaemonWorkspaceIdentity
{
    public static string NormalizeWorkspacePath(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        return Path.GetFullPath(workspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string ComputeWorkspaceHash(string workspacePath)
    {
        var normalized = NormalizeWorkspacePath(workspacePath);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    public static string BuildPidFilePath(string workspacePath)
    {
        var hash = ComputeWorkspaceHash(workspacePath);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".querylens", "pids");
        return Path.Combine(root, $"{hash}.json");
    }
}
