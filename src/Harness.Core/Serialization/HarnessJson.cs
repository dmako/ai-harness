using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Harness.Core.Serialization;

/// <summary>An object-shaped wire payload whose member names follow the selected JSON naming policy.</summary>
public sealed class JsonObjectMap : Dictionary<string, object?>;

/// <summary>JSON serialization using generated metadata, including heterogeneous wire dictionaries and sequences.</summary>
public static class HarnessJson
{
    private static readonly ConcurrentQueue<IJsonTypeInfoResolver> Resolvers = new();
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> OptionsCache = new();
    private static readonly JsonSerializerOptions Defaults = new();

    public static void Register(IJsonTypeInfoResolver resolver) => Resolvers.Enqueue(resolver);

    public static IJsonTypeInfoResolver Resolver { get; } = new GeneratedResolver();

    public static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerOptions? options = null) =>
        (JsonTypeInfo<T>)Options(options).GetTypeInfo(typeof(T));

    public static string Serialize(object? value, JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(value, TypeInfo<object?>(options));

    public static byte[] SerializeToUtf8Bytes(object? value, JsonSerializerOptions? options = null) =>
        JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<object?>(options));

    public static JsonElement SerializeToElement(object? value, JsonSerializerOptions? options = null) =>
        JsonSerializer.SerializeToElement(value, TypeInfo<object?>(options));

    public static JsonNode? SerializeToNode(object? value, JsonSerializerOptions? options = null) =>
        JsonSerializer.SerializeToNode(value, TypeInfo<object?>(options));

    public static T? Deserialize<T>(string json, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize(json, TypeInfo<T>(options));

    public static T? Deserialize<T>(ReadOnlySpan<byte> json, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize(json, TypeInfo<T>(options));

    public static T? Deserialize<T>(Stream json, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize(json, TypeInfo<T>(options));

    public static T? Deserialize<T>(JsonElement json, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize(json, TypeInfo<T>(options));

    public static T? Deserialize<T>(JsonNode? json, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize(json, TypeInfo<T>(options));

    private static JsonSerializerOptions Options(JsonSerializerOptions? options) =>
        OptionsCache.GetValue(
            options ?? Defaults,
            original =>
            {
                var copy = new JsonSerializerOptions(original)
                {
                    TypeInfoResolver = original.TypeInfoResolver ?? Resolver,
                };
                copy.Converters.Insert(0, new WireValueConverter());
                copy.MakeReadOnly();
                return copy;
            }
        );

    private sealed class GeneratedResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            foreach (var resolver in Resolvers)
            {
                if (resolver.GetTypeInfo(type, options) is { } info)
                {
                    return info;
                }
            }
            throw new NotSupportedException($"No generated JSON metadata registered for {type}.");
        }
    }

    private sealed class WireValueConverter : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonElement.ParseValue(ref reader);

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    return;
                case byte[] bytes:
                    writer.WriteBase64StringValue(bytes);
                    return;
                case JsonNode node:
                    node.WriteTo(writer, options);
                    return;
                case JsonElement element:
                    element.WriteTo(writer);
                    return;
                case IDictionary<string, object?> dictionary:
                    writer.WriteStartObject();
                    foreach (var (key, item) in dictionary)
                    {
                        var shaped = value is JsonObjectMap;
                        if (
                            shaped
                            && item is null
                            && options.DefaultIgnoreCondition is JsonIgnoreCondition.WhenWritingNull
                        )
                        {
                            continue;
                        }
                        writer.WritePropertyName(shaped ? options.PropertyNamingPolicy?.ConvertName(key) ?? key : key);
                        JsonSerializer.Serialize(
                            writer,
                            item,
                            (JsonTypeInfo<object?>)options.GetTypeInfo(typeof(object))
                        );
                    }
                    writer.WriteEndObject();
                    return;
                case IDictionary dictionary:
                    writer.WriteStartObject();
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        writer.WritePropertyName((string)entry.Key);
                        JsonSerializer.Serialize(
                            writer,
                            entry.Value,
                            (JsonTypeInfo<object?>)options.GetTypeInfo(typeof(object))
                        );
                    }
                    writer.WriteEndObject();
                    return;
                case IEnumerable sequence when value is not string:
                    writer.WriteStartArray();
                    foreach (var item in sequence)
                    {
                        JsonSerializer.Serialize(
                            writer,
                            item,
                            (JsonTypeInfo<object?>)options.GetTypeInfo(typeof(object))
                        );
                    }
                    writer.WriteEndArray();
                    return;
                case object when value.GetType() == typeof(object):
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                    return;
                default:
                    JsonSerializer.Serialize(writer, value, options.GetTypeInfo(value.GetType()));
                    return;
            }
        }
    }
}
