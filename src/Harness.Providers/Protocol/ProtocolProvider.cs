using System.Text;
using System.Text.Json;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Providers.Mcp;
using ModelContextProtocol.Protocol;

namespace Harness.Providers.Protocol;

/// <summary>A lazy harness-provider/1 adapter with one cached handshake and bounded failed retries.</summary>
public sealed class ProtocolProvider : IProviderRefresher, IVersionedProvider, IProviderDiscovery
{
    private readonly McpSupervisor _supervisor;
    private readonly SemaphoreSlim _handshake = new(1, 1);
    private readonly TimeProvider _clock;
    private ProviderInfo? _info;
    private Exception? _infoError;
    private DateTimeOffset _tried;
    private readonly object _closeGate = new();
    private Task? _closeTask;
    private bool _closed;

    public ProtocolProvider(
        string name,
        ProviderConfiguration configuration,
        string root,
        IProviderProcessFactory factory,
        IReadOnlyDictionary<string, string?>? environment = null,
        McpRestartPolicy? policy = null,
        TimeProvider? clock = null,
        bool allowDetachedChildren = false
    )
    {
        Name = name;
        _clock = clock ?? TimeProvider.System;
        _supervisor = new(
            factory,
            new(
                name,
                new(configuration.Command, configuration.Args)
                {
                    AllowDetachedChildren = allowDetachedChildren,
                    WorkingDirectory = configuration.Dir is "project" ? root : "",
                    Environment = environment ?? new Dictionary<string, string?>(),
                }
            )
            {
                CallTimeout =
                    configuration.TimeoutMs > 0
                        ? TimeSpan.FromMilliseconds(configuration.TimeoutMs)
                        : TimeSpan.FromSeconds(30),
            },
            policy,
            _clock
        );
    }

    public string Name { get; }
    public string ServerVersion => _supervisor.ServerVersion;

