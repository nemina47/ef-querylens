using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EFQueryLens.Core.Daemon;
using Microsoft.Extensions.Hosting;

namespace EFQueryLens.Daemon;

internal sealed class QueryLensConfigWatcher(
    DaemonWorkspaceOptions workspaceOptions,
    DaemonEventStreamBroker eventStreamBroker)
    : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly bool _debugEnabled = ReadBoolEnvironmentVariable("QUERYLENS_DEBUG", fallback: false);
    private string? _lastFingerprint;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workspacePath = workspaceOptions.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return;
        }

        var configPath = Path.Combine(workspacePath, ".querylens.json");
        _lastFingerprint = BuildFingerprint(configPath, out _);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var fingerprint = BuildFingerprint(configPath, out var contextNames);
                if (string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                _lastFingerprint = fingerprint;
                eventStreamBroker.PublishConfigReloaded(contextNames);
                LogDebug($"config-reloaded path={configPath} contexts={string.Join(",", contextNames)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LogDebug($"config-watch-error type={ex.GetType().Name} message={ex.Message}");
            }
        }
    }

    private static string BuildFingerprint(string configPath, out IReadOnlyList<string> contextNames)
    {
        if (!File.Exists(configPath))
        {
            contextNames = [];
            return "missing";
        }

        var text = File.ReadAllText(configPath);
        contextNames = ReadContextNames(text);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    private static IReadOnlyList<string> ReadContextNames(string json)
    {
        try
        {
            var config = JsonSerializer.Deserialize<QueryLensConfig>(json, JsonOptions);
            if (config?.Contexts is null || config.Contexts.Count == 0)
            {
                return [];
            }

            return config.Contexts
                .Select(context => context.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool ReadBoolEnvironmentVariable(string variableName, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private void LogDebug(string message)
    {
        if (!_debugEnabled)
        {
            return;
        }

        Console.Error.WriteLine($"[QL-DAEMON-CONFIG] {message}");
    }
}
