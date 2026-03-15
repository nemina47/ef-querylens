using EFQueryLens.Core;
using EFQueryLens.Core.Daemon;

namespace EFQueryLens.DaemonClient;

public sealed partial class DaemonBackedEngine
{
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
