using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Serialization;
using ModelContextProtocol.Protocol;

namespace Harness.Providers.Mcp;

/// <summary>Generic pack mapping primitives; no downstream tool-specific behavior.</summary>
internal static class McpMapping
{
    internal readonly record struct Entry(JsonElement Value, string? Key = null)
    {
        internal JsonElement Field(string path) =>
            path == "$key" ? HarnessJson.SerializeToElement(Key) : Lookup(Value, path);
    }

    internal static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    internal static ProviderException ToolError(string provider, string tool, CallToolResult result)
    {
        var text = Text(result).Trim();
        try
        {
            using var body = JsonDocument.Parse(text);
            var message = String(Lookup(body.RootElement, "error"));
            if (message.Length is 0)
            {
                message = String(Lookup(body.RootElement, "message"));
            }

            if (message.Length > 0)
            {
                return new(
                    provider,
                    CredentialRedactor.Redact(message),
                    tool,
                    CredentialRedactor.Redact(String(Lookup(body.RootElement, "hint")))
                );
            }
        }
        catch (JsonException)
        { /* Plain-text tool failures are also valid MCP. */
        }
        return new(
            provider,
            text.Length is 0
                ? "tool reported an error without details"
                : CredentialRedactor.Redact(Utf8Prefix(text, 500, "…")),
            tool
        );
    }

    internal static JsonElement Body(CallToolResult result)
    {
        if (result.StructuredContent is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } structured)
        {
            return structured.Clone();
        }

