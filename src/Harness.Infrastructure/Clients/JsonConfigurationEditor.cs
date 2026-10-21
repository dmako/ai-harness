using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Instructions;

namespace Harness.Infrastructure.Clients;

/// <summary>JSON member splices preserve every byte outside the edited member.</summary>
public static class JsonConfigurationEditor
{
    private sealed record Member(string Key, int KeyStart, Node Value);
    private sealed record Node(int Start, int End, IReadOnlyList<Member>? Members = null);
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static Node Parse(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        reader.Read();
        var root = Read(ref reader);
        if (reader.Read())
        {
            throw new InvalidDataException("unexpected data after top-level JSON value");
        }

        return root;
    }
    private static Node Read(ref Utf8JsonReader reader)
    {
        var start = checked((int)reader.TokenStartIndex);
        if (reader.TokenType is not JsonTokenType.StartObject)
        {
            reader.Skip();
            return new(start, checked((int)reader.BytesConsumed));
        }
        List<Member> members = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType is not JsonTokenType.EndObject)
        {
            if (reader.TokenType is not JsonTokenType.PropertyName)
            {
                throw new InvalidDataException("expected JSON object key");
            }

            var key = reader.GetString()!;
            if (!keys.Add(key))
            {
                throw new InvalidDataException("duplicate JSON object key; edit it by hand");
            }

            var keyStart = checked((int)reader.TokenStartIndex);
            reader.Read();
            members.Add(new(key, keyStart, Read(ref reader)));
        }
        return new(start, checked((int)reader.BytesConsumed), members);
    }
    public static JsonObject ParseObject(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        Parse(bytes);
        return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("top-level value is not a JSON object");
    }
    public static TextEdit Set(string source, IReadOnlyList<string> path, string key, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bytes = Encoding.UTF8.GetBytes(source);
        var root = Parse(bytes);
        if (root.Members is null)
        {
            throw new InvalidDataException("top-level value is not a JSON object");
        }

        var node = root;
        var depth = 0;
        for (; depth < path.Count; depth++)
        {
            var member = node.Members.FirstOrDefault(m => m.Key == path[depth]);
            if (member is null)
            {
                break;
            }

            node = member.Value;
            if (node.Members is null)
            {
                throw new InvalidDataException(string.Join('.', path.Take(depth + 1)) + " is not an object");
            }
        }
        var action = node.Members.Any(m => m.Key == key) && depth == path.Count ? "update" : "add";
        if (depth < path.Count)
        {
            value = new JsonObject { [key] = value?.DeepClone() };
            for (var i = path.Count - 1; i > depth; i--)
            {
                value = new JsonObject { [path[i]] = value };
            }

            key = path[depth];
        }
        return new(SetMember(bytes, node, key, value), action);
    }
    public static TextEdit Delete(string source, IReadOnlyList<string> path, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var node = Parse(bytes);
        if (node.Members is null)
        {
            throw new InvalidDataException("top-level value is not a JSON object");
        }

        foreach (var part in path)
        {
            var child = node.Members?.FirstOrDefault(m => m.Key == part)?.Value;
            if (child?.Members is null)
            {
                return new(source, "absent");
            }

            node = child;
        }
        var members = node.Members;
        var index = members.ToList().FindIndex(m => m.Key == key);
        if (index < 0)
        {
            return new(source, "absent");
        }

        var current = members[index];
        var span = index < members.Count - 1 ? (current.KeyStart, members[index + 1].KeyStart) : (members[Math.Max(0, index - 1)].Value.End, current.Value.End);
        var (start, end) = members.Count is 1 ? (node.Start + 1, node.End - 1) : span;
        return new(Splice(bytes, start, end, ""), "remove");
    }
    private static string Indent(byte[] source, int offset)
    {
        var start = Array.LastIndexOf(source, (byte)'\n', Math.Max(0, offset - 1)) + 1;
        var end = start;
        while (end < source.Length && source[end] is (byte)' ' or (byte)'\t')
        {
            end++;
        }

        return Encoding.UTF8.GetString(source[start..end]);
    }
    private static string SetMember(byte[] source, Node node, string key, JsonNode? value)
    {
        var members = node.Members!;
        var text = Encoding.UTF8.GetString(source);
        var unit = text.Split('\n').Select(line => line[..(line.Length - line.TrimStart(' ', '\t').Length)]).FirstOrDefault(s => s.Length > 0) ?? "  ";
        var indent = Indent(source, node.Start) + unit;
        if (members.Count > 0)
        {
            indent = source.AsSpan(node.Start, members[0].KeyStart - node.Start).Contains((byte)'\n') ? Indent(source, members[0].KeyStart) : "";
        }

        var multiline = members.Count is 0 || indent.Length > 0;
        var encoded = Encode(value, multiline, indent, unit);
        var existing = members.FirstOrDefault(m => m.Key == key);
        if (existing is not null)
        {
            return Splice(source, existing.Value.Start, existing.Value.End, encoded);
        }

        var member = JsonSerializer.Serialize(key, Compact) + ": " + encoded;
        if (members.Count is 0)
        {
            return Splice(source, node.Start + 1, node.End - 1, "\n" + indent + member + "\n" + Indent(source, node.Start));
        }

        return Splice(source, members[^1].Value.End, members[^1].Value.End, (multiline ? ",\n" + indent : ", ") + member);
    }
    private static JsonNode? Sorted(JsonNode? value) => value switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Sorted).ToArray()),
        null => null,
        _ => value.DeepClone(),
    };
    private static string Encode(JsonNode? value, bool multiline, string indent, string unit)
    {
        var options = new JsonSerializerOptions(Compact) { WriteIndented = multiline, IndentCharacter = unit.Contains('\t', StringComparison.Ordinal) ? '\t' : ' ', IndentSize = unit.Length };
        var encoded = JsonSerializer.Serialize(Sorted(value), options).Replace("<", "\\u003c", StringComparison.Ordinal).Replace(">", "\\u003e", StringComparison.Ordinal).Replace("&", "\\u0026", StringComparison.Ordinal);
        return encoded.Replace("\n", "\n" + indent, StringComparison.Ordinal);
    }
    private static string Splice(byte[] source, int from, int to, string replacement) => Encoding.UTF8.GetString(source[..from]) + replacement + Encoding.UTF8.GetString(source[to..]);
}
