using System.Diagnostics.CodeAnalysis;
using System.Text;
using Harness.Core.Memory;
using Harness.Core.Providers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Harness.Providers.Mcp;

public sealed record McpProcessOptions(string Name, ProviderProcessOptions Process)
{
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int StderrLimit { get; init; } = 64 * 1024;
    public int MaximumFrameBytes { get; init; } = 16 * 1024 * 1024;
    public string ClientVersion { get; init; } = "0.1.0";
}

/// <summary>Official SDK session over owned, bounded subprocess pipes.</summary>
public sealed class McpProcessClient : IAsyncDisposable
{
    private readonly McpProcessOptions _options;
    private readonly IProviderProcess _process;
    private readonly ByteTail _stderr;
    private readonly Task _stderrReader;
    private readonly Task _processExit;
    private McpClient? _client;
    private readonly object _closeGate = new();
    private Task? _closeTask;
    private int _closed;

    private McpProcessClient(McpProcessOptions options, IProviderProcess process)
    {
        _options = options;
        _process = process;
        _stderr = new(options.StderrLimit);
        _stderrReader = ReadStderrAsync();
        _processExit = ReapDescendantsAsync();
    }

    public int Id => _process.Id;
    public bool Alive => _closed is 0 && !_process.Completion.IsCompleted && _client?.Completion.IsCompleted is false;
    public Task Completion =>
        _client is null ? _process.Completion : Task.WhenAny(_process.Completion, _client.Completion);
    public string ServerVersion =>
        Alive && _client!.ServerInfo.Version.Length > 0
            ? _client.ServerInfo.Name + " " + _client.ServerInfo.Version
            : "";

    [SuppressMessage(
        "Reliability",
        "CA2025:Do not pass disposable objects into unawaited tasks",
        Justification = "A timed-out native launch cannot be interrupted; its separate reaper owns the late process and never captures the deadline or linked cancellation sources."
    )]
    public static async Task<McpProcessClient> StartAsync(
        IProviderProcessFactory factory,
        McpProcessOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        if (
            options.StartTimeout <= TimeSpan.Zero
            || options.CallTimeout <= TimeSpan.Zero
            || options.StderrLimit is <= 0 or > 4 * 1024 * 1024
            || options.MaximumFrameBytes is <= 0 or > 64 * 1024 * 1024
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Timeouts must be positive; stderr and frames are limited to 4 MiB and 64 MiB respectively."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(options.StartTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var launching = Task.Run(() => factory.Start(options.Process), CancellationToken.None);
        IProviderProcess process;
        try
        {
            process = await launching.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _ = ReapLateStartAsync(launching);
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
            {
                throw new TimeoutException(options.Name + ": start: timed out", error);
            }

            throw new IOException(CredentialRedactor.Redact(options.Name + ": start: " + error.Message), error);
        }
        var client = new McpProcessClient(options, process);
        try
        {
            var output = new BoundedFrameStream(process.Output, options.MaximumFrameBytes, process.KillTree);
            client._client = await McpClient
                .CreateAsync(
                    new StreamClientTransport(process.Input, output),
                    new McpClientOptions
                    {
                        ClientInfo = new() { Name = "harness", Version = options.ClientVersion },
                        ProtocolVersion = "2025-11-25",
                        InitializationTimeout = options.StartTimeout,
                    },
                    cancellationToken: linked.Token
                )
                .ConfigureAwait(false);
            return client;
        }
        catch (Exception error)
        {
            process.KillTree();
            await client.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
            {
                throw new TimeoutException(client.Message("start: timed out"), error);
            }

            throw new IOException(client.Message("start: " + error.Message), error);
        }
    }

    public Task<CallToolResult> CallToolAsync(
        string name,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default
    ) =>
        CallAsync(
            name,
            token => _client!.CallToolAsync(name, arguments, cancellationToken: token).AsTask(),
            cancellationToken
        );

    public Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<Tool>>(
            "tools/list",
            async token =>
            {
                List<Tool> tools = [];
                HashSet<string> cursors = new(StringComparer.Ordinal);
                string? cursor = null;
                do
                {
                    var page = await _client!
                        .ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, token)
                        .ConfigureAwait(false);
                    tools.AddRange(page.Tools);
                    cursor = page.NextCursor;
                    if (
                        tools.Count > 10000
                        || !string.IsNullOrEmpty(cursor) && (!cursors.Add(cursor) || cursors.Count > 256)
                    )
                    {
                        throw new InvalidDataException("tools/list exceeded pagination limits");
                    }
                } while (!string.IsNullOrEmpty(cursor));
                return tools;
            },
            cancellationToken
        );

    public string StderrTail(int lines = 5) =>
        CredentialRedactor.Redact(
            string.Join('\n', _stderr.Text.TrimEnd('\n').Split('\n').TakeLast(Math.Max(0, lines))).Trim()
        );

    public ValueTask DisposeAsync()
    {
        lock (_closeGate)
        {
            return new(_closeTask ??= CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) is not 0)
        {
            return;
        }

        try
        {
            await _process.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (_client is not null)
                {
                    await _client.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await Task.WhenAll(_stderrReader, _processExit).ConfigureAwait(false);
            }
        }
    }

    private async Task<T> CallAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> call,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Alive)
        {
            throw new IOException(Message("provider process exited"));
        }
        // Always apply the configured deadline, even when the registry has a longer parent deadline.
        using var deadline = new CancellationTokenSource(_options.CallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await call(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
            {
                _process.KillTree();
                throw new TimeoutException(Message(operation + ": timed out"), error);
            }
            throw new IOException(
                Message(operation + ": " + (!Alive ? "provider process exited" : error.Message)),
                error
            );
        }
    }

    private string Message(string message)
    {
        var tail = StderrTail();
        return CredentialRedactor.Redact(
            _options.Name + ": " + message + (tail.Length is 0 ? "" : "\nstderr (last lines):\n" + tail)
        );
    }

    private async Task ReadStderrAsync()
    {
        var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await _process.Error.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                _stderr.Append(buffer.AsSpan(0, count));
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { /* Process cleanup closes the pipe. */
        }
    }

    private async Task ReapDescendantsAsync()
    {
        await _process.Completion.ConfigureAwait(false);
        _process.KillTree();
    }

    private static async Task ReapLateStartAsync(Task<IProviderProcess> launching)
    {
        await Task.WhenAny(launching).ConfigureAwait(false);
        if (!launching.IsCompletedSuccessfully)
        {
            _ = launching.Exception;
            return;
        }
        try
        {
            var process = await launching.ConfigureAwait(false);
            process.KillTree();
            await process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
            when (error
                    is IOException
                        or System.ComponentModel.Win32Exception
                        or TimeoutException
                        or ObjectDisposedException
            )
        { /* Startup failure was already reported; observe a late launcher failure as well. */
        }
    }

    private sealed class ByteTail(int limit)
    {
        private readonly byte[] _bytes = new byte[limit];
        private readonly object _gate = new();
        private int _count;
        internal string Text
        {
            get
            {
                lock (_gate)
                {
                    return Encoding.UTF8.GetString(_bytes, 0, _count);
                }
            }
        }

        internal void Append(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                if (bytes.Length >= _bytes.Length)
                {
                    bytes[^_bytes.Length..].CopyTo(_bytes);
                    _count = _bytes.Length;
                    return;
                }
                var overflow = Math.Max(0, _count + bytes.Length - _bytes.Length);
                _bytes.AsSpan(overflow, _count - overflow).CopyTo(_bytes);
                bytes.CopyTo(_bytes.AsSpan(_count - overflow));
                _count = _count - overflow + bytes.Length;
            }
        }
    }
}
