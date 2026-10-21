using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Harness.Mcp.Context;

/// <summary>The Go JSON string policy, including literal supplementary Unicode and HTML escaping.</summary>
public static class ContextJson
{
    public static string Serialize(object value)
    {
        var output = new StringBuilder();
        Write(JsonSerializer.SerializeToElement(value), output);
        return output.ToString();
    }
    public static int Size(object value) => Encoding.UTF8.GetByteCount(Serialize(value));

    private static void Write(JsonElement element, StringBuilder output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, Utf8Comparer.Instance))
                {
                    if (!first)
                    {
                        output.Append(',');
                    }

                    first = false;
                    String(property.Name, output);
                    output.Append(':');
                    Write(property.Value, output);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var separator = "";
                foreach (var item in element.EnumerateArray())
                { output.Append(separator); separator = ","; Write(item, output); }
                output.Append(']');
                break;
            case JsonValueKind.String:
                String(element.GetString()!, output);
                break;
            default:
                output.Append(element.GetRawText());
                break;
        }
    }
    private static void String(string value, StringBuilder output)
    {
        output.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            var escape = rune.Value switch { 34 => "\\\"", 92 => "\\\\", 8 => "\\b", 12 => "\\f", 10 => "\\n", 13 => "\\r", 9 => "\\t", _ => null };
            if (escape is not null)
            {
                output.Append(escape);
            }
            else if (rune.Value is < 32 or 60 or 62 or 38 or 0x2028 or 0x2029)
            {
                output.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                output.Append(rune.ToString());
            }
        }
        output.Append('"');
    }
    private sealed class Utf8Comparer : IComparer<string>
    {
        public static Utf8Comparer Instance { get; } = new();
        public int Compare(string? x, string? y) => Encoding.UTF8.GetBytes(x ?? "").AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y ?? ""));
    }
}

public sealed record ContextAnswer(string Json);
