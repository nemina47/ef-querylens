using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace EFQueryLens.Core.Scripting.Evaluation;

/// <summary>
/// Fake service implementations used for offline DbContext construction.
/// These provide stub implementations of dependency injection services so that
/// DbContext.OnConfiguring can resolve dependencies without accessing a real database
/// or external services.
/// </summary>
internal abstract class QueryLensFakeServices
{
    /// <summary>
    /// Provides fake services for DbContext construction when real services are unavailable.
    /// This ensures DbContext.OnConfiguring can resolve dependencies like IConfiguration
    /// for named connection strings without failing in the offline evaluation context.
    /// </summary>
    internal sealed class ServiceProvider : IServiceProvider
    {
        private readonly IConfiguration _configuration = new Configuration();

        /// <summary>
        /// Static accessor for the current fake service provider in the AsyncLocal context.
        /// This allows external code or EF Core to discover and use the fake provider.
        /// </summary>
        internal static ServiceProvider? Current =>
            QueryEvaluator.CurrentFakeServiceProvider as ServiceProvider;

        public object? GetService(Type serviceType)
        {
            // Provide fake implementations for common services EF Core might request
            if (serviceType == typeof(IConfiguration))
                return _configuration;

            if (serviceType == typeof(IConfigurationRoot))
                return _configuration as IConfigurationRoot;

            if (serviceType == typeof(Configuration))
                return _configuration;

            // For other IConfiguration-like types by name, return our configuration
            if (serviceType?.Name is "IConfiguration" or "IConfigurationRoot")
                return _configuration;

            // Check if this provider itself is being requested
            if (serviceType == typeof(IServiceProvider) || serviceType == typeof(ServiceProvider))
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
    internal sealed class Configuration : IConfiguration, IConfigurationRoot
    {
        private const string DummyConnectionString =
            "Server=localhost;Database=__querylens__;Encrypt=false;TrustServerCertificate=true;";

        public string? this[string key]
        {
            get
            {
                // Return dummy connection strings for any connection string lookup,
                // including the canonical Name=_querylens used by generated factories.
                if (key?.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase) == true)
                    return DummyConnectionString;

                // Return null for other keys so EF Core uses its defaults
                return null;
            }
            set { }
        }

        public IEnumerable<IConfigurationProvider> Providers => [];

        public IConfigurationSection GetSection(string key) =>
            new ConfigurationSection(this, key, key);

        public IEnumerable<IConfigurationSection> GetChildren() =>
            [];

        public IChangeToken GetReloadToken() =>
            new ChangeToken();

        public void Reload() { }
    }

    /// <summary>
    /// Fake configuration section that acts as a null/empty section.
    /// </summary>
    internal sealed class ConfigurationSection : IConfigurationSection
    {
        private readonly Configuration _root;

        public ConfigurationSection(Configuration root, string key, string path)
        {
            _root = root;
            Key = key;
            Path = path;
        }

        public string Key { get; }
        public string Path { get; }
        public string? Value
        {
            get => _root[Path];
            set => _root[Path] = value;
        }

        public string? this[string key]
        {
            get => _root[$"{Path}:{key}"];
            set { }
        }

        public IEnumerable<IConfigurationSection> GetChildren()
        {
            if (!string.Equals(Path, "ConnectionStrings", StringComparison.OrdinalIgnoreCase))
                return [];

            // Named connection resolution is intentionally name-agnostic and handled by indexer
            // lookup (ConnectionStrings:<any-name>) so we do not hardcode child names here.
            return [];
        }

        public IChangeToken GetReloadToken() =>
            new ChangeToken();

        public IConfigurationSection GetSection(string key) =>
            new ConfigurationSection(
                _root,
                key,
                string.IsNullOrWhiteSpace(Path) ? key : $"{Path}:{key}");
    }

    /// <summary>
    /// Fake change token that never signals changes (configuration is static in eval context).
    /// </summary>
    internal sealed class ChangeToken : IChangeToken
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
