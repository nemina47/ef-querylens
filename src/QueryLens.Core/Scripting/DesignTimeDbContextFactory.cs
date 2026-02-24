using System.Reflection;

namespace QueryLens.Core.Scripting;

/// <summary>
/// Pure-reflection helper that discovers and invokes an
/// <c>IDesignTimeDbContextFactory&lt;TContext&gt;</c> in the user's assemblies,
/// without requiring a reference to <c>Microsoft.EntityFrameworkCore.Design</c>.
/// </summary>
internal static class DesignTimeDbContextFactory
{
    private const string InterfaceName =
        "Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory`1";

    /// <summary>
    /// Searches <paramref name="assemblies"/> for a concrete type that
    /// implements <c>IDesignTimeDbContextFactory&lt;TContext&gt;</c> where the
    /// generic argument's full name matches <paramref name="dbContextType"/>.
    /// Uses full-name equality so discovery works regardless of which
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> the assembly was loaded into.
    /// </summary>
    /// <returns>
    /// A fresh DbContext instance returned by the factory, or <c>null</c>
    /// if no factory was found or if the factory threw during construction
    /// (callers should fall back to the bootstrap approach).
    /// </returns>
    internal static object? TryCreate(Type dbContextType, IEnumerable<Assembly> assemblies)
    {
        foreach (var asm in assemblies)
        {
            Type? factoryType;
            try
            {
                factoryType = asm.GetTypes().FirstOrDefault(t =>
                    !t.IsAbstract && !t.IsInterface &&
                    t.GetInterfaces().Any(i =>
                        i.IsGenericType &&
                        i.GetGenericTypeDefinition().FullName == InterfaceName &&
                        i.GetGenericArguments()[0].FullName == dbContextType.FullName));
            }
            catch { continue; } // ReflectionTypeLoadException on some assemblies

            if (factoryType is null) continue;

            try
            {
                var factory = Activator.CreateInstance(factoryType)!;
                var method  = factoryType.GetMethod("CreateDbContext")
                              ?? factoryType.GetInterfaces()
                                  .SelectMany(i => i.GetMethods())
                                  .FirstOrDefault(m => m.Name == "CreateDbContext");

                if (method is null) continue;

                return method.Invoke(factory, [Array.Empty<string>()]);
            }
            catch { return null; } // factory needs DI/env — skip, fall through to bootstrap
        }

        return null;
    }
}
