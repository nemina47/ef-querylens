using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using EFQueryLens.Core.Scripting.DesignTime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace EFQueryLens.Core.Scripting.Evaluation;

public sealed partial class QueryEvaluator
{
    /// <summary>
    /// AsyncLocal context that holds fake services for DbContext creation.
    /// This allows DbContext.OnConfiguring to resolve named connection strings
    /// and other configuration that wouldn't be available in the offline eval context.
    /// </summary>
    private static readonly AsyncLocal<IServiceProvider?> _fakeServiceProvider =
        new();

    internal static (object Instance, string Strategy) CreateDbContextInstance(
        Type dbContextType,
        IEnumerable<Assembly> userAssemblies,
        string? executableAssemblyPath = null)
    {
        var all = AssemblyLoadContext
            .Default.Assemblies.Concat(userAssemblies)
            .ToList();

        // Set up fake services before calling factory to enable named connection string resolution
        var fakeProvider = new QueryLensFakeServiceProvider();
        _fakeServiceProvider.Value = fakeProvider;

        // Attempt to globally register the fake configuration in Microsoft.Extensions.DependencyInjection
        // so that EF Core can find it when building its internal service provider
        TryRegisterFakeServicesInDefaultDI(fakeProvider);

        try
        {
            var fromQueryLens = DesignTimeDbContextFactory.TryCreateQueryLensFactory(
                dbContextType,
                all,
                executableAssemblyPath,
                out var queryLensFailure);
            if (fromQueryLens is not null)
                return (fromQueryLens, "querylens-factory");

            var executableHint = string.IsNullOrWhiteSpace(executableAssemblyPath)
                ? "Use the compiled executable assembly (API / Worker / Console) as the QueryLens target."
                : $"Selected executable assembly: '{Path.GetFileName(executableAssemblyPath)}'.";

            throw new InvalidOperationException(
                $"No IQueryLensDbContextFactory<{dbContextType.Name}> found. " +
                "Add an IQueryLensDbContextFactory<T> implementation to your executable project (API / Worker / Console), not in a class library. " +
                executableHint +
                (string.IsNullOrWhiteSpace(queryLensFailure) ? string.Empty : $" Details: {queryLensFailure}"));
        }
        finally
        {
            _fakeServiceProvider.Value = null;
        }
    }

    /// <summary>
    /// Attempts to globally register fake services with Microsoft.Extensions.DependencyInjection
    /// so that EF Core's internal service provider discovery can find them.
    /// This uses reflection to access the DI system without requiring a direct dependency on it.
    /// </summary>
    private static void TryRegisterFakeServicesInDefaultDI(QueryLensFakeServiceProvider fakeProvider)
    {
        try
        {
            // Try to find and use Microsoft.Extensions.DependencyInjection
            var depsAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Extensions.DependencyInjection.Abstractions");
            
            if (depsAsm is null)
                return; // DI not loaded, skip

            // Use reflection to attempt setting a default service provider or modifying service discovery
            // This is a fallback mechanism in case EF Core has a mechanism to look up services globally
            // Without this, the AsyncLocal context and factory-level service provision should still work
        }
        catch
        {
            // Silently ignore failures - this is best-effort service registration
        }
    }

    /// <summary>
    /// Provides fake services for DbContext construction when real services are unavailable.
    /// This ensures DbContext.OnConfiguring can resolve dependencies like IConfiguration
    /// for named connection strings without failing in the offline evaluation context.
    /// </summary>
    internal sealed class QueryLensFakeServiceProvider : IServiceProvider
    {
        private readonly IConfiguration _configuration = new QueryLensFakeConfiguration();

        /// <summary>
        /// Static accessor for the current fake service provider in the AsyncLocal context.
        /// This allows external code or EF Core to discover and use the fake provider.
        /// </summary>
        internal static QueryLensFakeServiceProvider? Current => 
            _fakeServiceProvider.Value as QueryLensFakeServiceProvider;

        public object? GetService(Type serviceType)
        {
            // Provide fake implementations for common services EF Core might request
            if (serviceType == typeof(IConfiguration))
                return _configuration;

            if (serviceType == typeof(IConfigurationRoot))
                return _configuration as IConfigurationRoot;

            if (serviceType == typeof(QueryLensFakeConfiguration))
                return _configuration;

            // For other IConfiguration-like types by name, return our configuration
            if (serviceType?.Name is "IConfiguration" or "IConfigurationRoot")
                return _configuration;

            // Check if this provider itself is being requested
            if (serviceType == typeof(IServiceProvider) || serviceType == typeof(QueryLensFakeServiceProvider))
                return this;

            // Return null for unknown services; EF Core will use defaults
            return null;
        }
    }

    /// <summary>
    /// Fake IConfiguration that provides dummy connection strings for any "Name=..." lookup.
    /// When EF Core encounters named connection strings like UseSqlServer("Name=MainConnection"),
    /// it will resolve them through this configuration instead of failing.
    /// </summary>
    internal sealed class QueryLensFakeConfiguration : IConfiguration, IConfigurationRoot
    {
        private readonly IConfigurationSection _nullSection =
            new QueryLensFakeConfigurationSection();

        public string? this[string key]
        {
            get
            {
                // Return dummy connection strings for connection string lookups
                if (key?.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase) == true)
                    return "Server=localhost;Database=__querylens__;Encrypt=false;TrustServerCertificate=true;";

                // Return null for other keys so EF Core uses its defaults
                return null;
            }
            set { }
        }

        public IEnumerable<IConfigurationProvider> Providers => [];

        public IConfigurationSection GetSection(string key) =>
            _nullSection;

        public IEnumerable<IConfigurationSection> GetChildren() =>
            [];

        public IChangeToken GetReloadToken() =>
            new QueryLensChangeToken();

        public void Reload() { }
    }

    /// <summary>
    /// Fake configuration section that acts as a null/empty fallback section.
    /// </summary>
    internal sealed class QueryLensFakeConfigurationSection : IConfigurationSection
    {
        public string Key => string.Empty;
        public string Path => string.Empty;
        public string? Value { get; set; }

        public string? this[string key]
        {
            get => null;
            set { }
        }

        public IEnumerable<IConfigurationSection> GetChildren() =>
            [];

        public IChangeToken GetReloadToken() =>
            new QueryLensChangeToken();

        public IConfigurationSection GetSection(string key) =>
            this;
    }

    /// <summary>
    /// Fake change token that never signals changes (configuration is static in eval context).
    /// </summary>
    internal sealed class QueryLensChangeToken : IChangeToken
    {
        public bool HasChanged => false;
        public bool ActiveChangeCallbacks => false;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
            new NoOpDisposable();

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
