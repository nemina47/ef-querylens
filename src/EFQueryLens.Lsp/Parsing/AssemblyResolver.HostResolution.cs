using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EFQueryLens.Lsp.Parsing;

public static partial class AssemblyResolver
{
    [GeneratedRegex(@"IQueryLensDbContextFactory\s*<\s*([\w.]+)\s*>", RegexOptions.None, 1000)]
    private static partial Regex QueryLensFactoryTypeRegex();

    [GeneratedRegex(@"Project\("".+?""\)\s*=\s*"".+?""\s*,\s*""(.+?\.csproj)""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectRegex();

    private sealed record CandidateAssembly(
        string DllPath,
        DateTime TimestampUtc,
        bool HasFactory,
        bool HasRuntimeArtifacts,
        bool LooksLikeRefOrObj);

    /// <summary>
    /// Scans the host project's source files for an <c>IQueryLensDbContextFactory&lt;T&gt;</c>
    /// declaration and returns the concrete DbContext type name <c>T</c>.
    ///
    /// This is the authoritative way to resolve the DbContext type: the type parameter is set
    /// explicitly by the user and is always the concrete <see cref="DbContext"/> subclass —
    /// regardless of how the context is injected elsewhere (e.g. via an interface).
    ///
    /// Returns <c>null</c> when no factory declaration is found, the project directory cannot
    /// be derived from <paramref name="assemblyDllPath"/>, or any I/O error occurs.
    /// </summary>
    internal static string? TryExtractDbContextTypeFromFactory(string assemblyDllPath)
    {
        var candidates = TryExtractDbContextTypeNamesFromFactories(assemblyDllPath);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    internal static IReadOnlyList<string> TryExtractDbContextTypeNamesFromFactories(string assemblyDllPath)
    {
        // Derive the project source root from the DLL path by walking up until we find a .csproj.
        // Standard layout: {projectDir}/bin/{config}/{tfm}/Assembly.dll
        var dir = Path.GetDirectoryName(assemblyDllPath);
        while (!string.IsNullOrEmpty(dir))
        {
            try
            {
                if (Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
                    break;
            }
            catch { /* continue */ }

            dir = Path.GetDirectoryName(dir);
        }

        if (string.IsNullOrEmpty(dir))
            return [];

        var results = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in EnumerateProjectSourceFiles(dir))
        {
            try
            {
                var text = File.ReadAllText(file);
                var matches = QueryLensFactoryTypeRegex().Matches(text);

                foreach (Match match in matches)
                {
                    if (match.Success)
                        results.Add(match.Groups[1].Value.Trim());
                }
            }
            catch { /* ignore unreadable files */ }
        }

        return results.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Returns true if the project directory contains a source file with an
    /// IQueryLensDbContextFactory implementation — i.e. the user explicitly set
    /// this project up as the QueryLens host.
    /// </summary>
    private static bool HasQueryLensFactory(string projectDir)
    {
        foreach (var file in EnumerateProjectSourceFiles(projectDir))
        {
            try
            {
                var text = File.ReadAllText(file);
                if (text.Contains("IQueryLensDbContextFactory<", StringComparison.Ordinal))
                    return true;
            }
            catch
            {
                // Ignore unreadable files and continue scanning.
            }
        }

        return false;
    }

    /// <summary>
    /// Enumerates user source files for a project while skipping generated/output folders
    /// so scanning remains deterministic and resilient on large solutions.
    /// </summary>
    private static IEnumerable<string> EnumerateProjectSourceFiles(string projectDir)
    {
        var pending = new Stack<string>();
        pending.Push(projectDir);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*.cs", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                files = [];
            }

            foreach (var file in files)
                yield return file;

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch
            {
                directories = [];
            }

            foreach (var dir in directories)
            {
                var name = Path.GetFileName(dir);
                if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pending.Push(dir);
            }
        }
    }

    /// <summary>
    /// Finds a host executable project that references the given class library.
    /// Strategy:
    ///   1. Walk up to find the .sln file
    ///   2. Parse the .sln to find all project paths
    ///   3. For each executable project, check if it references the class library
    ///   4. Among matching projects, prefer projects that contain a QueryLens factory
    ///      implementation; use most-recent build timestamp as a tiebreaker
    /// </summary>
    private static string? FindHostExecutableAssembly(
        string libraryCsprojPath,
        string libraryAssemblyName,
        ref string debugLog)
    {
        if (!TryFindNearestSolution(libraryCsprojPath, ref debugLog, out var slnFile, out var isSlnx)
            || slnFile is null)
        {
            debugLog += "  -> EXCEPTION: No .sln or .slnx file found.\n";
            return null;
        }

        var projectEntries = GetSolutionProjectEntries(
            slnFile,
            isSlnx,
            libraryCsprojPath,
            ref debugLog);

        debugLog += $"  -> Found {projectEntries.Count} other projects in solution\n";

        // Step 4c: Find executable projects in the solution. We do not require a direct
        // ProjectReference here because many host apps reference the target library
        // transitively (e.g. UI -> Infrastructure -> Application).
        var candidates = FindExecutableHostCandidates(projectEntries, ref debugLog);

        if (candidates.Count == 0)
        {
            debugLog += "  -> EXCEPTION: No executable project references this library.\n";
            return null;
        }

        // Step 4d: Among candidates, find one whose output contains the library DLL.
        // Prefer projects that explicitly contain a QueryLensDbContextFactory source file
        // (the user set them up as the QueryLens host) over projects that are merely
        // referencing the library for other purposes (e.g. data-migration workers).
        // Within the same tier, the most recently built DLL wins.
        var scored = ScoreHostCandidates(candidates, libraryAssemblyName, ref debugLog);
        var bestDll = SelectBestHostAssemblyPath(scored);

        if (bestDll is not null)
        {
            debugLog += $"  -> Selected host assembly: {bestDll}\n";
        }
        else
        {
            debugLog += "  -> EXCEPTION: No candidate host project has a built bin folder containing the library.\n";
        }

        return bestDll;
    }

    private static bool TryFindNearestSolution(
        string libraryCsprojPath,
        ref string debugLog,
        out string? solutionPath,
        out bool isSlnx)
    {
        var slnDir = Path.GetDirectoryName(libraryCsprojPath);
        solutionPath = null;
        isSlnx = false;

        while (!string.IsNullOrEmpty(slnDir))
        {
            var slnFiles = Directory.GetFiles(slnDir, "*.sln");
            if (slnFiles.Length > 0)
            {
                solutionPath = slnFiles.First();
                debugLog += $"  -> Found solution: {Path.GetFileName(solutionPath)}\n";
                return true;
            }

            var slnxFiles = Directory.GetFiles(slnDir, "*.slnx");
            if (slnxFiles.Length > 0)
            {
                solutionPath = slnxFiles.First();
                isSlnx = true;
                debugLog += $"  -> Found solution (slnx): {Path.GetFileName(solutionPath)}\n";
                return true;
            }

            slnDir = Directory.GetParent(slnDir)?.FullName;
        }

        return false;
    }

    private static List<string> GetSolutionProjectEntries(
        string solutionPath,
        bool isSlnx,
        string libraryCsprojPath,
        ref string debugLog)
    {
        if (isSlnx)
            return ParseSlnxProjectPaths(solutionPath, libraryCsprojPath, ref debugLog);

        var slnContent = File.ReadAllText(solutionPath);
        return SlnProjectRegex().Matches(slnContent)
            .Select(m => m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))
            .Select(relativePath => Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(solutionPath)!, relativePath)))
            .Where(p => File.Exists(p) && !string.Equals(p,
                Path.GetFullPath(libraryCsprojPath), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<(string CsprojPath, string AssemblyName)> FindExecutableHostCandidates(
        IReadOnlyList<string> projectEntries,
        ref string debugLog)
    {
        var candidates = new List<(string CsprojPath, string AssemblyName)>();

        foreach (var projPath in projectEntries)
        {
            try
            {
                var content = File.ReadAllText(projPath);
                if (!IsExecutableProject(content))
                    continue;

                var exeAssemblyName = Path.GetFileNameWithoutExtension(projPath);
                var exeNameMatch = AssemblyNameRegex().Match(content);
                if (exeNameMatch.Success)
                    exeAssemblyName = exeNameMatch.Groups[1].Value.Trim();

                candidates.Add((projPath, exeAssemblyName));
                debugLog += $"  -> Candidate host: {Path.GetFileName(projPath)} (assembly: {exeAssemblyName})\n";
            }
            catch
            {
                // Skip unreadable projects
            }
        }

        return candidates;
    }

    private static List<CandidateAssembly> ScoreHostCandidates(
        IReadOnlyList<(string CsprojPath, string AssemblyName)> candidates,
        string libraryAssemblyName,
        ref string debugLog)
    {
        var scored = new List<CandidateAssembly>();

        foreach (var (csprojPath, exeAssemblyName) in candidates)
        {
            var projDir = Path.GetDirectoryName(csprojPath)!;

            // Resolve all output paths for this host executable (bin glob + MSBuild query path)
            var hostDllPaths = FindProjectOutputDllPaths(csprojPath, exeAssemblyName, ref debugLog);
            if (hostDllPaths.Count == 0)
            {
                debugLog += $"  -> {exeAssemblyName}: no output DLL found\n";
                continue;
            }

            var hasFactory = HasQueryLensFactory(projDir);

            foreach (var hostDll in hostDllPaths)
            {
                // Verify the library DLL is co-located, proving this host was built with the reference
                var tfmDir = Path.GetDirectoryName(hostDll)!;
                var libraryDll = Path.Combine(tfmDir, $"{libraryAssemblyName}.dll");
                if (!File.Exists(libraryDll))
                {
                    debugLog += $"  -> {exeAssemblyName}: host DLL found at {hostDll} but library DLL not alongside\n";
                    continue;
                }

                var ts = File.GetLastWriteTimeUtc(hostDll);
                var hasRuntimeArtifacts = HasExecutableRuntimeArtifacts(hostDll);
                var looksLikeRefOrObj = LooksLikeRefOrObjPath(hostDll);

                debugLog +=
                    $"  -> {exeAssemblyName}: found at {hostDll} (timestamp: {ts:u}, hasFactory: {hasFactory}, " +
                    $"hasRuntimeArtifacts: {hasRuntimeArtifacts}, looksLikeRefOrObj: {looksLikeRefOrObj})\n";

                scored.Add(new CandidateAssembly(
                    hostDll,
                    ts,
                    hasFactory,
                    hasRuntimeArtifacts,
                    looksLikeRefOrObj));
            }
        }

        return scored;
    }

    private static string? SelectBestHostAssemblyPath(IReadOnlyList<CandidateAssembly> scored)
    {
        return scored
            .GroupBy(x => x.DllPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(x => x.HasFactory ? 1 : 0)
                .ThenByDescending(x => x.HasRuntimeArtifacts ? 1 : 0)
                .ThenByDescending(x => x.LooksLikeRefOrObj ? 0 : 1)
                .ThenByDescending(x => x.TimestampUtc)
                .First())
            .OrderByDescending(x => x.HasFactory ? 1 : 0)
            .ThenByDescending(x => x.HasRuntimeArtifacts ? 1 : 0)
            .ThenByDescending(x => x.LooksLikeRefOrObj ? 0 : 1)
            .ThenByDescending(x => x.TimestampUtc)
            .Select(x => x.DllPath)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns all candidate output DLL paths for a project, trying the bin/ folder first
    /// and then using an MSBuild TargetPath query for non-standard layouts such as
    /// UseArtifactsOutput=true.
    /// </summary>
    private static List<string> FindProjectOutputDllPaths(
        string csprojPath,
        string assemblyName,
        ref string debugLog)
    {
        var projectDir = Path.GetDirectoryName(csprojPath)!;
        var binDir = Path.Combine(projectDir, "bin");

        if (Directory.Exists(binDir))
        {
            var dllFiles = Directory.GetFiles(binDir, $"{assemblyName}.dll", SearchOption.AllDirectories);
            if (dllFiles.Length > 0)
            {
                debugLog += $"  -> Found {dllFiles.Length} bin candidate(s) for {assemblyName}\n";
                return [.. dllFiles];
            }

            debugLog += $"  -> {assemblyName}: not found in bin dir, trying MSBuild\n";
        }
        else
        {
            debugLog += $"  -> {assemblyName}: bin dir does not exist, trying MSBuild\n";
        }

        var msBuildDll = TryResolveDllViaMsBuild(csprojPath, ref debugLog);
        return msBuildDll is not null ? [msBuildDll] : [];
    }

    /// <summary>
    /// Picks the single best DLL from a list of candidates using runtime-artifact presence,
    /// ref/obj path detection, and last-write timestamp as tiebreakers.
    /// </summary>
    private static string? SelectBestDll(List<string> paths, ref string debugLog)
    {
        if (paths.Count == 0)
            return null;

        var selected = paths
            .Select(path => new CandidateAssembly(
                path,
                File.GetLastWriteTimeUtc(path),
                HasFactory: false,
                HasRuntimeArtifacts: HasExecutableRuntimeArtifacts(path),
                LooksLikeRefOrObj: LooksLikeRefOrObjPath(path)))
            .OrderByDescending(x => x.HasRuntimeArtifacts ? 1 : 0)
            .ThenByDescending(x => x.LooksLikeRefOrObj ? 0 : 1)
            .ThenByDescending(x => x.TimestampUtc)
            .First();

        debugLog +=
            $"  -> Selected {selected.DllPath} " +
            $"(hasRuntimeArtifacts: {selected.HasRuntimeArtifacts}, looksLikeRefOrObj: {selected.LooksLikeRefOrObj}, timestamp: {selected.TimestampUtc:u})\n";

        return selected.DllPath;
    }

    /// <summary>
    /// Queries MSBuild for the TargetPath property of a project without triggering a build.
    /// Handles non-standard output layouts such as UseArtifactsOutput=true.
    /// </summary>
    private static string? TryResolveDllViaMsBuild(string csprojPath, ref string debugLog)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add(csprojPath);
            psi.ArgumentList.Add("-getProperty:TargetPath");

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(15_000);

            if (process.ExitCode == 0 && !string.IsNullOrEmpty(output) && File.Exists(output))
            {
                debugLog += $"  -> MSBuild TargetPath resolved: {output}\n";
                return output;
            }

            debugLog += $"  -> MSBuild TargetPath query failed (exit: {process.ExitCode})\n";
        }
        catch (Exception ex)
        {
            debugLog += $"  -> MSBuild TargetPath exception: {ex.Message}\n";
        }

        return null;
    }

    private static bool HasExecutableRuntimeArtifacts(string dllPath)
    {
        var runtimeConfigPath = Path.ChangeExtension(dllPath, ".runtimeconfig.json");
        var depsPath = Path.ChangeExtension(dllPath, ".deps.json");
        return File.Exists(runtimeConfigPath) && File.Exists(depsPath);
    }

    private static bool LooksLikeRefOrObjPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/ref/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses a <c>.slnx</c> file (XML format introduced in VS 17.10 / .NET 9 SDK) and
    /// returns the absolute paths of all <c>.csproj</c> files referenced by it, excluding
    /// the library project itself.
    /// <para>
    /// .slnx schema: <c>&lt;Solution&gt; &lt;Project Path="relative/path/to.csproj" /&gt; …</c>
    /// </para>
    /// </summary>
    private static List<string> ParseSlnxProjectPaths(
        string slnxPath,
        string excludeCsprojPath,
        ref string debugLog)
    {
        var slnxDir = Path.GetDirectoryName(slnxPath)!;
        var normalizedExclude = Path.GetFullPath(excludeCsprojPath);
        var results = new List<string>();

        try
        {
            var doc = XDocument.Load(slnxPath);
            foreach (var element in doc.Descendants())
            {
                if (!element.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
                    continue;

                var pathAttr = element.Attribute("Path")?.Value;
                if (string.IsNullOrWhiteSpace(pathAttr)
                    || !pathAttr.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    continue;

                var absolutePath = Path.GetFullPath(
                    Path.Combine(slnxDir, pathAttr.Replace('\\', Path.DirectorySeparatorChar)));

                if (!File.Exists(absolutePath))
                    continue;

                if (string.Equals(absolutePath, normalizedExclude, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(absolutePath);
            }
        }
        catch (Exception ex)
        {
            debugLog += $"  -> Failed to parse .slnx: {ex.Message}\n";
        }

        debugLog += $"  -> Found {results.Count} other projects in .slnx\n";
        return results;
    }
}