        var text = Text(result).Trim();
        try
        {
            return HarnessJson.Deserialize<JsonElement>(text);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"response is not JSON ({System.Text.Encoding.UTF8.GetByteCount(Text(result))} bytes); map JSON with items/fields, or text with pattern"
                ),
                error
            );
        }
    }

    internal static Dictionary<string, object?> Arguments(
        IReadOnlyDictionary<string, JsonElement> template,
        IReadOnlyDictionary<string, string> values
    )
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in template)
        {
            if (value.ValueKind is not JsonValueKind.String)
            {
                result[key] = value.Clone();
                continue;
            }
            var text = value.GetString()!;
            if (text.StartsWith('{') && text.EndsWith('}') && text.Count(character => character is '{') is 1)
            {
                var name = text[1..^1];
                var replacement = values.GetValueOrDefault(name, "");
                if (replacement.Length > 0)
                {
                    result[key] = name is "limit" or "limit_more" or "budget"
                        ? long.Parse(replacement, CultureInfo.InvariantCulture)
                        : replacement;
                }

                continue;
            }
            var skip = false;
            foreach (var (name, replacement) in values)
            {
                var placeholder = "{" + name + "}";
                if (!text.Contains(placeholder, StringComparison.Ordinal))
                {
                    continue;
                }

                skip |= replacement.Length is 0;
                text = text.Replace(placeholder, replacement, StringComparison.Ordinal);
            }
            if (!skip)
            {
                result[key] = text;
            }
        }
        return result;
    }

    internal static JsonElement Lookup(JsonElement value, string path)
    {
        if (path.Length is 0)
        {
            return value;
        }

        foreach (var part in path.Split('.'))
        {
            if (value.ValueKind is JsonValueKind.Object && value.TryGetProperty(part, out var property))
            {
                value = property;
            }
            else if (
                value.ValueKind is JsonValueKind.Array
                && int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0
                && index < value.GetArrayLength()
            )
            {
                value = value[index];
            }
            else
            {
                return default;
            }
        }
        return value;
    }

    internal static (IReadOnlyList<Entry> Entries, bool Found) Collect(JsonElement value, string path)
    {
        if (path.Length is 0)
        {
            return value.ValueKind is JsonValueKind.Array
                ? (value.EnumerateArray().Select(item => new Entry(item)).ToArray(), true)
                : ([], false);
        }

        var current = new List<Entry> { new(value) };
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var next = new List<Entry>();
            var expanded = false;
            foreach (var entry in current)
            {
                if (part != "*")
                {
                    next.Add(new(Lookup(entry.Value, part), entry.Key));
                }
                else if (entry.Value.ValueKind is JsonValueKind.Array)
                {
                    expanded = true;
                    next.AddRange(entry.Value.EnumerateArray().Select(item => new Entry(item, entry.Key)));
                }
                else if (entry.Value.ValueKind is JsonValueKind.Object)
                {
                    expanded = true;
                    next.AddRange(
                        entry
                            .Value.EnumerateObject()
                            .OrderBy(property => property.Name, StringComparer.Ordinal)
                            .Select(property => new Entry(property.Value, property.Name))
                    );
                }
            }
            current = next;
            if (expanded && current.Count is 0)
            {
                return ([], true);
            }

            if (i == parts.Length - 1)
            {
                if (part == "*")
                {
                    return (current, true);
                }

                return (
                    current
                        .Where(entry => entry.Value.ValueKind is JsonValueKind.Array)
                        .SelectMany(entry => entry.Value.EnumerateArray().Select(item => new Entry(item, entry.Key)))
                        .ToArray(),
                    current.Any(entry => entry.Value.ValueKind is JsonValueKind.Array)
                );
            }
        }
        return (current, true);
    }

    [SuppressMessage(
        "Major Bug",
        "S1244:Floating point numbers should not be tested for equality",
        Justification = "The pinned Go where-filter compares decoded JSON numeric values exactly; an epsilon would change routing predicates."
    )]
    internal static bool Matches(Entry entry, IReadOnlyDictionary<string, JsonElement> where) =>
        where.All(pair =>
        {
            var got = entry.Field(pair.Key);
            if (pair.Value.ValueKind is JsonValueKind.Number)
            {
                if (got.ValueKind is JsonValueKind.Number)
                {
                    return got.GetDouble().Equals(pair.Value.GetDouble());
                }

                return got.ValueKind is JsonValueKind.String
                    && double.TryParse(
                        got.GetString(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var number
                    )
                    && number.Equals(pair.Value.GetDouble());
            }
            return got.ValueKind is JsonValueKind.Undefined
                ? pair.Value.ValueKind is JsonValueKind.Null
                : JsonElement.DeepEquals(got, pair.Value);
        });

    internal static ProviderItem Item(Entry entry, CapabilityMapping mapping, string capability, string root)
    {
        JsonElement Get(string field) => mapping.Fields.TryGetValue(field, out var path) ? entry.Field(path) : default;
        var kind = mapping.Kind.Length > 0 ? mapping.Kind : Kind(capability);
        var path = Relative(String(Get("path")), root);
        var name = String(Get("name"));
        var qualifiedName = String(Get("qualified_name"));
        if (name.Length is 0 && qualifiedName.Length > 0 && kind is not "file")
        {
            name = LastSegment(qualifiedName);
        }
        if (kind is "file" && name.Length is 0 && path.Length > 0)
        {
            name = Path.GetFileName(path);
        }

        if (entry.Value.ValueKind is JsonValueKind.String && mapping.Fields.Count is 0)
        {
            if (kind is "file")
            {
                path = Relative(entry.Value.GetString()!, root);
            }
            else
            {
                name = entry.Value.GetString()!;
            }
        }
        var relatedName = String(Get("related_name"));
        var relatedPath = Relative(String(Get("related_path")), root);
        var members = Get("members");
        var startValue = Get("start_line");
        var startLine = Integer(startValue);
        var endLine = Integer(Get("end_line"));
        if (LineRange(startValue, out var start, out var end))
        {
            startLine = start;
            if (endLine is 0)
            {
                endLine = end;
            }
        }
        return new()
        {
            Kind = kind,
            Name = name,
            QualifiedName = qualifiedName,
            Label = String(Get("label")),
            Path = path,
            StartLine = startLine,
            EndLine = endLine,
            Depth = Integer(Get("depth")),
            Snippet = String(Get("snippet")).Trim(),
            Score = Number(Get("score")),
            Related =
                relatedName.Length > 0 || relatedPath.Length > 0
                    ? new(relatedName, relatedPath, Integer(Get("related_line")))
                    : null,
            Members = members.ValueKind switch
            {
                JsonValueKind.Array => members
                    .EnumerateArray()
                    .Select(String)
                    .Where(member => member.Length > 0)
                    .ToArray(),
                JsonValueKind.String => members
                    .GetString()!
                    .Split(',')
                    .Select(member => member.Trim())
                    .Where(member => member.Length > 0)
                    .ToArray(),
                _ => null,
            },
            Count = Integer(Get("count")),
            Confidence = Number(Get("confidence")),
        };
    }

    internal static IEnumerable<Entry> PatternEntries(Regex pattern, string text)
    {
        var names = pattern
            .GetGroupNames()
            .Where(name => !int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .ToArray();
        foreach (Match match in pattern.Matches(text))
        {
            yield return new(
                HarnessJson.SerializeToElement(
                    names.ToDictionary(
                        name => name,
                        name => WebUtility.HtmlDecode(match.Groups[name].Value),
                        StringComparer.Ordinal
                    )
                )
            );
        }
    }

    internal static string Relative(string path, string root)
    {
        if (path.Length is 0)
        {
            return "";
        }

        if (Path.IsPathFullyQualified(path))
        {
            var relative = Path.GetRelativePath(root, path);
            if (!relative.StartsWith("..", StringComparison.Ordinal))
            {
                path = relative;
            }
        }
        path = path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
        return path.Replace(Path.DirectorySeparatorChar, '/');
    }

    internal static string String(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => "",
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => value
                .GetDouble()
                .ToString("0.###############################", CultureInfo.InvariantCulture),
            _ => value.GetRawText(),
        };

    internal static double Number(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
            JsonValueKind.String
                when double.TryParse(
                    value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var number
                ) && double.IsFinite(number) => number,
            _ => 0,
        };

    private static int Integer(JsonElement value) => (int)Math.Clamp(Number(value), int.MinValue, int.MaxValue);

    private static bool LineRange(JsonElement value, out int start, out int end)
    {
        start = end = 0;
        if (value.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString().AsSpan();
        var separator = text.IndexOf('-');
        return separator >= 0
            && int.TryParse(
                text[..separator].Trim(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out start
            )
            && int.TryParse(
                text[(separator + 1)..].Trim(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out end
            );
    }

    internal static string LastSegment(string text)
    {
        text = text.Trim();
        var dot = text.LastIndexOf('.');
        return
            text.Contains(' ', StringComparison.Ordinal)
            || text.Contains('\t', StringComparison.Ordinal)
            || dot < 0
            || dot == text.Length - 1
            ? text
            : text[(dot + 1)..];
    }

    internal static string FirstLine(string text) => text.Trim().Split('\n')[0];

    internal static string Utf8Prefix(string text, int limit, string suffix)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= limit)
        {
            return text;
        }

        while (limit > 0 && (bytes[limit] & 0xc0) == 0x80)
        {
            limit--;
        }

        return System.Text.Encoding.UTF8.GetString(bytes, 0, limit) + suffix;
    }

    private static string Kind(string capability) =>
        capability switch
        {
            Capabilities.SymbolSearch
            or Capabilities.PackageTypes
            or Capabilities.Callers
            or Capabilities.ArchDuplicates
            or Capabilities.ArchDeadCode => "symbol",
            Capabilities.PackageSearch => "package",
            Capabilities.FileSearch or Capabilities.ArchChangeCoupling => "file",
            Capabilities.ArchDependencies or Capabilities.ArchCycles => "relation",
            _ => "match",
        };
}
