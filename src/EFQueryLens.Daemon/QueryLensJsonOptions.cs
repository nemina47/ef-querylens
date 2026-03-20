using System.Text.Json;
using System.Text.Json.Serialization;

namespace EFQueryLens.Daemon;

internal static class QueryLensJsonOptions
{
    public static JsonSerializerOptions Create() => new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new TimeSpanTicksConverter(),
            new NullableTimeSpanTicksConverter(),
        },
    };

    // TimeSpan serialized as ticks (long) for clean round-tripping
    private sealed class TimeSpanTicksConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => TimeSpan.FromTicks(reader.GetInt64());

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value.Ticks);
    }

    private sealed class NullableTimeSpanTicksConverter : JsonConverter<TimeSpan?>
    {
        public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? null : TimeSpan.FromTicks(reader.GetInt64());

        public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteNumberValue(value.Value.Ticks);
        }
    }
}
