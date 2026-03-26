using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EFQueryLens.Core.Contracts;

namespace EFQueryLens.Lsp.Engine;

/// <summary>
/// Engine control operations beyond IQueryLensEngine (restart, invalidate cache).
/// </summary>
internal interface IEngineControl
{
    Task PingAsync(CancellationToken ct = default);
    Task RestartAsync(CancellationToken ct = default);
    Task InvalidateCacheAsync(CancellationToken ct = default);
    Task WarmTranslateAsync(TranslationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Calls the QueryLens engine server over HTTP/JSON.
/// Handles transparent reconnection and restart on failure.
/// </summary>
internal sealed class EngineHttpClient : IQueryLensEngine, IEngineControl
{
    private HttpClient _httpClient;
    private readonly string _workspacePath;
    private readonly string? _engineAssemblyPath;
    private readonly int _startTimeoutMs;
    private readonly bool _debugEnabled;

    public EngineHttpClient(
        int port,
        string workspacePath,
        string? engineAssemblyPath,
        int startTimeoutMs,
        bool debugEnabled)
    {
        _workspacePath = workspacePath;
        _engineAssemblyPath = engineAssemblyPath;
        _startTimeoutMs = startTimeoutMs;
        _debugEnabled = debugEnabled;
        _httpClient = CreateHttpClient(port);
    }

    // --- IQueryLensEngine ---

    public async Task<QueryTranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken ct = default)
    {
        try
        {
            return await PostJsonAsync<TranslationRequest, QueryTranslationResult>("/translate", request, ct);
        }
        catch (HttpRequestException)
        {
            await TryReconnectPortAsync(ct);
            return await PostJsonAsync<TranslationRequest, QueryTranslationResult>("/translate", request, ct);
        }
    }

    public async Task<ModelSnapshot> InspectModelAsync(
        ModelInspectionRequest request,
        CancellationToken ct = default)
    {
        return await PostJsonAsync<ModelInspectionRequest, ModelSnapshot>("/inspect-model", request, ct);
    }

    // --- IEngineControl ---

    public async Task PingAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/ping", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        // Attempt graceful shutdown of the current engine; ignore errors.
        try
        {
            await _httpClient.PostAsync("/shutdown", content: null, ct);
        }
        catch
        {
            // Best-effort — engine may already be gone.
        }

        var newPort = await EngineDiscovery.StartEngineAsync(
            _workspacePath,
            _engineAssemblyPath ?? string.Empty,
            _startTimeoutMs);

        if (newPort is null)
        {
            throw new InvalidOperationException(
                "RestartAsync: could not start a new QueryLens engine process.");
        }

        UpdatePort(newPort.Value);
    }

    public async Task InvalidateCacheAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsync("/invalidate", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task WarmTranslateAsync(TranslationRequest request, CancellationToken ct = default)
    {
        try
        {
            await _httpClient.PostAsJsonAsync("/translate/warm", request, EngineJsonOptions.Default, ct);
            // Ignore response — 202 is success, anything else is best-effort.
        }
        catch
        {
            // Best-effort — engine may still be starting up; pre-warm is not critical.
        }
    }

    // --- IAsyncDisposable ---

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    // --- Reconnection ---

    private async Task<int> TryReconnectPortAsync(CancellationToken ct)
    {
        var port = await EngineDiscovery.TryGetExistingPortAsync(_workspacePath, pingTimeoutMs: 2000);
        if (port is not null)
        {
            UpdatePort(port.Value);
            return port.Value;
        }

        var newPort = await EngineDiscovery.StartEngineAsync(
            _workspacePath,
            _engineAssemblyPath ?? string.Empty,
            _startTimeoutMs);

        if (newPort is not null)
        {
            UpdatePort(newPort.Value);
            return newPort.Value;
        }

        throw new InvalidOperationException(
            "Could not reconnect to or restart the QueryLens engine.");
    }

    private void UpdatePort(int port)
    {
        var old = _httpClient;
        _httpClient = CreateHttpClient(port);
        old.Dispose();
    }

    // --- Helpers ---

    private static HttpClient CreateHttpClient(int port) =>
        new()
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromMinutes(5),
        };

    private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken ct)
    {
        var response = await _httpClient.PostAsJsonAsync(path, request, EngineJsonOptions.Default, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<TResponse>(EngineJsonOptions.Default, ct);
        if (result is null)
        {
            throw new InvalidOperationException(
                $"Engine returned a null response body for {path}.");
        }

        return result;
    }
}

/// <summary>
/// Shared JSON serializer options for engine HTTP communication.
/// Uses camelCase with case-insensitive property matching, and ticks-based TimeSpan converters
/// to match the engine server's serialization format.
/// </summary>
internal static class EngineJsonOptions
{
    public static readonly JsonSerializerOptions Default = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        options.Converters.Add(new TimeSpanTicksConverter());
        options.Converters.Add(new NullableTimeSpanTicksConverter());

        return options;
    }
}

/// <summary>
/// Serializes <see cref="TimeSpan"/> as a ticks long value — matches the engine server format.
/// </summary>
file sealed class TimeSpanTicksConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TimeSpan.FromTicks(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Ticks);
}

/// <summary>
/// Serializes <see cref="Nullable{TimeSpan}"/> as a ticks long value or JSON null.
/// </summary>
file sealed class NullableTimeSpanTicksConverter : JsonConverter<TimeSpan?>
{
    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : TimeSpan.FromTicks(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value.Ticks);
    }
}
