using Harness.Core.Architecture;
using Harness.Core.Providers;

namespace Harness.Infrastructure.Configuration;

public sealed record HarnessConfiguration
{
    public StorageConfiguration Storage { get; set; } = new();
    public LogConfiguration Log { get; set; } = new();
    public SyncConfiguration Sync { get; set; } = new();
    public ArchitectureConfiguration Architecture { get; set; } = new();
    public Dictionary<string, ProviderConfiguration> Providers { get; set; } = new(StringComparer.Ordinal);

    public string ResolveDatabasePath(string stateRoot) => Path.GetFullPath(Storage.DbPath, stateRoot);
}

public sealed record StorageConfiguration
{
    public string DbPath { get; set; } = Path.Combine(".harness", "harness.db");
    public int BusyTimeoutMs { get; set; } = 5000;
}

public sealed record LogConfiguration
{
    public string Level { get; set; } = "info";
}

public sealed record SyncConfiguration
{
    public string[] Ignore { get; set; } = [];
}

public sealed record ArchitectureConfiguration
{
    public ComplexityConfiguration Complexity { get; set; } = new();
}

/// <summary>The lowest cyclomatic complexity reported as a note, a warning and a problem.</summary>
public sealed record ComplexityConfiguration
{
    public int Note { get; set; } = 11;
    public int Warn { get; set; } = 21;
    public int Problem { get; set; } = 51;

    public ComplexityThresholds Thresholds() => new(Note, Warn, Problem);
}

public sealed record ConfigurationSources(string Global, string Project);

public sealed record LoadedConfiguration(HarnessConfiguration Value, ConfigurationSources Sources);
