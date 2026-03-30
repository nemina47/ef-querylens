using System.Reflection;

namespace EFQueryLens.Core.Scripting.DesignTime;

internal static partial class DesignTimeDbContextFactory
{
    /// <summary>
    /// Searches <paramref name="assemblies"/> for a concrete type that implements
    /// EF Core's <c>IDesignTimeDbContextFactory&lt;TContext&gt;</c> — the standard
    /// design-time factory used by <c>dotnet ef</c> migrations.
    /// </summary>
    /// <returns>
    /// A fresh DbContext instance returned by <c>CreateDbContext(string[])</c>,
    /// or <c>null</c> if no factory was found or construction failed.
    /// </returns>
    internal static object? TryCreateEfDesignTimeFactory(
        Type dbContextType, IEnumerable<Assembly> assemblies) =>
        TryCreateEfDesignTimeFactory(dbContextType, assemblies, null, out _);

    /// <summary>
    /// Same as <see cref="TryCreateEfDesignTimeFactory(Type, IEnumerable{Assembly})"/>,
    /// but limits discovery to factory types declared in
    /// <paramref name="requiredFactoryAssemblyPath"/> and returns a diagnostic
    /// message when a matching factory is found but invocation fails.
    /// </summary>
    internal static object? TryCreateEfDesignTimeFactory(
        Type dbContextType,
        IEnumerable<Assembly> assemblies,
        string? requiredFactoryAssemblyPath,
        out string? failureReason)
    {
        failureReason = null;
        var normalizedRequiredPath = NormalizeAssemblyPath(requiredFactoryAssemblyPath);

        foreach (var asm in assemblies)
        {
            Type? factoryType;
            try
            {
                factoryType = asm.GetTypes().FirstOrDefault(t =>
                    !t.IsAbstract && !t.IsInterface &&
                    t.GetInterfaces().Any(i =>
                        i.IsGenericType &&
                        i.GetGenericTypeDefinition().FullName == EfDesignTimeInterfaceName &&
                        i.GetGenericArguments()[0].FullName == dbContextType.FullName));

                // Duck-typing fallback: accept factories that expose a public
                // parameterless CreateDbContext(string[]) returning the target type.
                factoryType ??= asm.GetTypes().FirstOrDefault(t =>
                    !t.IsAbstract && !t.IsInterface &&
                    t.GetConstructor(Type.EmptyTypes) is not null &&
                    t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Any(m =>
                            m.Name == "CreateDbContext" &&
                            m.GetParameters().Length == 1 &&
                            m.GetParameters()[0].ParameterType == typeof(string[]) &&
                            m.ReturnType.FullName == dbContextType.FullName));
            }
            catch (ReflectionTypeLoadException rtle)
            {
                var loaderMessages = (rtle.LoaderExceptions ?? [])
                    .Where(e => e is not null)
                    .Select(e => e!.Message)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3);
                var loaderDetail = string.Join("; ", loaderMessages);
                failureReason ??= $"Could not scan '{asm.GetName().Name}' for EF design-time factory" +
                    (string.IsNullOrWhiteSpace(loaderDetail) ? "." : $": {loaderDetail}");
                continue;
            }
            catch { continue; }

            if (factoryType is null) continue;

            if (!IsFactoryAssemblyAllowed(
                    factoryType,
                    normalizedRequiredPath,
                    "EF design-time",
                    out var locationMismatch))
            {
                failureReason ??= locationMismatch;
                continue;
            }

            try
            {
                var factory = Activator.CreateInstance(factoryType)!;
                var matchingInterface = factoryType.GetInterfaces().FirstOrDefault(i =>
                    i.IsGenericType
                    && i.GetGenericTypeDefinition().FullName == EfDesignTimeInterfaceName
                    && i.GetGenericArguments()[0].FullName == dbContextType.FullName);

                var method = matchingInterface?.GetMethod("CreateDbContext")
                             ?? factoryType.GetMethod("CreateDbContext",
                                 BindingFlags.Public | BindingFlags.Instance,
                                 null,
                                 [typeof(string[])],
                                 null)
                             ?? factoryType.GetInterfaces()
                                 .SelectMany(i => i.GetMethods())
                                 .FirstOrDefault(m =>
                                     m.Name == "CreateDbContext"
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType == typeof(string[])
                                     && m.ReturnType.FullName == dbContextType.FullName);

                if (method is null) continue;

                return method.Invoke(factory, [Array.Empty<string>()]);
            }
            catch (Exception ex)
            {
                failureReason =
                    $"Found EF design-time factory '{factoryType.FullName}' but CreateDbContext() failed: {Unwrap(ex)}";
                return null;
            }
        }

        return null;
    }
}
