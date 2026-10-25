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

/// <summary>
/// The lowest cyclomatic complexity reported as a note, a warning and a problem, for the languages listed;
/// a language's own table overrides any of them.
/// </summary>
public sealed record ComplexityConfiguration
{
    public static IReadOnlyList<string> Known { get; } = ["csharp", "go", "typescript"];

    public string[] Languages { get; set; } = ["csharp", "go"];
    public int Note { get; set; } = 11;
    public int Warn { get; set; } = 21;
    public int Problem { get; set; } = 51;
    public ComplexityLevels? Csharp { get; set; }
    public ComplexityLevels? Go { get; set; }
    public ComplexityLevels? Typescript { get; set; }
    public MetricsConfiguration Metrics { get; set; } = new();

    public IReadOnlyDictionary<string, ComplexityThresholds> Thresholds() =>
        Languages.Distinct(StringComparer.Ordinal).ToDictionary(language => language, For, StringComparer.Ordinal);

    public ComplexityThresholds For(string language)
    {
        var own = language switch
        {
            "csharp" => Csharp,
            "go" => Go,
            "typescript" => Typescript,
            _ => null,
        };
        return new(own?.Note ?? Note, own?.Warn ?? Warn, own?.Problem ?? Problem);
    }
}

/// <summary>The background Microsoft.CodeAnalysis.Metrics run that gives .NET members exact values (Windows).</summary>
public sealed record MetricsConfiguration
{
    public bool Enabled { get; set; }
    public int TimeoutMs { get; set; } = 600_000;
}

public sealed record ComplexityLevels
{
    public int? Note { get; set; }
    public int? Warn { get; set; }
    public int? Problem { get; set; }
}

public sealed record ConfigurationSources(string Global, string Project);

public sealed record LoadedConfiguration(HarnessConfiguration Value, ConfigurationSources Sources);
