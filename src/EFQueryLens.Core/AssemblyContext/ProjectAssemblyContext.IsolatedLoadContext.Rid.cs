using System.Runtime.InteropServices;

namespace EFQueryLens.Core.AssemblyContext;

public sealed partial class ProjectAssemblyContext
{
    private sealed partial class IsolatedLoadContext
    {
        internal string? TryResolveRidRuntimeAssemblyPath(string assemblySimpleName)
        {
            if (string.IsNullOrWhiteSpace(assemblySimpleName))
                return null;

            var runtimesRoot = Path.Combine(_assemblyDirectory, "runtimes");
            if (!Directory.Exists(runtimesRoot))
                return null;

            var fileName = assemblySimpleName + ".dll";
            var candidates = new List<(string Path, int RidScore, int TfmScore)>();

            try
            {
                foreach (var path in Directory.EnumerateFiles(runtimesRoot, fileName, SearchOption.AllDirectories))
                {
                    var rid = TryExtractRid(path);
                    if (string.IsNullOrWhiteSpace(rid))
                        continue;

                    var tfm = TryExtractTfm(path);
                    candidates.Add((
                        path,
                        GetRidScore(rid),
                        GetTfmScore(tfm)));
                }
            }
            catch
            {
                return null;
            }

            if (candidates.Count == 0)
                return null;

            return candidates
                .OrderBy(c => c.RidScore)
                .ThenByDescending(c => c.TfmScore)
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.Path)
                .FirstOrDefault();
        }

        private int GetRidScore(string rid)
        {
            for (var i = 0; i < _runtimeRidProbeOrder.Length; i++)
            {
                if (string.Equals(_runtimeRidProbeOrder[i], rid, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return int.MaxValue;
        }

        private static int GetTfmScore(string? tfm)
        {
            if (string.IsNullOrWhiteSpace(tfm))
                return 0;

            if (tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            {
                var versionText = tfm["netstandard".Length..];
                if (Version.TryParse(versionText, out var parsed))
                    return 1000 + (parsed.Major * 10) + parsed.Minor;
                return 1000;
            }

            if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            {
                var versionText = tfm[3..];
                if (Version.TryParse(versionText, out var parsed))
                    return 2000 + (parsed.Major * 10) + parsed.Minor;
                if (int.TryParse(versionText, out var majorOnly))
                    return 2000 + (majorOnly * 10);
                return 2000;
            }

            return 0;
        }

        private static string? TryExtractRid(string path)
        {
            var normalized = path.Replace('\\', '/');
            const string marker = "/runtimes/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return null;

            var start = markerIndex + marker.Length;
            var end = normalized.IndexOf('/', start);
            if (end <= start)
                return null;

            return normalized[start..end];
        }

        private static string? TryExtractTfm(string path)
        {
            var normalized = path.Replace('\\', '/');
            const string marker = "/lib/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return null;

            var start = markerIndex + marker.Length;
            var end = normalized.IndexOf('/', start);
            if (end <= start)
                return null;

            return normalized[start..end];
        }

        private static string[] BuildRuntimeRidProbeOrder()
        {
            var rids = new List<string>();

            if (OperatingSystem.IsWindows())
            {
                AddArchRid(rids, "win");
                rids.Add("win");
            }
            else if (OperatingSystem.IsLinux())
            {
                AddArchRid(rids, "linux");
                rids.Add("linux");
                rids.Add("unix");
            }
            else if (OperatingSystem.IsMacOS())
            {
                AddArchRid(rids, "osx");
                rids.Add("osx");
                rids.Add("unix");
            }
            else
            {
                rids.Add("unix");
            }

            return rids
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void AddArchRid(ICollection<string> rids, string baseRid)
        {
            var archRid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => baseRid + "-x64",
                Architecture.X86 => baseRid + "-x86",
                Architecture.Arm64 => baseRid + "-arm64",
                Architecture.Arm => baseRid + "-arm",
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(archRid))
                rids.Add(archRid);
        }
    }
}
