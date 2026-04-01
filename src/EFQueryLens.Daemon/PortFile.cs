using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EFQueryLens.Daemon;

internal static class PortFile
{
    internal static string GetPath(string workspacePath) =>
        Path.Combine(Path.GetTempPath(), $"querylens-{WorkspaceHash(workspacePath)}.port");

    internal static Task WriteAsync(string portFilePath, int port) =>
        File.WriteAllTextAsync(portFilePath, port.ToString(CultureInfo.InvariantCulture));

    internal static void TryDelete(string portFilePath)
    {
        if (!string.IsNullOrEmpty(portFilePath) && File.Exists(portFilePath))
        {
            try { File.Delete(portFilePath); } catch { /* best-effort */ }
        }
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
}
