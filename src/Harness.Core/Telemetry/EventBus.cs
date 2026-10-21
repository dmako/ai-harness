using System.Threading.Channels;

namespace Harness.Core.Telemetry;

/// <summary>Independent bounded consumers; a full queue drops the incoming event.</summary>
public sealed class EventBus<T> : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private Task? _closed;
    public void Subscribe(string name, Func<T, Task> consume, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(consume);
        lock (_gate)
        {
            if (_closed is not null)
                return;
            _subscriptions.Add(new(name, consume, capacity));
        }
    }
    public void Publish(T value)
    {
        lock (_gate)
        {
            if (_closed is not null)
                return;
            for (var i = 0; i < _subscriptions.Count; i++)
            {
                var sub = _subscriptions[i];
                if (!sub.Queue.Writer.TryWrite(value))
                    Interlocked.Increment(ref sub.Dropped);
            }
        }
    }
    public IReadOnlyDictionary<string, long> Dropped()
    {
        lock (_gate)
            return _subscriptions.ToDictionary(s => s.Name, s => Interlocked.Read(ref s.Dropped), StringComparer.Ordinal);
    }
    public long Failures { get { lock (_gate) return _subscriptions.Sum(s => Interlocked.Read(ref s.Failures)); } }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closed is null)
            {
                foreach (var sub in _subscriptions)
                    sub.Queue.Writer.TryComplete();
                _closed = Task.WhenAll(_subscriptions.Select(s => s.Worker));
            }
            return new(_closed);
        }
    }
    private sealed class Subscription
    {
        public string Name { get; }
        public Channel<T> Queue { get; }
        public Task Worker { get; }
        public long Dropped;
        public long Failures;
        public Subscription(string name, Func<T, Task> consume, int capacity)
        {
            Name = name;
            Queue = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
            Worker = Task.Run(async () =>
            {
                await foreach (var value in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    await ConsumeAsync(consume, value).ConfigureAwait(false);
            });
        }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failing optional subscriber must neither terminate its queue nor fail the publisher.")]
        private async Task ConsumeAsync(Func<T, Task> consume, T value)
        {
            try
            { await consume(value).ConfigureAwait(false); }
            catch (Exception) { Interlocked.Increment(ref Failures); }
        }
    }
}
