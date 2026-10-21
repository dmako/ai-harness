using System.Text;
using System.Text.Json;
using Harness.Core.Telemetry;
using ModelContextProtocol.Protocol;

namespace Harness.Mcp;

internal static class ToolMeasurements
{
    internal static ToolObservation Measure(string tool, IDictionary<string, JsonElement>? arguments, string client, DateTimeOffset at, long durationMs, CallToolResult? result, Exception? exception)
    {
        string Arg(string key) => arguments?.TryGetValue(key, out var v) is true && v.ValueKind is JsonValueKind.String ? v.GetString() ?? "" : "";
        var kind = Arg("kind");
        if (Arg("scope") is "workspace" || tool is "library_adoption" or "similar_concerns")
        {
            kind = (kind + " [workspace " + (Arg("depth") is { Length: > 0 } depth ? depth : "light") + "]").Trim();
        }

        var bytes = result?.Content.OfType<TextContentBlock>().Sum(b => Encoding.UTF8.GetByteCount(b.Text)) ?? 0;
        var error = exception?.Message ?? "";
        var empty = false;
        if (exception is null && result is not null)
        {
            if (result.IsError is true)
            {
                error = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "tool error";
            }

            if (result.StructuredContent is { ValueKind: JsonValueKind.Object } content)
            {
                var lists = content.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.Array).ToArray();
                empty = lists.Length > 0 && lists.All(p => p.Value.GetArrayLength() is 0);
                if (result.IsError is not true && lists.All(p => p.Name is "errors" || p.Value.GetArrayLength() is 0) && content.TryGetProperty("errors", out var errors) && errors.ValueKind is JsonValueKind.Array && errors.GetArrayLength() > 0 && errors.EnumerateArray().All(e => e.ValueKind is JsonValueKind.String))
                { error = "providers failed: " + string.Join("; ", errors.EnumerateArray().Select(e => e.GetString())); empty = false; }
            }
        }
        return new(at, tool, kind, client, durationMs, bytes, empty, error);
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An optional metrics adapter cannot fail the MCP response.")]
    internal static void Observe(IToolObserver? observer, ToolObservation measurement)
    {
        try
        { observer?.Observe(measurement); }
        catch (Exception) { /* Best effort. */ }
    }
}
