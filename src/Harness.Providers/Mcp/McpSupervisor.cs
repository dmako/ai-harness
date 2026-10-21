using System.Globalization;
using Harness.Core.Providers;
using ModelContextProtocol.Protocol;

namespace Harness.Providers.Mcp;

public sealed record McpRestartPolicy
{
    public int MaxFailures { get; init; } = 3;
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>Single-flight lazy startup with fail-fast backoff and bounded failure history.</summary>
public sealed class McpSupervisor(IProviderProcessFactory factory, McpProcessOptions options, McpRestartPolicy? policy = null, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _gate = new();
    private readonly McpRestartPolicy _policy = Defaults(policy ?? new());
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Queue<DateTimeOffset> _failures = [];
    private readonly CancellationTokenSource _lifetime = new();
    private McpProcessClient? _current;
    private DateTimeOffset _nextAttempt;
    private Exception? _lastError;
    private int _consecutive;
    private int _starts;
    private bool _closed;
    private Task? _closeTask;
    private readonly List<McpProcessClient> _retired = [];

    public string ServerVersion { get { lock (_gate) return _current?.ServerVersion ?? ""; } }

    public async Task<McpProcessClient> ClientAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_current is { Alive: true } current)
                return current;
            ObjectDisposedException.ThrowIf(_closed, this);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _startGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            McpProcessClient? old;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_current is { Alive: true } current)
                    return current;
                old = _current;
                if (old is not null)
                {
                    _current = null;
                    Fail(new IOException(options.Name + ": provider process exited"));
                }
            }
            if (old is not null)
                await old.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            { CheckGate(); }
            McpProcessClient started;
            try
            {
                started = await McpProcessClient.StartAsync(factory, options, linked.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    if (!_closed && !cancellationToken.IsCancellationRequested)
                        Fail(error);
                }
                throw;
            }
            lock (_gate)
            {
                _current = started;
                _consecutive = 0;
                _starts++;
            }
            _ = WatchAsync(started);
            return started;
        }
        finally { _startGate.Release(); }
    }

    public async Task<CallToolResult> CallToolAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) =>
        await (await ClientAsync(cancellationToken).ConfigureAwait(false)).CallToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false);

    public ProviderHealth Health()
    {
        lock (_gate)
        {
            Prune();
            return (_closed, _current?.Alive, _failures.Count >= _policy.MaxFailures, _lastError) switch
            {
                (true, _, _, _) => new("unavailable", "closed"),
                (_, true, _, _) => new("ready", string.Create(CultureInfo.InvariantCulture, $"running (pid {_current.Id})") + (_starts > 1 ? string.Create(CultureInfo.InvariantCulture, $", restarted {_starts - 1} time(s)") : "")),
                (_, _, true, _) => new("unavailable", string.Create(CultureInfo.InvariantCulture, $"failed {_failures.Count} times in {_policy.Window}: {_lastError?.Message}"), "check that the configured command runs on its own; retries resume after " + _failures.Peek().Add(_policy.Window).ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
                (_, _, _, { } error) => new("degraded", "not running after a failure (will restart on next use): " + error.Message),
                _ => new("idle", "starts on first use"),
            };
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
            return new(_closeTask ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            McpProcessClient[] clients;
            lock (_gate)
            { clients = _current is { } current ? [.. _retired, current] : [.. _retired]; _current = null; }
            await Task.WhenAll(clients.Select(client => client.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
        finally { _startGate.Release(); _lifetime.Dispose(); _startGate.Dispose(); }
    }

    private async Task WatchAsync(McpProcessClient client)
    {
        await client.Completion.ConfigureAwait(false);
        lock (_gate)
        {
            if (_current != client || _closed)
                return;
            _current = null;
            _retired.Add(client);
            Fail(new IOException(options.Name + ": provider process exited; stderr: " + client.StderrTail(3)));
        }
        try
        { await client.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or TimeoutException or ObjectDisposedException)
        {
            lock (_gate)
            { _lastError = error; }
        }
        finally { lock (_gate) { _retired.Remove(client); } }
    }

    private void Prune()
    {
        while (_failures.TryPeek(out var at) && _clock.GetUtcNow() - at >= _policy.Window)
            _failures.Dequeue();
    }

    private void CheckGate()
    {
        Prune();
        if (_failures.Count >= _policy.MaxFailures)
            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"{options.Name} failed {_failures.Count} times in {_policy.Window}; not retrying until {_failures.Peek().Add(_policy.Window):HH:mm:ss}. Last error: {_lastError?.Message}"));
        if (_clock.GetUtcNow() < _nextAttempt)
            throw new IOException(options.Name + " is restarting after a failure. Last error: " + _lastError?.Message);
    }

    private void Fail(Exception error)
    {
        Prune();
        _failures.Enqueue(_clock.GetUtcNow());
        _consecutive++;
        _lastError = error;
        var ticks = Math.Min(_policy.MaxBackoff.Ticks, _policy.BaseBackoff.Ticks * Math.Pow(2, Math.Min(_consecutive - 1, 30)));
        _nextAttempt = _clock.GetUtcNow().AddTicks((long)ticks);
    }

    private static McpRestartPolicy Defaults(McpRestartPolicy policy) => policy with
    {
        MaxFailures = policy.MaxFailures > 0 ? policy.MaxFailures : 3,
        Window = policy.Window > TimeSpan.Zero ? policy.Window : TimeSpan.FromMinutes(5),
        BaseBackoff = policy.BaseBackoff > TimeSpan.Zero ? policy.BaseBackoff : TimeSpan.FromMilliseconds(500),
        MaxBackoff = policy.MaxBackoff > TimeSpan.Zero ? policy.MaxBackoff : TimeSpan.FromSeconds(8),
    };
}
