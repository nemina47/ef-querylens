using System.Text.Json;

namespace EFQueryLens.Core.AssemblyContext;

public sealed partial class ProjectAssemblyContext
{
    private sealed partial class IsolatedLoadContext
    {
        private static string[] BuildSharedFrameworkProbeDirs(string assemblyPath)
        {
            var frameworkRequests = ReadRuntimeFrameworkRequests(assemblyPath);
            EnsureBaselineFrameworkRequests(frameworkRequests);

            var roots = GetDotnetRoots();
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                var sharedRoot = Path.Combine(root, "shared");
                if (!Directory.Exists(sharedRoot))
                    continue;

                foreach (var req in frameworkRequests)
                {
                    var frameworkBase = Path.Combine(sharedRoot, req.Name);
                    var selected = SelectFrameworkVersionDir(frameworkBase, req.Version);
                    if (!string.IsNullOrEmpty(selected))
                        dirs.Add(selected);
                }
            }

            return dirs.ToArray();
        }

        private static void EnsureBaselineFrameworkRequests(ICollection<(string Name, string? Version)> frameworkRequests)
        {
            // Resolve core runtime assemblies from Microsoft.NETCore.App.
            var netCoreVersion = frameworkRequests
                .FirstOrDefault(f =>
                    string.Equals(f.Name, "Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
                .Version;

            if (!frameworkRequests.Any(f =>
                    string.Equals(f.Name, "Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase)))
            {
                frameworkRequests.Add(("Microsoft.NETCore.App", null));
            }

            // Some user projects (especially class-library outputs that need synthetic runtimeconfig)
            // don't list AspNetCore even though factory code depends on Microsoft.Extensions.* assemblies.
            // Probe AspNetCore shared framework too, preferring the same runtime train as NETCore when known.
            if (!frameworkRequests.Any(f =>
                    string.Equals(f.Name, "Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase)))
            {
                frameworkRequests.Add(("Microsoft.AspNetCore.App", netCoreVersion));
            }
        }

        private static List<(string Name, string? Version)> ReadRuntimeFrameworkRequests(string assemblyPath)
        {
            var result = new List<(string Name, string? Version)>();
            var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
            if (!File.Exists(runtimeConfigPath))
                return result;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
                if (!doc.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions))
                    return result;

                if (runtimeOptions.TryGetProperty("framework", out var frameworkObj))
                    TryReadFramework(frameworkObj, result);

                if (runtimeOptions.TryGetProperty("frameworks", out var frameworksArray)
                    && frameworksArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var framework in frameworksArray.EnumerateArray())
                        TryReadFramework(framework, result);
                }
            }
            catch
            {
                // Best-effort runtime probing only.
            }

            return result;
        }

        private static void TryReadFramework(
            JsonElement framework,
            ICollection<(string Name, string? Version)> target)
        {
            if (!framework.TryGetProperty("name", out var nameProp)
                || nameProp.ValueKind != JsonValueKind.String)
                return;

            var name = nameProp.GetString();
            if (string.IsNullOrWhiteSpace(name))
                return;

            string? version = null;
            if (framework.TryGetProperty("version", out var versionProp)
                && versionProp.ValueKind == JsonValueKind.String)
                version = versionProp.GetString();

            target.Add((name, version));
        }

        private static string? SelectFrameworkVersionDir(string frameworkBasePath, string? requestedVersion)
        {
            if (!Directory.Exists(frameworkBasePath))
                return null;

            if (!string.IsNullOrWhiteSpace(requestedVersion))
            {
                var exact = Path.Combine(frameworkBasePath, requestedVersion);
                if (Directory.Exists(exact))
                    return exact;
            }

            var candidates = Directory.EnumerateDirectories(frameworkBasePath)
                .Select(d => new
                {
                    Path = d,
                    Name = Path.GetFileName(d),
                })
                .Select(x => new
                {
                    x.Path,
                    Parsed = Version.TryParse(x.Name, out var v) ? v : null,
                })
                .Where(x => x.Parsed is not null)
                .ToList();

            if (candidates.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(requestedVersion)
                && Version.TryParse(requestedVersion, out var requested))
            {
                var sameTrain = candidates
                    .Where(c => c.Parsed!.Major == requested.Major && c.Parsed.Minor == requested.Minor)
                    .OrderByDescending(c => c.Parsed)
                    .FirstOrDefault();

                if (sameTrain is not null)
                    return sameTrain.Path;
            }

            return candidates
                .OrderByDescending(c => c.Parsed)
                .First().Path;
        }

        private static IReadOnlyCollection<string> GetDotnetRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
                roots.Add(envRoot);

            var envRootX86 = Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)");
            if (!string.IsNullOrWhiteSpace(envRootX86) && Directory.Exists(envRootX86))
                roots.Add(envRootX86);

            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                var processDir = Path.GetDirectoryName(Environment.ProcessPath);
                if (!string.IsNullOrWhiteSpace(processDir) && Directory.Exists(processDir))
                    roots.Add(processDir);
            }

            if (OperatingSystem.IsWindows())
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var defaultRoot = Path.Combine(programFiles, "dotnet");
                if (Directory.Exists(defaultRoot))
                    roots.Add(defaultRoot);

                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var defaultRootX86 = Path.Combine(programFilesX86, "dotnet");
                if (Directory.Exists(defaultRootX86))
                    roots.Add(defaultRootX86);
            }

            return roots;
        }
    }
}
