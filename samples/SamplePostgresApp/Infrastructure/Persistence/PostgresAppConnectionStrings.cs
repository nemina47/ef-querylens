using Microsoft.Extensions.Configuration;

namespace SamplePostgresApp.Infrastructure.Persistence;

internal static class PostgresAppConnectionStrings
{
    private const string RuntimeConnectionName = "SamplePostgres";
    private const string OfflineConnectionName = "SamplePostgresQueryLensOffline";
    private const string RuntimeEnvironmentVariable = "SAMPLE_POSTGRES_CONNECTION_STRING";
    private const string OfflineEnvironmentVariable = "SAMPLE_POSTGRES_QUERYLENS_CONNECTION_STRING";

    public static string ResolveRuntimeConnectionString()
        => ResolveConnectionString(RuntimeEnvironmentVariable, RuntimeConnectionName);

    public static string ResolveOfflineConnectionString()
        => ResolveConnectionString(OfflineEnvironmentVariable, OfflineConnectionName);

    private static string ResolveConnectionString(string environmentVariableName, string connectionName)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(environmentVariableName);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var configuration = BuildConfiguration();
        var fromConfiguration = configuration.GetConnectionString(connectionName);
        if (!string.IsNullOrWhiteSpace(fromConfiguration))
        {
            return fromConfiguration;
        }

        throw new InvalidOperationException(
            $"Connection string '{connectionName}' was not found. " +
            $"Set environment variable '{environmentVariableName}' or add it to appsettings.json.");
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(PostgresAppConnectionStrings).Assembly.Location)
            ?? AppContext.BaseDirectory;

        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var builder = new ConfigurationBuilder()
            .SetBasePath(assemblyDirectory)
            .AddJsonFile("appsettings.json", optional: true);

        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            builder.AddJsonFile($"appsettings.{environmentName}.json", optional: true);
        }

        return builder.Build();
    }
}
