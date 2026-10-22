using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Core.Memory;

namespace Harness.Core.Telemetry;

public static class TelemetryJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = true,
        };
        options.Converters.Add(new TimestampConverter());
        return options;
    }

    private sealed class TimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) => DateTimeOffset.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(new MemoryTimestamp(value).ToRfc3339Nano());
    }
}
