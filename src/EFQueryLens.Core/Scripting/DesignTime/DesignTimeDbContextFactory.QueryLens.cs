using System.Reflection;

namespace EFQueryLens.Core.Scripting.DesignTime;

internal static partial class DesignTimeDbContextFactory
{
    /// <summary>
    /// Searches <paramref name="assemblies"/> for a concrete type that implements
    /// <c>IQueryLensDbContextFactory&lt;TContext&gt;</c> — the QueryLens-native
    /// factory interface.
    /// </summary>
    /// <returns>
    /// A fresh DbContext instance returned by <c>CreateOfflineContext()</c>,
    /// or <c>null</c> if no factory was found or construction failed.
    /// </returns>
    internal static object? TryCreateQueryLensFactory(
        Type dbContextType, IEnumerable<Assembly> assemblies) =>
        TryCreateQueryLensFactory(dbContextType, assemblies, null, out _);

    /// <summary>
    /// Same as <see cref="TryCreateQueryLensFactory(Type, IEnumerable{Assembly})"/>,
    /// but limits discovery to factory types declared in
    /// <paramref name="requiredFactoryAssemblyPath"/> when provided.
    /// </summary>
    internal static object? TryCreateQueryLensFactory(
        Type dbContextType,
        IEnumerable<Assembly> assemblies,
        string? requiredFactoryAssemblyPath) =>
        TryCreateQueryLensFactory(dbContextType, assemblies, requiredFactoryAssemblyPath, out _);

    /// <summary>
    /// Same as <see cref="TryCreateQueryLensFactory(Type, IEnumerable{Assembly})"/>,
    /// but returns a diagnostic message when a matching factory type is found and
    /// invocation fails.
    /// </summary>
    internal static object? TryCreateQueryLensFactory(
        Type dbContextType,
        IEnumerable<Assembly> assemblies,
        out string? failureReason) =>
        TryCreateQueryLensFactory(dbContextType, assemblies, null, out failureReason);

    /// <summary>
    /// Same as <see cref="TryCreateQueryLensFactory(Type, IEnumerable{Assembly}, out string?)"/>,
    /// but limits discovery to factory types declared in
    /// <paramref name="requiredFactoryAssemblyPath"/> when provided.
    /// </summary>
    internal static object? TryCreateQueryLensFactory(
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
                        i.GetGenericTypeDefinition().FullName == QueryLensInterfaceName &&
                        i.GetGenericArguments()[0].FullName == dbContextType.FullName));

                // Compatibility fallback: accept "duck-typed" factories that expose
                // a public parameterless CreateOfflineContext() returning the target
                // DbContext type, even when the generic interface identity doesn't match
                // (e.g., copied interface definition in user code).
                factoryType ??= asm.GetTypes().FirstOrDefault(t =>
                    !t.IsAbstract && !t.IsInterface &&
                    t.GetConstructor(Type.EmptyTypes) is not null &&
                    t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Any(m =>
                            m.Name == "CreateOfflineContext" &&
                            m.GetParameters().Length == 0 &&
                            m.ReturnType.FullName == dbContextType.FullName));
            }
            catch (ReflectionTypeLoadException rtle)
            {
                // Surface which dependencies were missing so callers can diagnose the failure
                // rather than silently skipping the assembly and reporting "No factory found".
                var loaderMessages = (rtle.LoaderExceptions ?? [])
                    .Where(e => e is not null)
                    .Select(e => e!.Message)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3);
                var loaderDetail = string.Join("; ", loaderMessages);
                failureReason ??= $"Could not scan '{asm.GetName().Name}' for QueryLens factory" +
                    (string.IsNullOrWhiteSpace(loaderDetail) ? "." : $": {loaderDetail}");
                continue;
            }
            catch { continue; } // Native or otherwise unscannable assemblies

            if (factoryType is null) continue;

            if (!IsFactoryAssemblyAllowed(
                    factoryType,
                    normalizedRequiredPath,
                    "QueryLens",
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
                    && i.GetGenericTypeDefinition().FullName == QueryLensInterfaceName
                    && i.GetGenericArguments()[0].FullName == dbContextType.FullName);

                var method = matchingInterface?.GetMethod("CreateOfflineContext")
                             ?? factoryType.GetMethod("CreateOfflineContext")
                             ?? factoryType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                 .FirstOrDefault(m =>
                                     m.Name == "CreateOfflineContext"
                                     && m.GetParameters().Length == 0
                                     && m.ReturnType.FullName == dbContextType.FullName)
                             ?? factoryType.GetInterfaces()
                                 .SelectMany(i => i.GetMethods())
                                 .FirstOrDefault(m =>
                                     m.Name == "CreateOfflineContext"
                                     && m.GetParameters().Length == 0
                                     && m.ReturnType.FullName == dbContextType.FullName);

                if (method is null) continue;

                return method.Invoke(factory, null); // CreateOfflineContext takes no args
            }
            catch (Exception ex)
            {
                failureReason =
                    $"Found QueryLens factory '{factoryType.FullName}' but CreateOfflineContext() failed: {Unwrap(ex)}";
                return null;
            }
        }

        return null;
    }
}
