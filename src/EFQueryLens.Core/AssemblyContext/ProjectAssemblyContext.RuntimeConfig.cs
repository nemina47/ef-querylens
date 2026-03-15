namespace EFQueryLens.Core.AssemblyContext;

public sealed partial class ProjectAssemblyContext
{
    /// <summary>
    /// Class libraries do not generate a .runtimeconfig.dev.json file by default.
    /// AssemblyDependencyResolver requires this file to know where the NuGet package
    /// cache is located. If it is missing, we generate a dummy one pointing to the
    /// standard NuGet cache so that all third-party dependencies can be resolved.
    /// </summary>
    private static void EnsureRuntimeConfigDevExists(string assemblyPath)
    {
        var runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        var devConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.dev.json");

        if (!File.Exists(runtimeConfigPath))
        {
            try
            {
                // Create a generic runtimeconfig.json so AssemblyDependencyResolver doesn't abort.
                // It just needs to exist for the dev.json to be processed.
                var baseJson = """
                               {
                                 "runtimeOptions": {
                                   "tfm": "net8.0",
                                                                     "frameworks": [
                                                                         {
                                                                             "name": "Microsoft.NETCore.App",
                                                                             "version": "8.0.0"
                                                                         },
                                                                         {
                                                                             "name": "Microsoft.AspNetCore.App",
                                                                             "version": "8.0.0"
                                                                         }
                                                                     ]
                                 }
                               }
                               """;
                File.WriteAllText(runtimeConfigPath, baseJson);
            }
            catch
            {
            }
        }

        if (!File.Exists(devConfigPath))
        {
            try
            {
                var nugetCache = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
                if (string.IsNullOrEmpty(nugetCache))
                {
                    nugetCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".nuget", "packages");
                }

                nugetCache = nugetCache.Replace("\\", "\\\\");

                var devJson = $$"""
                                {
                                  "runtimeOptions": {
                                    "additionalProbingPaths": [
                                      "{{nugetCache}}"
                                    ]
                                  }
                                }
                                """;

                File.WriteAllText(devConfigPath, devJson);
            }
            catch
            {
            }
        }
    }
}
