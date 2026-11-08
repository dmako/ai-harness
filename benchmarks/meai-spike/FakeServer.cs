using System.Text;
using System.Text.Json;

namespace MeaiSpike;

/// <summary>
/// A minimal MCP server over stdio (newline-delimited JSON-RPC) answering every tools/call with the same JSON body of
/// about <c>bytes</c> bytes, shaped like ripwire's grep ({"hits":[{file,line,in}]}). It keeps the provider's own
/// work constant, so the benchmark sees only the client side: the SDK, the call path and the mapping.
/// </summary>
internal static class FakeServer
{
    public static async Task<int> RunAsync(int bytes)
    {
        var payload = Payload(bytes);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var output = Console.OpenStandardOutput();
        string? line;
        while ((line = await input.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (line.Length is 0)
            {
                continue;
            }
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                continue; // a notification
            }
            var method = root.GetProperty("method").GetString();
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id");
                id.WriteTo(writer);
                writer.WritePropertyName("result");
                writer.WriteStartObject();
                switch (method)
                {
                    case "initialize":
                        writer.WriteString(
                            "protocolVersion",
                            root.GetProperty("params").GetProperty("protocolVersion").GetString()
                        );
                        writer.WriteStartObject("capabilities");
                        writer.WriteStartObject("tools");
                        writer.WriteEndObject();
                        writer.WriteEndObject();
                        writer.WriteStartObject("serverInfo");
                        writer.WriteString("name", "fake");
                        writer.WriteString("version", "1.0.0");
                        writer.WriteEndObject();
                        break;
                    case "tools/list":
                        writer.WriteStartArray("tools");
                        foreach (var name in new[] { "grep", "grep_b", "miss" })
                        {
                            writer.WriteStartObject();
                            writer.WriteString("name", name);
                            writer.WriteString("description", "fixed answer");
                            writer.WriteStartObject("inputSchema");
                            writer.WriteString("type", "object");
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                        break;
                    case "tools/call" when root.GetProperty("params").GetProperty("name").GetString() is "miss":
                        writer.WriteStartArray("content");
                        writer.WriteStartObject();
                        writer.WriteString("type", "text");
                        writer.WriteString("text", "symbol not found");
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                        writer.WriteBoolean("isError", true);
                        break;
                    case "tools/call":
                        writer.WriteStartArray("content");
                        writer.WriteStartObject();
                        writer.WriteString("type", "text");
                        writer.WriteString("text", payload);
                        writer.WriteEndObject();
                        writer.WriteEndArray();
                        break;
                    default:
                        break; // ping and the rest: an empty result
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            buffer.WriteByte((byte)'\n');
            await output.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        return 0;
    }

    private static string Payload(int bytes)
    {
        var text = new StringBuilder("{\"hits\":[");
        for (var i = 0; text.Length < bytes; i++)
        {
            text.Append(i is 0 ? "" : ",")
                .Append("{\"file\":\"src/Harness.Providers/Mcp/GenericMcpProvider.cs\",\"line\":")
                .Append(i + 1)
                .Append(",\"in\":\"GenericMcpProvider.ExecuteAsync\"}");
        }
        return text.Append("]}").ToString();
    }
}
