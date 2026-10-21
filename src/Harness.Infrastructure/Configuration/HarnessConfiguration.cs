using Harness.Core.Providers;

namespace Harness.Infrastructure.Configuration;

public sealed record HarnessConfiguration
{
    public StorageConfiguration Storage { get; init; } = new();
    public LogConfiguration Log { get; init; } = new();
    public SyncConfiguration Sync { get; init; } = new();
    public Dictionary<string, ProviderConfiguration> Providers { get; init; } = new(StringComparer.Ordinal);

    public string ResolveDatabasePath(string stateRoot) => Path.GetFullPath(Storage.DbPath, stateRoot);
}

public sealed record StorageConfiguration
{
    public string DbPath { get; init; } = Path.Combine(".harness", "harness.db");
    public int BusyTimeoutMs { get; init; } = 5000;
}

public sealed record LogConfiguration
{
    public string Level { get; init; } = "info";
}

public sealed record SyncConfiguration
{
    public string[] Ignore { get; init; } = [];
}

public sealed record ConfigurationSources(string Global, string Project);

public sealed record LoadedConfiguration(HarnessConfiguration Value, ConfigurationSources Sources);
