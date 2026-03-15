using System.Text.RegularExpressions;

namespace EFQueryLens.Lsp.Parsing;

public static partial class AssemblyResolver
{
    /// <summary>
    /// Returns true if the project directory contains a source file with an
    /// IQueryLensDbContextFactory implementation — i.e. the user explicitly set
    /// this project up as the QueryLens host.
    /// </summary>
    private static bool HasQueryLensFactory(string projectDir)
    {
        foreach (var file in EnumerateProjectSourceFiles(projectDir))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Contains("QueryLensDbContextFactory", StringComparison.OrdinalIgnoreCase))
                return true;

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
        var libraryCsprojName = Path.GetFileName(libraryCsprojPath);

        // Step 4a: Walk up to find the .sln file
        var slnDir = Path.GetDirectoryName(libraryCsprojPath);
        string? slnFile = null;

        while (!string.IsNullOrEmpty(slnDir))
        {
            var slnFiles = Directory.GetFiles(slnDir, "*.sln");
            if (slnFiles.Length > 0)
            {
                slnFile = slnFiles.First();
                debugLog += $"  -> Found solution: {Path.GetFileName(slnFile)}\n";
                break;
            }

            slnDir = Directory.GetParent(slnDir)?.FullName;
        }

        if (slnFile is null)
        {
            debugLog += "  -> EXCEPTION: No .sln file found.\n";
            return null;
        }

        // Step 4b: Parse the .sln to extract project paths
        var slnContent = File.ReadAllText(slnFile);
        var projectEntries = Regex.Matches(slnContent,
                @"Project\("".+?""\)\s*=\s*"".+?""\s*,\s*""(.+?\.csproj)""",
                RegexOptions.Multiline)
            .Select(m => Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(slnFile)!, m.Groups[1].Value)))
            .Where(p => File.Exists(p) && !string.Equals(p,
                Path.GetFullPath(libraryCsprojPath), StringComparison.OrdinalIgnoreCase))
            .ToList();

        debugLog += $"  -> Found {projectEntries.Count} other projects in solution\n";

        // Step 4c: Find executable projects in the solution. We do not require a direct
        // ProjectReference here because many host apps reference the target library
        // transitively (e.g. UI -> Infrastructure -> Application).
        var candidates = new List<(string CsprojPath, string AssemblyName)>();

        foreach (var projPath in projectEntries)
        {
            try
            {
                var content = File.ReadAllText(projPath);

                if (!IsExecutableProject(content))
                    continue;

                var exeAssemblyName = Path.GetFileNameWithoutExtension(projPath);
                var exeNameMatch = Regex.Match(content, @"<AssemblyName>(.+?)</AssemblyName>");
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

        if (candidates.Count == 0)
        {
            debugLog += "  -> EXCEPTION: No executable project references this library.\n";
            return null;
        }

        // Step 4d: Among candidates, find one whose bin folder contains the library DLL.
        // Prefer projects that explicitly contain a QueryLensDbContextFactory source file
        // (the user set them up as the QueryLens host) over projects that are merely
        // referencing the library for other purposes (e.g. data-migration workers).
        // Within the same tier, the most recently built DLL wins.
        var scored = new List<(string HostDll, DateTime Timestamp, bool HasFactory)>();

        foreach (var (csprojPath, exeAssemblyName) in candidates)
        {
            var projDir = Path.GetDirectoryName(csprojPath)!;
            var binDir = Path.Combine(projDir, "bin");

            if (!Directory.Exists(binDir))
            {
                debugLog += $"  -> {exeAssemblyName}: bin dir does not exist\n";
                continue;
            }

            // Look for the LIBRARY's DLL in the host's bin folder (proves it was built with the reference)
            var libraryDlls = Directory.GetFiles(binDir, $"{libraryAssemblyName}.dll", SearchOption.AllDirectories);
            if (libraryDlls.Length == 0)
            {
                debugLog += $"  -> {exeAssemblyName}: library DLL not found in bin\n";
                continue;
            }

            // Found it — now find the host's own DLL in the same tfm subfolder
            var libraryDll = libraryDlls.OrderByDescending(File.GetLastWriteTimeUtc).First();
            var tfmDir = Path.GetDirectoryName(libraryDll)!;

            var hostDll = Path.Combine(tfmDir, $"{exeAssemblyName}.dll");
            if (!File.Exists(hostDll))
            {
                debugLog += $"  -> {exeAssemblyName}: host DLL not found in {tfmDir}\n";
                continue;
            }

            var ts = File.GetLastWriteTimeUtc(hostDll);
            var hasFactory = HasQueryLensFactory(projDir);
            debugLog += $"  -> {exeAssemblyName}: found at {hostDll} (timestamp: {ts:u}, hasFactory: {hasFactory})\n";

            scored.Add((hostDll, ts, hasFactory));
        }

        var bestDll = scored
            .OrderByDescending(x => x.HasFactory ? 1 : 0)
            .ThenByDescending(x => x.Timestamp)
            .Select(x => x.HostDll)
            .FirstOrDefault();

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

    /// <summary>
    /// Searches the bin directory of a project for a DLL matching the assembly name.
    /// </summary>
    private static string? FindDllInBin(string projectDir, string assemblyName, ref string debugLog)
    {
        var binDir = Path.Combine(projectDir, "bin");
        debugLog += $"  -> Checking bin dir: {binDir}\n";

        if (!Directory.Exists(binDir))
        {
            debugLog += "  -> EXCEPTION: bin directory does not exist.\n";
            return null;
        }

        var dllFiles = Directory.GetFiles(binDir, $"{assemblyName}.dll", SearchOption.AllDirectories);
        if (dllFiles.Length > 0)
        {
            return dllFiles.OrderByDescending(File.GetLastWriteTimeUtc).First();
        }

        debugLog += $"  -> EXCEPTION: Searched for {assemblyName}.dll in {binDir} recursively but found 0 files.\n";
        return null;
    }
}
