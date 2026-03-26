using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using EFQueryLens.Lsp;

namespace EFQueryLens.Lsp.Parsing;

/// <summary>
/// Resolves the target assembly (.dll) for a given C# source file.
///
/// For executable projects (Console / Web), it looks in the project's own bin folder.
/// For class library projects, it walks up to the .sln file, finds an executable project
/// that references the library, and uses its bin folder instead — because class libraries
/// don't copy NuGet dependencies to their own output directory.
/// </summary>
public static partial class AssemblyResolver
{
    private sealed record CachedAssemblySelection(string TargetAssemblyPath, long ExpiresAtUtcTicks);

    private static readonly ConcurrentDictionary<string, CachedAssemblySelection> TargetAssemblyCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly int TargetAssemblyCacheTtlMs = LspEnvironment.ReadInt(
        "QUERYLENS_ASSEMBLY_RESOLVER_CACHE_TTL_MS",
        fallback: 5_000,
        min: 0,
        max: 120_000);

    /// <summary>
    /// Walks up the directory tree from the given source file path to find the nearest
    /// .csproj file, determines if it's executable or a class library, and resolves the
    /// correct assembly path accordingly.
    /// </summary>
    public static string? TryGetTargetAssembly(string sourceFilePath)
    {
        var normalizedSourceFilePath = Path.GetFullPath(sourceFilePath);
        if (TryGetCachedTargetAssembly(normalizedSourceFilePath, out var cachedTargetAssembly))
        {
            return cachedTargetAssembly;
        }

        var debugLog = $"Started at: {normalizedSourceFilePath}\n";
        var currentDir = Path.GetDirectoryName(normalizedSourceFilePath);

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
            debugLog += "  -> Project is executable, using own output dir\n";
            var candidates = FindProjectOutputDllPaths(csprojFile!, assemblyName, ref debugLog);
            var resolved = SelectBestDll(candidates, ref debugLog)
                           ?? $"DEBUG_FAIL:\n{debugLog}";
            CacheTargetAssembly(normalizedSourceFilePath, resolved);
            return resolved;
        }

        // Step 4: It is a class library — find a host executable project
        debugLog += "  -> Project is a class library, searching for host executable...\n";
        var hostResolved = FindHostExecutableAssembly(csprojFile, assemblyName, ref debugLog)
                           ?? $"DEBUG_FAIL:\n{debugLog}";
        CacheTargetAssembly(normalizedSourceFilePath, hostResolved);
        return hostResolved;
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

}
