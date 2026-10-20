namespace Harness.Infrastructure.Storage;

public sealed record StorageOptions
{
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan EffectiveBusyTimeout => BusyTimeout > TimeSpan.Zero ? BusyTimeout : TimeSpan.FromSeconds(5);
}
