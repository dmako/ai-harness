using Harness.Core.Providers;
using Harness.Core.Telemetry;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Telemetry;

/// <summary>Local, bounded recording only. No network exporter or project initialization.</summary>
public sealed class TelemetryRecorder : IToolObserver, IContextObserver, IAsyncDisposable
{
    private readonly EventBus<ToolObservation> _tools = new();
    private readonly EventBus<ProviderObservation> _providers = new();
    private readonly EventBus<ContextObservation> _contexts = new();
    private readonly EventBus<ClientToolEvent> _events = new();
    private readonly HashSet<string> _versions = new(StringComparer.Ordinal);
    public TelemetryRecorder(string path, int busyTimeoutMs = 5000, Func<string, string>? version = null)
    {
        var options = new StorageOptions { BusyTimeout = TimeSpan.FromMilliseconds(Math.Clamp(busyTimeoutMs, 0, 2000)) };
        Task Write(Action<TelemetryStore, CancellationToken> write)
        {
            if (!File.Exists(path))
            {
                return Task.CompletedTask;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var db = HarnessDatabase.OpenExisting(path, options);
            write(new(db), timeout.Token);
            return Task.CompletedTask;
        }
        _tools.Subscribe("telemetry", e => Write((store, token) => store.RecordTool(e, token)));
        _providers.Subscribe("telemetry", e => Write((store, token) => store.RecordProvider(e, token)));
        _contexts.Subscribe("telemetry", e => Write((store, token) => store.RecordContext(e, token)));
        _events.Subscribe("telemetry", e => Write((store, token) => store.RecordEvent(e, token)));
        _providers.Subscribe("versions", e =>
        {
            if (!File.Exists(path) || _versions.Contains(e.Provider))
            {
                return Task.CompletedTask;
            }

            var value = version?.Invoke(e.Provider) ?? "";
            if (value.Length is 0)
            {
                return Task.CompletedTask;
            }

            using var db = HarnessDatabase.OpenExisting(path, options);
            db.PutDiagnostic("provider_version:" + e.Provider, Harness.Core.Memory.CredentialRedactor.Redact(value));
            _versions.Add(e.Provider);
            return Task.CompletedTask;
        });
    }
    public void Observe(ToolObservation observation) => _tools.Publish(observation);
    public void Observe(ContextObservation observation) => _contexts.Publish(observation);
    public void Observe(ClientToolEvent observation) => _events.Publish(observation);
    public void Observe(ProviderResult result, ProviderRequest request) => _providers.Publish(new(DateTimeOffset.UtcNow, result.Provider, result.Capability, request.Query, result.Items.Count, result.Bytes, (long)result.Latency.TotalMilliseconds, result.TimedOut, result.Error?.Message ?? ""));
    public long Dropped => _tools.Dropped().Values.Sum() + _providers.Dropped().Values.Sum() + _contexts.Dropped().Values.Sum() + _events.Dropped().Values.Sum();
    public long Failures => _tools.Failures + _providers.Failures + _contexts.Failures + _events.Failures;
    public async ValueTask DisposeAsync()
    {
        await _tools.DisposeAsync().ConfigureAwait(false);
        await _providers.DisposeAsync().ConfigureAwait(false);
        await _contexts.DisposeAsync().ConfigureAwait(false);
        await _events.DisposeAsync().ConfigureAwait(false);
    }
}
