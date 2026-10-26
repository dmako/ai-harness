using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Harness.Core.Architecture;
using Harness.Core.Memory;
using Harness.Core.Projects;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Harness.Mcp.Architecture;
using Harness.Mcp.Context;
using Harness.Mcp.Memory;
using Harness.Mcp.Providers;
using Harness.Mcp.Tasks;
using Harness.Mcp.Workspaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Harness.Mcp;

public static class HarnessMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly IReadOnlyList<Tool> Tools = LoadTools();

    public static Task RunAsync(
        Stream input,
        Stream output,
        IHarnessStateSession session,
        HarnessRuntime runtime,
        ILoggerFactory loggerFactory,
        IServiceProvider? services = null,
        IProviderRegistry? providers = null,
        WorkspaceTools? workspaceTools = null,
        CancellationToken cancellationToken = default
    ) =>
        RunAsync(
            input,
            output,
            session,
            runtime,
            loggerFactory,
            services,
            providers,
            workspaceTools,
            null,
            cancellationToken
        );

    public static async Task RunAsync(
        Stream input,
        Stream output,
        IHarnessStateSession session,
        HarnessRuntime runtime,
        ILoggerFactory loggerFactory,
        IServiceProvider? services,
        IProviderRegistry? providers,
        WorkspaceTools? workspaceTools,
        Harness.Core.Telemetry.IToolObserver? observer,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        await using var ownedSession = session;
        var providerTools = new ProviderTools(providers, session as IProviderProjectSession, workspaceTools);
        var couplingTools = new CouplingTools(providers, session as IChangeCouplingSession);
        var contextTools = new ContextTools(providers, session as IContextProjectSession, workspaceTools);
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
                    return ValueTask.FromResult(
                        new ListToolsResult
                        {
                            Tools = Tools.ToList(),
                            TimeToLive = TimeSpan.Zero,
                            CacheScope = CacheScope.Public,
                        }
                    );
                },
                CallToolHandler = async (request, token) =>
                {
                    var client = request.Server.ClientInfo;
                    var version = string.IsNullOrEmpty(client?.Version) ? "" : " " + client.Version;
                    var caller = string.IsNullOrEmpty(client?.Name) ? "unknown" : client.Name + version;
                    var at = DateTimeOffset.UtcNow;
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    CallToolResult? result = null;
                    Exception? failure = null;
                    try
                    {
                        return result = await CallAsync(
                            request.Params,
                            session,
                            runtime,
                            token,
                            caller,
                            providerTools,
                            contextTools,
                            workspaceTools,
                            couplingTools
                        );
                    }
                    catch (OperationCanceledException error) when (token.IsCancellationRequested)
                    {
                        failure = error;
                        // The C# SDK intentionally emits no reply for client-cancelled calls.
                        transport.CompleteCancelledRequest(request.JsonRpcRequest.Id);
                        throw;
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        throw;
                    }
                    finally
                    {
                        ToolMeasurements.Observe(
                            observer,
                            ToolMeasurements.Measure(
                                request.Params.Name,
                                request.Params.Arguments,
                                client?.Name ?? "",
                                at,
                                (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                                result,
                                failure
                            )
                        );
                    }
                },
            },
        };
        await using var server = McpServer.Create(transport, options, loggerFactory, services);
        await server.RunAsync(cancellationToken);
    }

    internal static async ValueTask<CallToolResult> CallAsync(
        CallToolRequestParams parameters,
        IHarnessStateSession session,
        HarnessRuntime runtime,
        CancellationToken cancellationToken,
        string caller = "unknown",
        ProviderTools? providerTools = null,
        ContextTools? contextTools = null,
        WorkspaceTools? workspaceTools = null,
        CouplingTools? couplingTools = null
    )
    {
        var tool =
            Tools.FirstOrDefault(tool => tool.Name == parameters.Name)
            ?? throw new McpProtocolException(
                $"unknown tool {HarnessJson.Serialize(parameters.Name)}",
                McpErrorCode.InvalidParams
            );
        if (ValidateArguments(tool, parameters.Arguments) is { } error)
        {
            return Error(error);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return parameters.Name switch
        {
            "ping" => Success(
                new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["message"] = "pong",
                    ["server"] = "harness",
                    ["version"] = runtime.Version,
                }
            ),
            "diagnostics" => Success(await session.DiagnosticsAsync(cancellationToken)),
            "state_put_test" or "state_get_test" => await StateTestAsync(parameters, session, cancellationToken),
            _ => await ToolAsync(
                new(parameters, session, caller, cancellationToken),
                new(providerTools, contextTools, workspaceTools, couplingTools)
            ),
        };
    }

    /// <summary>One tool call: its parameters, the session, who calls and the cancellation token.</summary>
    private sealed record ToolCall(
        CallToolRequestParams Parameters,
        IHarnessStateSession Session,
        string Caller,
        CancellationToken Token
    )
    {
        public string Name => Parameters.Name;
        public IDictionary<string, JsonElement>? Arguments => Parameters.Arguments;
    }

    /// <summary>The tool handlers the server was started with; each falls back to one over the session.</summary>
    private sealed record ToolSet(
        ProviderTools? Providers,
        ContextTools? Context,
        WorkspaceTools? Workspace,
        CouplingTools? Coupling
    );

    /// <summary>Runs a tool; a regex timeout or an expected failure becomes a tool error.</summary>
    private static async Task<CallToolResult> ToolAsync(ToolCall call, ToolSet tools)
    {
        try
        {
            return await DispatchAsync(call, tools);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return Error(TimeoutMessage(call.Name));
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or InvalidDataException
                        or JsonException
                        or ArgumentException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or FormatException
                        or OverflowException
                        or UnauthorizedAccessException
            )
        {
            return Error(exception.Message);
        }
    }

    private static string TimeoutMessage(string name) =>
        name switch
        {
            "architecture_findings" or "architecture_propose" =>
                "architecture analysis timed out while matching source evidence",
            "library_adoption" or "similar_concerns" => "workspace analysis timed out while matching source evidence",
            _ => (name.StartsWith("task_", StringComparison.Ordinal) ? "task" : "memory")
                + " content rejected: credential validation timed out",
        };

    private static Task<CallToolResult> DispatchAsync(ToolCall call, ToolSet tools) =>
        call.Name switch
        {
            "architecture_findings" or "architecture_propose" => FindingsAsync(call),
            "library_adoption" or "similar_concerns" => EvidenceAsync(call, tools.Workspace),
            "change_coupling" => CouplingAsync(call, tools.Coupling),
            "get_context" => ContextAsync(call, tools.Context),
            _ when ProviderTools.Handles(call.Name) => ProvidersAsync(call, tools.Providers),
            _ when call.Name.StartsWith("task_", StringComparison.Ordinal) => TasksAsync(call),
            _ => MemoryAsync(call),
        };

    private static async Task<CallToolResult> FindingsAsync(ToolCall call) =>
        call.Session is Harness.Core.Findings.IFindingSession findings
            ? Success(await FindingsTools.CallAsync(call.Name, call.Arguments, findings, call.Caller, call.Token))
            : Error("finding session unavailable");

    private static async Task<CallToolResult> EvidenceAsync(ToolCall call, WorkspaceTools? workspace) =>
        workspace is null
            ? Error("this project is not in a workspace; use 'harness workspace create' to add it")
            : Success(await workspace.EvidenceAsync(call.Name, call.Arguments, call.Token));

    private static async Task<CallToolResult> CouplingAsync(ToolCall call, CouplingTools? coupling) =>
        Success(
            await (coupling ?? new CouplingTools(null, call.Session as IChangeCouplingSession)).CallAsync(
                call.Arguments,
                call.Token
            )
        );

    private static async Task<CallToolResult> ContextAsync(ToolCall call, ContextTools? context) =>
        Success(
            await (context ?? new ContextTools(project: call.Session as IContextProjectSession)).CallAsync(
                call.Arguments,
                call.Token
            )
        );

    private static async Task<CallToolResult> ProvidersAsync(ToolCall call, ProviderTools? providers) =>
        Success(
            await (providers ?? new ProviderTools(project: call.Session as IProviderProjectSession)).CallAsync(
                call.Name,
                call.Arguments,
                call.Token
            )
        );

    private static async Task<CallToolResult> TasksAsync(ToolCall call) =>
        call.Session is ITaskSession tasks
            ? Success(
                await TaskTools.CallAsync(
                    call.Name,
                    call.Arguments,
                    tasks,
                    call.Session as IMemorySession,
                    call.Caller,
                    call.Token
                )
            )
            : Error("task session unavailable");

    private static async Task<CallToolResult> MemoryAsync(ToolCall call) =>
        call.Session is IMemorySession memory
            ? Success(await MemoryTools.CallAsync(call.Name, call.Arguments, memory, call.Caller, call.Token))
            : Error("memory session unavailable");

    /// <summary>The state_put_test and state_get_test tools: bounded keys and values in the state store.</summary>
    private static async Task<CallToolResult> StateTestAsync(
        CallToolRequestParams parameters,
        IHarnessStateSession session,
        CancellationToken cancellationToken
    )
    {
        var key = parameters.Arguments!["key"].GetString()!;
        if (key.Length == 0)
        {
            return Error("key must not be empty");
        }

        if (key.EnumerateRunes().Count() > 256)
        {
            return Error("key exceeds 256 characters");
        }

        var value = parameters.Name == "state_put_test" ? parameters.Arguments["value"].GetString()! : null;
        if (value is not null && Encoding.UTF8.GetByteCount(value) > 64 * 1024)
        {
            return Error("value exceeds 65536 bytes");
        }

        try
        {
            return value is null
                ? await StateGetAsync(session, key, cancellationToken)
                : await StatePutAsync(session, key, value, cancellationToken);
        }
        catch (IOException exception)
        {
            return Error(exception.Message);
        }
    }

    private static async Task<CallToolResult> StatePutAsync(
        IHarnessStateSession session,
        string key,
        string value,
        CancellationToken cancellationToken
    )
    {
        await session.PutAsync(key, value, cancellationToken);
        return Success(
            new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["key"] = key, ["value"] = value }
        );
    }

    private static async Task<CallToolResult> StateGetAsync(
        IHarnessStateSession session,
        string key,
        CancellationToken cancellationToken
    )
    {
        var stored = await session.GetAsync(key, cancellationToken);
        SortedDictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["key"] = key,
            ["found"] = stored is not null,
        };
        if (!string.IsNullOrEmpty(stored))
        {
            result["value"] = stored;
        }

        return Success(result);
    }

    private static CallToolResult Success(object value)
    {
        if (value is ContextAnswer answer)
        {
            using var document = JsonDocument.Parse(answer.Json);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = answer.Json }],
                StructuredContent = document.RootElement.Clone(),
            };
        }
        // Use canonical key order in both result representations.
        var element = HarnessJson.SerializeToElement(value, JsonOptions);
        var ordered = Order(element);
        var json = HarnessJson.SerializeToElement(ordered, JsonOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = json.GetRawText() }],
            StructuredContent = json,
        };
    }

    private static object? Order(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element
                .EnumerateObject()
                .ToDictionary(property => property.Name, property => Order(property.Value), StringComparer.Ordinal)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(Order).ToArray(),
            _ => element,
        };

    private static CallToolResult Error(string message)
    {
        string safe;
        try
        {
            safe = message.StartsWith("validating \"arguments\"", StringComparison.Ordinal)
                ? CredentialRedactor.Redact(message)
                : message;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            safe = "request rejected: diagnostic redaction timed out";
        }
        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = safe }] };
    }

    private static string? ValidateArguments(Tool tool, IDictionary<string, JsonElement>? arguments) =>
        MemoryArguments.Validate(tool, arguments);

    private static Tool[] LoadTools()
    {
        var assembly = typeof(HarnessMcpServer).Assembly;
        return assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith("Harness.Mcp.Schemas.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                return HarnessJson.Deserialize<Tool>(stream, McpJsonUtilities.DefaultOptions)!;
            })
            .ToArray();
    }
}
