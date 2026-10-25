using System.Text.Json.Nodes;
using System.Threading.Channels;
using Harness.Core.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Harness.Web;

internal sealed class UiLiveHub(Func<IUiChangeSource> source) : IAsyncDisposable
{
    private sealed class Subscriber
    {
        internal readonly Channel<bool> _wake = Channel.CreateBounded<bool>(1);
        private readonly object _gate = new();
        private JsonObject _pending = [];

        internal void Add(JsonObject change)
        {
            lock (_gate)
            {
                foreach (
                    var key in new[] { "registry", "workspaces" }.Where(key => change[key]?.GetValue<bool>() is true)
                )
                {
                    _pending[key] = true;
                }
                foreach (var project in change["projects"]?.AsArray().Select(p => p!.GetValue<string>()) ?? [])
                {
                    _pending["kinds"] ??= new JsonObject();
                    var previous = _pending["kinds"]![project]?.AsArray().Select(k => k!.GetValue<string>()) ?? [];
                    var added = change["kinds"]?[project]?.AsArray().Select(k => k!.GetValue<string>()) ?? [];
                    _pending["kinds"]![project] = new JsonArray(
                        previous
                            .Concat(added)
                            .Distinct(StringComparer.Ordinal)
                            .Select(k => (JsonNode?)JsonValue.Create(k))
                            .ToArray()
                    );
                    _pending["projects"] = new JsonArray(
                        _pending["kinds"]!
                            .AsObject()
                            .Select(p => p.Key)
                            .Order(StringComparer.Ordinal)
                            .Select(p => (JsonNode?)JsonValue.Create(p))
                            .ToArray()
                    );
                }
            }
            _wake.Writer.TryWrite(true);
        }

        internal JsonObject Take()
        {
            lock (_gate)
            {
                var pending = _pending;
                _pending = [];
                return pending;
            }
        }
    }

    private readonly object _gate = new();
    private readonly HashSet<Subscriber> _subscribers = [];
    private readonly List<Task> _watchers = [];
    private CancellationTokenSource? _watcherCancellation;
    private bool _disposed;

    public async Task ServeAsync(HttpContext context)
    {
        Subscriber subscriber;
        lock (_gate)
        {
            if (_disposed || _subscribers.Count >= 16)
            {
                throw new UiRequestException(503, "too many open event streams");
            }
            subscriber = new();
            _subscribers.Add(subscriber);
            if (_subscribers.Count is 1)
            {
                _watchers.RemoveAll(t => t.IsCompletedSuccessfully);
                _watcherCancellation = new();
                var token = _watcherCancellation.Token;
                _watchers.Add(Task.Run(() => WatchAsync(token), CancellationToken.None));
            }
        }
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        try
        {
            await SendAsync(context, ": harness live updates\nretry: 3000\n\n");
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var ready = await WaitAsync(subscriber, context.RequestAborted);
                if (!ready)
                {
                    await SendAsync(context, ": heartbeat\n\n");
                    continue;
                }
                var change = subscriber.Take();
                if (change.Count > 0)
                {
                    await SendAsync(context, "event: change\ndata: " + change.ToJsonString() + "\n\n");
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        { /* Disconnected or stalled reader. */
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriber);
                if (_subscribers.Count is 0)
                {
                    _watcherCancellation?.Cancel();
                    _watcherCancellation?.Dispose();
                    _watcherCancellation = null;
                }
            }
        }
    }

    private static async Task<bool> WaitAsync(Subscriber subscriber, CancellationToken token)
    {
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
        heartbeat.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await subscriber._wake.Reader.ReadAsync(heartbeat.Token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task SendAsync(HttpContext context, string frame)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await context.Response.WriteAsync(frame, deadline.Token);
        await context.Response.Body.FlushAsync(deadline.Token);
    }

    private async Task WatchAsync(CancellationToken token)
    {
        await using var watcher = source();
        try
        {
            await watcher.PollAsync(token);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1500));
            while (await timer.WaitForNextTickAsync(token))
            {
                var change = await watcher.PollAsync(token);
                if (change.Count is 0)
                {
                    continue;
                }
                lock (_gate)
                {
                    foreach (var subscriber in _subscribers)
                    {
                        subscriber.Add(change);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { /* Last page disconnected or host stopped. */
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] watchers;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _disposed = true;
            cancellation = _watcherCancellation;
            _watcherCancellation = null;
            cancellation?.Cancel();
            watchers = _watchers.ToArray();
        }
        await Task.WhenAll(watchers);
        cancellation?.Dispose();
    }
}
