using System.Text;
using System.Text.Json;
using Harness.Core.Telemetry;
using ModelContextProtocol.Protocol;

namespace Harness.Mcp;

internal static class ToolMeasurements
{
    internal static ToolObservation Measure(
        string tool,
        IDictionary<string, JsonElement>? arguments,
        string client,
        DateTimeOffset at,
        long durationMs,
        CallToolResult? result,
        Exception? exception
    )
    {
        var bytes = result?.Content.OfType<TextContentBlock>().Sum(b => Encoding.UTF8.GetByteCount(b.Text)) ?? 0;
        var (empty, error) =
            exception is null && result is not null ? Outcome(result) : (false, exception?.Message ?? "");
        return new(at, tool, Kind(tool, arguments), client, durationMs, bytes, empty, error);
    }

    /// <summary>The kind argument, marked with the workspace depth for workspace searches and evidence tools.</summary>
    private static string Kind(string tool, IDictionary<string, JsonElement>? arguments)
    {
        var kind = Argument(arguments, "kind");
        if (Argument(arguments, "scope") is not "workspace" && tool is not ("library_adoption" or "similar_concerns"))
        {
            return kind;
        }

        var depth = Argument(arguments, "depth") is { Length: > 0 } given ? given : "light";
        return (kind + " [workspace " + depth + "]").Trim();
    }

    private static string Argument(IDictionary<string, JsonElement>? arguments, string key) =>
        arguments?.TryGetValue(key, out var value) is true && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>A tool error's first text; an answer is empty when all its lists are empty.</summary>
    private static (bool Empty, string Error) Outcome(CallToolResult result)
    {
        var failed = result.IsError is true;
        var error = failed
            ? result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "tool error"
            : "";
        return result.StructuredContent is { ValueKind: JsonValueKind.Object } content
            ? Structured(content, failed, error)
            : (false, error);
    }

    /// <summary>
    /// An answer whose only non-empty list is a list of provider error messages failed, rather than
    /// being empty.
    /// </summary>
    private static (bool Empty, string Error) Structured(JsonElement content, bool failed, string error)
    {
        var lists = content.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.Array).ToArray();
        if (!failed && ProviderErrors(content, lists) is { } errors)
        {
            return (false, "providers failed: " + errors);
        }

        return (lists.Length > 0 && lists.All(p => p.Value.GetArrayLength() is 0), error);
    }

    private static string? ProviderErrors(JsonElement content, JsonProperty[] lists) =>
        lists.All(p => p.Name is "errors" || p.Value.GetArrayLength() is 0)
        && content.TryGetProperty("errors", out var errors)
        && errors.ValueKind is JsonValueKind.Array
        && errors.GetArrayLength() > 0
        && errors.EnumerateArray().All(e => e.ValueKind is JsonValueKind.String)
            ? string.Join("; ", errors.EnumerateArray().Select(e => e.GetString()))
            : null;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "An optional metrics adapter cannot fail the MCP response."
    )]
    internal static void Observe(IToolObserver? observer, ToolObservation measurement)
    {
        try
        {
            observer?.Observe(measurement);
        }
        catch (Exception)
        { /* Best effort. */
        }
    }
}
