using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text;
using Harness.Core.Projects;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol;

namespace Harness.Mcp;

public static class HarnessMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly IReadOnlyList<Tool> Tools = LoadTools();

    public static async Task RunAsync(Stream input, Stream output, IHarnessStateSession session, HarnessRuntime runtime,
        ILoggerFactory loggerFactory, IServiceProvider? services = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        await using var ownedSession = session;
        await using var transport = new CompatibleStdioTransport(input, output);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "harness", Version = runtime.Version },
            ProtocolVersion = "2025-06-18",
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(new ListToolsResult { Tools = Tools.ToList(), TimeToLive = TimeSpan.Zero, CacheScope = CacheScope.Public });
                },
                CallToolHandler = async (request, token) =>
                {
                    try { return await CallAsync(request.Params, session, runtime, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // The C# SDK intentionally emits no reply for client-cancelled calls.
                        transport.CompleteCancelledRequest(request.JsonRpcRequest.Id);
                        throw;
                    }
                },
            },
        };
        await using var server = McpServer.Create(transport, options, loggerFactory, services);
        await server.RunAsync(cancellationToken);
    }

    internal static async ValueTask<CallToolResult> CallAsync(CallToolRequestParams parameters, IHarnessStateSession session,
        HarnessRuntime runtime, CancellationToken cancellationToken)
    {
        var tool = Tools.FirstOrDefault(tool => tool.Name == parameters.Name)
            ?? throw new McpProtocolException($"unknown tool {JsonSerializer.Serialize(parameters.Name)}", McpErrorCode.InvalidParams);
        if (ValidateArguments(tool, parameters.Arguments) is { } error)
        {
            return Error(error);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (parameters.Name == "ping")
        {
            return Success(new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "pong", ["server"] = "harness", ["version"] = runtime.Version });
        }

        if (parameters.Name == "diagnostics")
        {
            return Success(await session.DiagnosticsAsync(cancellationToken));
        }

        var key = parameters.Arguments!["key"].GetString()!;
        if (key.Length == 0) return Error("key must not be empty");
        if (key.EnumerateRunes().Count() > 256) return Error("key exceeds 256 characters");
        var value = parameters.Name == "state_put_test" ? parameters.Arguments["value"].GetString()! : null;
        if (value is not null && Encoding.UTF8.GetByteCount(value) > 64 * 1024) return Error("value exceeds 65536 bytes");
        try
        {
            if (value is not null)
            {
                await session.PutAsync(key, value, cancellationToken);
                return Success(new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["key"] = key, ["value"] = value });
            }

            var stored = await session.GetAsync(key, cancellationToken);
            SortedDictionary<string, object?> result = new(StringComparer.Ordinal) { ["key"] = key, ["found"] = stored is not null };
            if (!string.IsNullOrEmpty(stored)) result["value"] = stored;
            return Success(result);
        }
        catch (IOException exception)
        {
            return Error(exception.Message);
        }
    }

    private static CallToolResult Success(object value)
    {
        // Go's result adapter writes canonical key order in both representations.
        var element = JsonSerializer.SerializeToElement(value, JsonOptions);
        var ordered = Order(element);
        var json = JsonSerializer.SerializeToElement(ordered, JsonOptions);
        return new CallToolResult { Content = [new TextContentBlock { Text = json.GetRawText() }], StructuredContent = json };
    }

    private static object? Order(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => Order(property.Value), StringComparer.Ordinal)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(Order).ToArray(),
        _ => element,
    };

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static string? ValidateArguments(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        var required = tool.InputSchema.TryGetProperty("required", out var list) ? list.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];
        var given = arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var expected = "\nexpected arguments: required " + (required.Length == 0 ? "nothing" : string.Join(", ", required));
        var prefix = "validating \"arguments\": validating root: ";
        var missing = required.Where(name => !given.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) return prefix + "required: missing properties: " + JsonSerializer.Serialize(missing) + expected;
        var extra = given.Keys.Except(required, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (extra.Length > 0) return prefix + "unexpected additional properties " + JsonSerializer.Serialize(extra) + expected;
        foreach (var name in required)
        {
            var value = given[name];
            if (value.ValueKind == JsonValueKind.String) continue;
            var type = value.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
                JsonValueKind.Null => "null",
                JsonValueKind.Array => "array",
                _ => "object",
            };
            var display = value.ValueKind == JsonValueKind.Null ? "<invalid reflect.Value>" : value.GetRawText();
            return prefix + $"validating /properties/{name}: type: {display} has type \"{type}\", want \"string\"" + expected;
        }

        return null;
    }

    private static Tool[] LoadTools()
    {
        var assembly = typeof(HarnessMcpServer).Assembly;
        return assembly.GetManifestResourceNames().Where(name => name.StartsWith("Harness.Mcp.Schemas.", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                return JsonSerializer.Deserialize<Tool>(stream, McpJsonUtilities.DefaultOptions)!;
            }).ToArray();
    }
}
