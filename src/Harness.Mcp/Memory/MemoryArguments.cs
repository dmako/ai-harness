using System.Text.Json;
using Harness.Core.Serialization;
using ModelContextProtocol.Protocol;

namespace Harness.Mcp.Memory;

/// <summary>Validates the advertised scalar, nested object, array, nullable and anyOf input contracts.</summary>
internal static class MemoryArguments
{
    public static string? Validate(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        var required = tool.InputSchema.TryGetProperty("required", out var requiredSchema)
            ? requiredSchema.EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray()
            : [];
        var properties = tool.InputSchema.TryGetProperty("properties", out var propertiesSchema)
            ? propertiesSchema.EnumerateObject().Select(property => property.Name).ToArray()
            : [];
        var optional = properties.Except(required, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var given = arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var hint =
            "\nexpected arguments: required "
            + (required.Length is 0 ? "nothing" : string.Join(", ", required))
            + (optional.Length is 0 ? "" : "; optional " + string.Join(", ", optional));
        if (given.ContainsKey("path") && tool.Name is "memory_put" or "memory_propose" or "memory_search")
        {
            hint = "\npath: use scope \"path:<dir or file>\" (not path)" + hint;
        }

        if (tool.Name is "task_complete")
        {
            hint +=
                "\nacceptance: [{\"index\": 1, \"result\": \"pass\", \"evidence\": \"...\"}] (one per criterion; [] when the task has none); validation: [\"dotnet test\"] or [{\"command\": \"...\", \"result\": \"pass\"}]";
        }

        const string Prefix = "validating \"arguments\": validating root: ";
        var missing = required.Where(name => !given.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
        {
            return Prefix + "required: missing properties: " + HarnessJson.Serialize(missing) + hint;
        }

        var extra = given.Keys.Except(properties, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (extra.Length > 0)
        {
            return Prefix + "unexpected additional properties " + HarnessJson.Serialize(extra) + hint;
        }

        foreach (var name in given.Keys.Order(StringComparer.Ordinal))
        {
            var error = Check(propertiesSchema.GetProperty(name), given[name], "/properties/" + name);
            if (error is not null)
            {
                return Prefix + error + hint;
            }
        }
        return null;
    }

    private static string? Check(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("anyOf", out var variants))
        {
            return CheckAnyOf(variants, value, path);
        }

        return CheckType(schema, value, path)
            ?? value.ValueKind switch
            {
                JsonValueKind.Array => CheckItems(schema, value, path),
                JsonValueKind.Object => CheckObject(schema, value, path),
                _ => null,
            };
    }

    /// <summary>Null when the value matches a variant, else every variant's error.</summary>
    private static string? CheckAnyOf(JsonElement variants, JsonElement value, string path)
    {
        List<string> errors = [];
        var index = 0;
        foreach (var variant in variants.EnumerateArray())
        {
            var error = Check(
                variant,
                value,
                path + "/anyOf/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            if (error is null)
            {
                return null;
            }

            errors.Add(error);
            index++;
        }
        return "validating "
            + path
            + ": anyOf: did not validate against any of ["
            + string.Join(" ", Enumerable.Repeat("<anonymous schema>", errors.Count))
            + "]:\n"
            + string.Join("\n", errors);
    }

    /// <summary>The error when the value's JSON type is not one the schema allows; an integer is a number.</summary>
    private static string? CheckType(JsonElement schema, JsonElement value, string path)
    {
        var type = schema.GetProperty("type");
        var allowed =
            type.ValueKind is JsonValueKind.Array
                ? type.EnumerateArray().Select(item => item.GetString()).ToArray()
                : [type.GetString()];
        var actual = JsonType(value);
        if (
            allowed.Contains(actual, StringComparer.Ordinal)
            || actual is "integer" && allowed.Contains("number", StringComparer.Ordinal)
        )
        {
            return null;
        }

        var display = value.ValueKind is JsonValueKind.Null ? "<invalid reflect.Value>" : Display(value);
        var want = allowed.Length is 1
            ? HarnessJson.Serialize(allowed[0])
            : "one of " + HarnessJson.Serialize(string.Join(", ", allowed));
        return $"validating {path}: type: {display} has type \"{actual}\", want {want}";
    }

    private static string JsonType(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => value.TryGetDouble(out var number) && double.IsInteger(number)
                ? "integer"
                : "number",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => "array",
            _ => "object",
        };

    private static string? CheckItems(JsonElement schema, JsonElement value, string path)
    {
        foreach (var item in value.EnumerateArray())
        {
            var error = Check(schema.GetProperty("items"), item, path + "/items");
            if (error is not null)
            {
                return "validating " + path + ": " + error;
            }
        }
        return null;
    }

    /// <summary>Required and unexpected fields, then each field against its property schema.</summary>
    private static string? CheckObject(JsonElement schema, JsonElement value, string path)
    {
        var required = schema.TryGetProperty("required", out var requiredSchema)
            ? requiredSchema.EnumerateArray().Select(x => x.GetString()).Order(StringComparer.Ordinal).ToArray()
            : [];
        var fields = value.EnumerateObject().Select(x => x.Name).ToArray();
        var missing = required.Except(fields, StringComparer.Ordinal).ToArray();
        if (missing.Length is not 0)
        {
            return $"validating {path}: required: missing properties: {HarnessJson.Serialize(missing)}";
        }

        var properties = schema.GetProperty("properties");
        var allowedNames = properties.EnumerateObject().Select(p => p.Name).ToArray();
        var extra = fields.Except(allowedNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (extra.Length is not 0)
        {
            return $"validating {path}: unexpected additional properties {HarnessJson.Serialize(extra)}";
        }

        foreach (var name in fields.Order(StringComparer.Ordinal))
        {
            var error = Check(properties.GetProperty(name), value.GetProperty(name), path + "/properties/" + name);
            if (error is not null)
            {
                return "validating " + path + ": " + error;
            }
        }
        return null;
    }

    private static string Display(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Object => "map["
                + string.Join(
                    " ",
                    value
                        .EnumerateObject()
                        .OrderBy(p => p.Name, StringComparer.Ordinal)
                        .Select(p => p.Name + ":" + Display(p.Value))
                )
                + "]",
            JsonValueKind.Array => "[" + string.Join(" ", value.EnumerateArray().Select(Display)) + "]",
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null => "<nil>",
            _ => value.GetRawText(),
        };
}
