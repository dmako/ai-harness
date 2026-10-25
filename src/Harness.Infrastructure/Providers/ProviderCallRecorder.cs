using Harness.Core.Providers;
using Harness.Infrastructure.Telemetry;

namespace Harness.Infrastructure.Providers;

/// <summary>Compatibility adapter for callers that need only provider observations.</summary>
public sealed class ProviderCallRecorder(string databasePath, int busyTimeoutMs = 5000) : IAsyncDisposable
{
    private readonly TelemetryRecorder _recorder = new(databasePath, busyTimeoutMs);

    public void Observe(ProviderResult result, ProviderRequest request) => _recorder.Observe(result, request);

    public ValueTask DisposeAsync() => _recorder.DisposeAsync();
}
