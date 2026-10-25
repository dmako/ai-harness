using System.Text.Json;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Harness.Providers.Protocol;

/// <summary>Exposes a provider over the same v1 contract consumed by ProtocolProvider; takes provider ownership.</summary>
public static class ProtocolServer
{
    public static async Task RunAsync(
        IProvider provider,
        string version,
        Stream input,
        Stream output,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default
    )
    {
        await using var ownedProvider = provider;
        var capabilities = provider.Capabilities();
        var tools = capabilities
            .Select(cap => new Tool
            {
                Name = ProviderProtocol.ToolFor(cap.Capability),
                Description = "Harness capability " + cap.Capability + " (provider protocol v1).",
                InputSchema = RequestSchema(false),
            })
            .ToList();
        tools.Add(
            new()
            {
                Name = ProviderProtocol.InfoTool,
                Description = "Harness provider protocol handshake: protocol version, provider name and capabilities.",
                InputSchema = HarnessJson.Deserialize<JsonElement>(
                    """{"type":"object","additionalProperties":false}"""
                ),
            }
        );
        if (provider is IProviderRefresher)
        {
            tools.Add(
                new()
                {
                    Name = ProviderProtocol.RefreshTool,
                    Description =
                        "Bring the provider's index up to date after files changed (full rebuild or changed paths).",
                    InputSchema = RequestSchema(true),
                }
            );
        }

        await using var transport = new StreamServerTransport(input, output, provider.Name, loggerFactory);
        var options = new McpServerOptions
        {
            ServerInfo = new() { Name = provider.Name, Version = version },
            Capabilities = new() { Tools = new() },
            Handlers = new()
            {
                ListToolsHandler = (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(new ListToolsResult { Tools = tools });
                },
                CallToolHandler = async (request, token) =>
                {
                    var name = request.Params.Name;
                    if (!tools.Any(tool => tool.Name == name))
                    {
                        throw new McpProtocolException("unknown tool " + name, McpErrorCode.InvalidParams);
                    }

                    try
                    {
                        var args = HarnessJson.SerializeToElement(
                            request.Params.Arguments ?? new Dictionary<string, JsonElement>()
                        );
                        var schema = tools.First(tool => tool.Name == name).InputSchema;
                        Validate(args, schema);
                        object result;
                        if (name == ProviderProtocol.InfoTool)
                        {
                            result = new ProviderInfo(
                                ProviderProtocol.Version,
                                provider.Name,
                                capabilities.Count is 0
                                    ? null
                                    : capabilities
                                        .Select(cap => new ProtocolCapability(
                                            cap.Capability,
                                            cap.Freshness,
                                            cap.Cost,
                                            cap.Latency,
                                            cap.Evidence ?? ""
                                        ))
                                        .ToArray(),
                                provider is IProviderRefresher
                            );
                        }
                        else if (name == ProviderProtocol.RefreshTool && provider is IProviderRefresher refresher)
                        {
                            var refreshed = await refresher
                                .RefreshAsync(
                                    HarnessJson.Deserialize<RefreshRequest>(args, ProviderProtocol.JsonOptions)!,
                                    token
                                )
                                .ConfigureAwait(false);
                            var envelope = new Dictionary<string, object?>(StringComparer.Ordinal);
                            if (refreshed.Skipped)
                            {
                                envelope["skipped"] = true;
                            }

                            if (refreshed.Detail.Length > 0)
                            {
                                envelope["detail"] = refreshed.Detail;
                            }

                            result = envelope;
                        }
                        else
                        {
                            var capability = capabilities
                                .First(cap => ProviderProtocol.ToolFor(cap.Capability) == name)
                                .Capability;
                            var inputRequest = HarnessJson.Deserialize<CapabilityRequest>(
                                args,
                                ProviderProtocol.JsonOptions
                            )!;
                            var answer = await provider
                                .ExecuteAsync(
                                    new(
                                        capability,
                                        inputRequest.Query,
                                        inputRequest.Path,
                                        inputRequest.Limit,
                                        inputRequest.Target,
                                        inputRequest.Budget,
                                        inputRequest.Package,
                                        inputRequest.Version,
                                        inputRequest.Type
                                    ),
                                    token
                                )
                                .ConfigureAwait(false);
                            if (answer.Error is { } error)
                            {
                                throw error;
                            }

                            result = new ProtocolResponse(
                                answer.Items,
                                answer.Truncated,
                                answer.Summary is { Count: > 0 } ? answer.Summary : null,
                                answer.Text
                            );
                        }
                        var body = HarnessJson.SerializeToElement(result, ProviderProtocol.JsonOptions);
                        return new CallToolResult
                        {
                            StructuredContent = body,
                            Content = [new TextContentBlock { Text = body.GetRawText() }],
                        };
                    }
                    catch (Exception error)
                        when (error
                                is IOException
                                    or JsonException
                                    or ArgumentException
                                    or UnsupportedCapabilityException
                                    or ProviderNotIndexedException
                                    or ProviderException
                                    or InvalidOperationException
                        )
                    {
                        return new CallToolResult
                        {
                            IsError = true,
                            Content = [new TextContentBlock { Text = CredentialRedactor.Redact(error.Message) }],
                        };
                    }
                },
            },
        };
        await using var server = McpServer.Create(transport, options, loggerFactory);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement RequestSchema(bool refresh) =>
        HarnessJson.Deserialize<JsonElement>(
            refresh
                ? """
                {"type":"object","additionalProperties":false,"properties":{"full":{"type":"boolean"},"changed":{"type":["null","array"],"items":{"type":"string"}}}}
                """
                : """
                {"type":"object","additionalProperties":false,"properties":{"query":{"type":"string"},"path":{"type":"string"},"limit":{"type":"integer"},"target":{"type":"string"},"budget":{"type":"integer"},"package":{"type":"string"},"version":{"type":"string"},"type":{"type":"string"}}}
                """
        );

    private static void Validate(JsonElement args, JsonElement schema)
    {
        foreach (var property in args.EnumerateObject())
        {
            if (
                !schema.TryGetProperty("properties", out var properties)
                || !properties.TryGetProperty(property.Name, out var field)
            )
            {
                throw new ArgumentException("unexpected additional property " + property.Name);
            }

            var valid =
                field.GetProperty("type").ValueKind is JsonValueKind.Array
                    ? property.Value.ValueKind is JsonValueKind.Null
                        || property.Value.ValueKind is JsonValueKind.Array
                            && property.Value.EnumerateArray().All(item => item.ValueKind is JsonValueKind.String)
                    : field.GetProperty("type").GetString() switch
                    {
                        "string" => property.Value.ValueKind is JsonValueKind.String,
                        "integer" => property.Value.ValueKind is JsonValueKind.Number
                            && property.Value.TryGetInt32(out _),
                        "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                        _ => false,
                    };
            if (!valid)
            {
                throw new ArgumentException("invalid argument " + property.Name);
            }
        }
    }

    internal sealed record CapabilityRequest(
        string Query = "",
        string Path = "",
        int Limit = 0,
        string Target = "",
        int Budget = 0,
        string Package = "",
        string Version = "",
        string Type = ""
    );
}