    public async Task DiscoverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await HandshakeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsProviderFailure(error))
        { /* Health reports discovery failures; other providers remain usable. */
        }
    }

    public IReadOnlyList<CapabilityInfo> Capabilities()
    {
        try
        {
            var info = _info ?? HandshakeAsync(CancellationToken.None).GetAwaiter().GetResult();
            return (info.Capabilities ?? [])
                .Select(cap => new CapabilityInfo(
                    cap.Capability,
                    Name,
                    Hint(cap.Cost),
                    Hint(cap.Latency),
                    cap.Freshness is "indexed" ? "indexed" : "live",
                    Core.Providers.Capabilities.IsPackage(cap.Capability) ? "external" : "project",
                    cap.Evidence is "exact" or "inferred" ? cap.Evidence : null
                ))
                .ToArray();
        }
        catch (Exception error) when (IsProviderFailure(error))
        {
            return [];
        }
    }

    public async ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default)
    {
        if (!probe)
        {
            return _supervisor.Health();
        }

        try
        {
            var info = await HandshakeAsync(cancellationToken).ConfigureAwait(false);
            var health = _supervisor.Health();
            return health with
            {
                Detail =
                    health.Detail
                    + "; "
                    + info.Name
                    + ", protocol "
                    + info.Protocol
                    + ": "
                    + string.Join(", ", (info.Capabilities ?? []).Select(cap => cap.Capability)),
            };
        }
        catch (Exception error) when (IsProviderFailure(error))
        {
            return new(
                "unavailable",
                "handshake failed: " + Safe(error.Message),
                "check that the command starts on its own and implements harness provider protocol v1 ("
                    + ProviderProtocol.InfoTool
                    + ")"
            );
        }
    }

    public async Task<ProviderResult> ExecuteAsync(
        ProviderRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ProviderInfo info;
        try
        {
            info = await HandshakeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsProviderFailure(error))
        {
            return new()
            {
                Error = new ProviderException(Name, "handshake failed: " + Safe(error.Message), innerException: error),
                TimedOut = error is TimeoutException,
            };
        }
        if (!(info.Capabilities ?? []).Any(cap => cap.Capability == request.Capability))
        {
            return new() { Error = new UnsupportedCapabilityException() };
        }

        var tool = ProviderProtocol.ToolFor(request.Capability);
        var bytes = 0;
        try
        {
            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (
                var (key, value) in new Dictionary<string, object?>
                {
                    ["query"] = request.Query,
                    ["path"] = request.Path,
                    ["limit"] = request.Limit,
                    ["target"] = request.Target,
                    ["budget"] = request.Budget,
                    ["package"] = request.Package,
                    ["version"] = request.Version,
                    ["type"] = request.Type,
                }
            )
            {
                if (value is not ("" or 0))
                {
                    arguments[key] = value;
                }
            }

            var client = await _supervisor.ClientAsync(cancellationToken).ConfigureAwait(false);
            var result = await client.CallToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
            bytes = Encoding.UTF8.GetByteCount(McpMapping.Text(result));
            var response = Decode<ProtocolResponse>(result, tool);
            if ((response.Items ?? []).Any(item => item is null))
            {
                throw new InvalidDataException("unexpected response: null item");
            }

            var items = (response.Items ?? [])
                .Select(item =>
                    item with
                    {
                        Provider = Name,
                        Tool = string.IsNullOrEmpty(item.Tool) ? tool : item.Tool,
                    }
                )
                .ToArray();
            return new()
            {
                Items = request.Limit > 0 ? items.Take(request.Limit).ToArray() : items,
                Truncated = response.Truncated || request.Limit > 0 && items.Length > request.Limit,
                Summary = response.Summary,
                Text = response.Text ?? "",
                Bytes = bytes,
            };
        }
        catch (Exception error) when (IsProviderFailure(error))
        {
            return new()
            {
                Bytes = bytes,
                Error =
                    error is ProviderException
                        ? error
                        : new ProviderException(Name, Safe(error.Message), tool, innerException: error),
                TimedOut = error is TimeoutException,
            };
        }
    }

    public async Task<RefreshResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
    {
        var info = await HandshakeAsync(cancellationToken).ConfigureAwait(false);
        if (!info.Refresh)
        {
            return new(true, "provider keeps no index");
        }

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (request.Full)
        {
            arguments["full"] = true;
        }

        if (request.Changed is { Count: > 0 })
        {
            arguments["changed"] = request.Changed;
        }

        var client = await _supervisor.ClientAsync(cancellationToken).ConfigureAwait(false);
        return Decode<RefreshResult>(
            await client
                .CallToolAsync(ProviderProtocol.RefreshTool, arguments, cancellationToken)
                .ConfigureAwait(false),
            ProviderProtocol.RefreshTool
        );
    }

    // The supervisor owns process/session shutdown; the semaphore stays usable for concurrent callers completing cancellation.
    public ValueTask DisposeAsync()
    {
        lock (_closeGate)
        {
            return new(_closeTask ??= CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        lock (_closeGate)
        {
            _closed = true;
        }

        try
        {
            await _supervisor.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _handshake.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _handshake.Release();
            _handshake.Dispose();
        }
    }

    private async Task<ProviderInfo> HandshakeAsync(CancellationToken cancellationToken)
    {
        lock (_closeGate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
        }

        await _handshake.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_info is not null)
            {
                return _info;
            }

            if (_infoError is not null && _clock.GetUtcNow() - _tried < TimeSpan.FromSeconds(30))
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_infoError).Throw();
            }

            _tried = _clock.GetUtcNow();
            _infoError = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                var client = await _supervisor.ClientAsync(linked.Token).ConfigureAwait(false);
                var info = Decode<ProviderInfo>(
                    await client
                        .CallToolAsync(ProviderProtocol.InfoTool, new Dictionary<string, object?>(), linked.Token)
                        .ConfigureAwait(false),
                    ProviderProtocol.InfoTool
                );
                if (!ProviderProtocol.Compatible(info.Protocol ?? ""))
                {
                    throw new InvalidDataException(
                        "speaks "
                            + JsonSerializer.Serialize(info.Protocol)
                            + "; harness needs "
                            + ProviderProtocol.Version
                    );
                }

                if ((info.Capabilities ?? []).Any(cap => cap is null || string.IsNullOrEmpty(cap.Capability)))
                {
                    throw new InvalidDataException("unexpected response: capability name is missing");
                }

                _infoError = null;
                return _info = info;
            }
            catch (OperationCanceledException error)
                when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var timeout = new TimeoutException(Name + ": handshake timed out", error);
                _infoError = timeout;
                throw timeout;
            }
            catch (Exception error) when (IsProviderFailure(error))
            {
                _infoError = error;
                throw;
            }
        }
        finally
        {
            _handshake.Release();
        }
    }

    private T Decode<T>(CallToolResult result, string tool)
    {
        if (result.IsError is true)
        {
            throw McpMapping.ToolError(Name, tool, result);
        }

        try
        {
            var body = result.StructuredContent
                is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } structured
                ? structured
                : JsonSerializer.Deserialize<JsonElement>(McpMapping.Text(result).Trim());
            return body.Deserialize<T>(ProviderProtocol.JsonOptions) ?? throw new JsonException("null response");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("unexpected response: " + error.Message, error);
        }
    }

    private static string Hint(string? value) => value is "low" or "high" ? value : "medium";

    private static string Safe(string value) => CredentialRedactor.Redact(value);

    private static bool IsProviderFailure(Exception error) =>
        error
            is IOException
                or InvalidDataException
                or JsonException
                or TimeoutException
                or ProviderException
                or InvalidOperationException
                or ModelContextProtocol.McpException;
}
