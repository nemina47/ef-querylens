using System.Text.Json;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Lsp.Engine;

namespace EFQueryLens.Integration.Tests.Lsp;

/// <summary>
/// Tests for <see cref="EngineJsonOptions"/> — verifies camelCase naming and
/// ticks-based <see cref="TimeSpan"/> serialization used in engine HTTP communication.
/// </summary>
public class EngineJsonOptionsTests
{
    // ── Options configuration ────────────────────────────────────────────────

    [Fact]
    public void Default_HasCamelCaseNamingPolicy()
    {
        Assert.Equal(JsonNamingPolicy.CamelCase, EngineJsonOptions.Default.PropertyNamingPolicy);
    }

    [Fact]
    public void Default_IsCaseInsensitive()
    {
        Assert.True(EngineJsonOptions.Default.PropertyNameCaseInsensitive);
    }

    // ── TimeSpan serialized as ticks ─────────────────────────────────────────

    [Fact]
    public void Serialize_TranslationMetadata_TimespanWrittenAsTicks()
    {
        var ts = TimeSpan.FromMilliseconds(250); // 2_500_000 ticks
        var metadata = new TranslationMetadata
        {
            DbContextType = "MyContext",
            EfCoreVersion = "8.0.0",
            ProviderName = "Sqlite",
            TranslationTime = ts,
        };

        var json = JsonSerializer.Serialize(metadata, EngineJsonOptions.Default);
        using var doc = JsonDocument.Parse(json);

        var ticks = doc.RootElement.GetProperty("translationTime").GetInt64();
        Assert.Equal(ts.Ticks, ticks);
    }

    [Fact]
    public void Deserialize_TranslationMetadata_TicksValueReadAsTimeSpan()
    {
        var expected = TimeSpan.FromSeconds(1);
        var json = $$"""
            {
              "dbContextType": "MyContext",
              "efCoreVersion": "8.0.0",
              "providerName": "Sqlite",
              "translationTime": {{expected.Ticks}}
            }
            """;

        var metadata = JsonSerializer.Deserialize<TranslationMetadata>(json, EngineJsonOptions.Default)!;

        Assert.Equal(expected, metadata.TranslationTime);
    }

    [Fact]
    public void RoundTrip_TimeSpan_IsLossless()
    {
        var original = TimeSpan.FromMilliseconds(123.456);
        var metadata = new TranslationMetadata
        {
            DbContextType = "Context",
            EfCoreVersion = "8.0",
            ProviderName = "Npgsql",
            TranslationTime = original,
        };

        var json = JsonSerializer.Serialize(metadata, EngineJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<TranslationMetadata>(json, EngineJsonOptions.Default)!;

        Assert.Equal(original, restored.TranslationTime);
    }

    // ── Nullable TimeSpan ────────────────────────────────────────────────────

    [Fact]
    public void Serialize_NullableTimeSpan_WrittenAsTicks()
    {
        var ts = TimeSpan.FromSeconds(2);
        var metadata = new TranslationMetadata
        {
            DbContextType = "C",
            EfCoreVersion = "8",
            ProviderName = "Sqlite",
            ContextResolutionTime = ts,
        };

        var json = JsonSerializer.Serialize(metadata, EngineJsonOptions.Default);
        using var doc = JsonDocument.Parse(json);

        var ticks = doc.RootElement.GetProperty("contextResolutionTime").GetInt64();
        Assert.Equal(ts.Ticks, ticks);
    }

    [Fact]
    public void Serialize_NullableTimeSpan_NullWrittenAsJsonNull()
    {
        var metadata = new TranslationMetadata
        {
            DbContextType = "C",
            EfCoreVersion = "8",
            ProviderName = "Sqlite",
            ContextResolutionTime = null,
        };

        var json = JsonSerializer.Serialize(metadata, EngineJsonOptions.Default);
        using var doc = JsonDocument.Parse(json);

        var kind = doc.RootElement.GetProperty("contextResolutionTime").ValueKind;
        Assert.Equal(JsonValueKind.Null, kind);
    }

    [Fact]
    public void RoundTrip_NullableTimeSpan_IsLossless()
    {
        TimeSpan? original = TimeSpan.FromMinutes(3);
        var metadata = new TranslationMetadata
        {
            DbContextType = "C",
            EfCoreVersion = "8",
            ProviderName = "Sqlite",
            ContextResolutionTime = original,
        };

        var json = JsonSerializer.Serialize(metadata, EngineJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<TranslationMetadata>(json, EngineJsonOptions.Default)!;

        Assert.Equal(original, restored.ContextResolutionTime);
    }

    [Fact]
    public void RoundTrip_NullableTimeSpan_NullIsPreserved()
    {
        var metadata = new TranslationMetadata
        {
            DbContextType = "C",
            EfCoreVersion = "8",
            ProviderName = "Sqlite",
            ContextResolutionTime = null,
        };

        var json = JsonSerializer.Serialize(metadata, EngineJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<TranslationMetadata>(json, EngineJsonOptions.Default)!;

        Assert.Null(restored.ContextResolutionTime);
    }

    // ── Property naming ──────────────────────────────────────────────────────

    [Fact]
    public void Serialize_PropertyNamesAreCamelCase()
    {
        var metadata = new TranslationMetadata
        {
            DbContextType = "MyContext",
            EfCoreVersion = "8",
            ProviderName = "Sqlite",
        };

        var json = JsonSerializer.Serialize(metadata, EngineJsonOptions.Default);

        Assert.Contains("\"dbContextType\"", json);
        Assert.Contains("\"efCoreVersion\"", json);
        Assert.Contains("\"providerName\"", json);
    }

    [Fact]
    public void Deserialize_AcceptsPascalCaseProperty_WhenCaseInsensitive()
    {
        var json = """
            {
              "DbContextType": "MyContext",
              "EfCoreVersion": "8.0",
              "ProviderName": "Sqlite",
              "TranslationTime": 0
            }
            """;

        var metadata = JsonSerializer.Deserialize<TranslationMetadata>(json, EngineJsonOptions.Default)!;

        Assert.Equal("MyContext", metadata.DbContextType);
        Assert.Equal("8.0", metadata.EfCoreVersion);
    }
}
