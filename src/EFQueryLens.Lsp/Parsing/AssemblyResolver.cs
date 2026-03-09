using System.Text.RegularExpressions;

namespace EFQueryLens.Lsp.Parsing;

/// <summary>
/// Resolves the target assembly (.dll) for a given C# source file.
///
/// For executable projects (Console / Web), it looks in the project's own bin folder.
/// For class library projects, it walks up to the .sln file, finds an executable project
/// that references the library, and uses its bin folder instead — because class libraries
/// don't copy NuGet dependencies to their own output directory.
/// </summary>
public static class AssemblyResolver
{
    /// <summary>
    /// Walks up the directory tree from the given source file path to find the nearest
    /// .csproj file, determines if it's executable or a class library, and resolves the
    /// correct assembly path accordingly.
    /// </summary>
    public static string? TryGetTargetAssembly(string sourceFilePath)
    {
        var debugLog = $"Started at: {sourceFilePath}\n";
        var currentDir = Path.GetDirectoryName(sourceFilePath);

        // Step 1: Find the nearest .csproj
        string? csprojFile = null;
        string? projectDir = null;

        while (!string.IsNullOrEmpty(currentDir))
        {
            debugLog += $"- Checking dir: {currentDir}\n";
            var csprojFiles = Directory.GetFiles(currentDir, "*.csproj")
                .Where(f => !f.Contains("Backup", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (csprojFiles.Count > 0)
            {
                csprojFile = csprojFiles.First();
                projectDir = currentDir;
                debugLog += $"  -> Found csproj: {Path.GetFileName(csprojFile)}\n";
                break;
            }

            currentDir = Directory.GetParent(currentDir)?.FullName;
        }

        if (csprojFile is null || projectDir is null)
            return $"DEBUG_FAIL: Walked to root, no csproj found.\n{debugLog}";

        // Step 2: Extract the assembly name
        var assemblyName = Path.GetFileNameWithoutExtension(csprojFile);
        var csprojContent = File.ReadAllText(csprojFile);

        var nameMatch = Regex.Match(csprojContent, @"<AssemblyName>(.+?)</AssemblyName>");
        if (nameMatch.Success)
        {
            assemblyName = nameMatch.Groups[1].Value.Trim();
            debugLog += $"  -> AssemblyName overridden to: {assemblyName}\n";
        }

        // Step 3: Detect if this is an executable project
        if (IsExecutableProject(csprojContent))
        {
            debugLog += "  -> Project is executable, using own bin dir\n";
            return FindDllInBin(projectDir, assemblyName, ref debugLog)
                   ?? $"DEBUG_FAIL:\n{debugLog}";
        }

        // Step 4: It's a class library — find a host executable project
        debugLog += "  -> Project is a class library, searching for host executable...\n";
        return FindHostExecutableAssembly(csprojFile, assemblyName, ref debugLog)
               ?? $"DEBUG_FAIL:\n{debugLog}";
    }

    /// <summary>
    /// Determines if a .csproj is an executable project (console app or web app).
    /// </summary>
    private static bool IsExecutableProject(string csprojContent)
    {
        // Web SDK projects are always executable
        if (csprojContent.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
            csprojContent.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check for explicit OutputType
        var outputTypeMatch = Regex.Match(csprojContent, @"<OutputType>(\w+)</OutputType>", RegexOptions.IgnoreCase);
        if (outputTypeMatch.Success)
        {
            var outputType = outputTypeMatch.Groups[1].Value;
            return outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
                   outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Finds a host executable project that references the given class library.
    /// Strategy:
    ///   1. Walk up to find the .sln file
    ///   2. Parse the .sln to find all project paths
    ///   3. For each executable project, check if it references the class library
    ///   4. Among matching projects, pick the one whose bin folder contains the library DLL
    ///      and has the most recent build timestamp
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

        // Step 4c: Find executable projects that reference the class library
        var candidates = new List<(string CsprojPath, string AssemblyName)>();

        foreach (var projPath in projectEntries)
        {
            try
            {
                var content = File.ReadAllText(projPath);

                if (!IsExecutableProject(content))
                    continue;

                // Check if this project references the library (directly)
                if (!content.Contains(libraryCsprojName, StringComparison.OrdinalIgnoreCase))
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

        // Step 4d: Among candidates, find one whose bin folder contains the library DLL
        //          and pick the most recently built one
        string? bestDll = null;
        DateTime bestTimestamp = DateTime.MinValue;

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

            // Found it! Now find the host's own DLL in the same tfm subfolder
            var libraryDll = libraryDlls.OrderByDescending(File.GetLastWriteTimeUtc).First();
            var tfmDir = Path.GetDirectoryName(libraryDll)!;

            var hostDll = Path.Combine(tfmDir, $"{exeAssemblyName}.dll");
            if (!File.Exists(hostDll))
            {
                debugLog += $"  -> {exeAssemblyName}: host DLL not found in {tfmDir}\n";
                continue;
            }

            var ts = File.GetLastWriteTimeUtc(hostDll);
            debugLog += $"  -> {exeAssemblyName}: found at {hostDll} (timestamp: {ts:u})\n";

            if (ts > bestTimestamp)
            {
                bestDll = hostDll;
                bestTimestamp = ts;
            }
        }

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
