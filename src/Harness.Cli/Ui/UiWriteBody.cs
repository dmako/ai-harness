using System.Text.Json.Nodes;
using Harness.Core.Web;

namespace Harness.Cli.Ui;

internal sealed class UiWriteBody
{
    private readonly Dictionary<string, JsonNode?> _values;

    internal UiWriteBody(JsonObject body, params string[] fields)
    {
        _values = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in body)
        {
            if (!fields.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new UiRequestException(400, "invalid request body: unknown field \"" + name + "\"");
            }
            if (!_values.TryAdd(name, value))
            {
                throw new UiRequestException(400, "invalid request body: repeated field \"" + name + "\"");
            }
            if (!Valid(name, value))
            {
                throw new UiRequestException(400, "invalid request body: wrong type for \"" + name + "\"");
            }
        }
    }

    private static bool Valid(string name, JsonNode? value) =>
        value is null
        || (
            name.ToUpperInvariant() switch
            {
                "TASK" => value is JsonObject,
                "PROJECT_IDS" or "DEPENDS_ON" or "ACCEPTANCE" or "SCOPE" or "SUPERSEDES" => value is JsonArray items
                    && items.All(item => item is null || item is JsonValue scalar && scalar.TryGetValue<string>(out _)),
                "KEEP_BOTH" or "DRY_RUN" or "FORCE" => value is JsonValue flag && flag.TryGetValue<bool>(out _),
                _ => value is JsonValue text && text.TryGetValue<string>(out _),
            }
        );

    internal string Text(string name) => _values.GetValueOrDefault(name)?.GetValue<string>() ?? "";

    internal string? Optional(string name) => _values.GetValueOrDefault(name)?.GetValue<string>();

    internal bool Flag(string name) => _values.GetValueOrDefault(name)?.GetValue<bool>() ?? false;

    internal string[] List(string name) =>
        _values.GetValueOrDefault(name)?.AsArray().Select(n => n?.GetValue<string>() ?? "").ToArray() ?? [];

    internal JsonObject? Object(string name) => _values.GetValueOrDefault(name)?.AsObject();
}
