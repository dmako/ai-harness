using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Harness.Mcp.Memory;

/// <summary>Validates the object, scalar, nullable and string-array contracts advertised by the implemented tools.</summary>
internal static class MemoryArguments
{
    public static string? Validate(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        var required = tool.InputSchema.TryGetProperty("required", out var requiredSchema) ? requiredSchema.EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray() : [];
        var properties = tool.InputSchema.TryGetProperty("properties", out var propertiesSchema) ? propertiesSchema.EnumerateObject().Select(property => property.Name).ToArray() : [];
        var optional = properties.Except(required, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var given = arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var hint = "\nexpected arguments: required " + (required.Length is 0 ? "nothing" : string.Join(", ", required)) + (optional.Length is 0 ? "" : "; optional " + string.Join(", ", optional));
        if (given.ContainsKey("path") && tool.Name is "memory_put" or "memory_propose" or "memory_search")
            hint = "\npath: use scope \"path:<dir or file>\" (not path)" + hint;
        const string Prefix = "validating \"arguments\": validating root: ";
        var missing = required.Where(name => !given.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
            return Prefix + "required: missing properties: " + JsonSerializer.Serialize(missing) + hint;
        var extra = given.Keys.Except(properties, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (extra.Length > 0)
            return Prefix + "unexpected additional properties " + JsonSerializer.Serialize(extra) + hint;
        foreach (var name in given.Keys.Order(StringComparer.Ordinal))
        {
            var error = Check(propertiesSchema.GetProperty(name), given[name], "/properties/" + name);
            if (error is not null)
                return Prefix + error + hint;
        }
        return null;
    }

    private static string? Check(JsonElement schema, JsonElement value, string path)
    {
        var type = schema.GetProperty("type");
        var allowed = type.ValueKind is JsonValueKind.Array ? type.EnumerateArray().Select(item => item.GetString()).ToArray() : [type.GetString()];
        var actual = value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => value.TryGetDouble(out var number) && double.IsInteger(number) ? "integer" : "number",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => "array",
            _ => "object",
        };
        if (!allowed.Contains(actual, StringComparer.Ordinal) && !(actual is "integer" && allowed.Contains("number", StringComparer.Ordinal)))
        {
            var display = value.ValueKind switch { JsonValueKind.Null => "<invalid reflect.Value>", JsonValueKind.String => value.GetString(), _ => value.GetRawText() };
            var want = allowed.Length is 1 ? JsonSerializer.Serialize(allowed[0]) : "one of " + JsonSerializer.Serialize(string.Join(", ", allowed));
            return $"validating {path}: type: {display} has type \"{actual}\", want {want}";
        }
        if (value.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var error = Check(schema.GetProperty("items"), item, path + "/items");
                if (error is not null)
                    return "validating " + path + ": " + error;
            }
        }
        return null;
    }
}
