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

    /// <summary>Model prices that add to or override harness's shipped price table ([pricing.&lt;model&gt;]).</summary>
    public Dictionary<string, PriceConfiguration> Pricing { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The project's SCM chosen by hand ([scm]), when its remotes do not map to one.</summary>
    public ScmProjectConfiguration Scm { get; set; } = new();

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

/// <summary>
/// A model's API price in US dollars per million tokens. Left out, a kind keeps the shipped entry's price; for a
/// model harness does not ship, cached input and cache writes cost what input costs.
/// </summary>
public sealed record PriceConfiguration
{
    public double? Input { get; set; }
    public double? CachedInput { get; set; }
    public double? CacheWrite { get; set; }
    public double? Output { get; set; }
}

/// <summary>
/// [scm]: an SCM configured on this machine by name, the project's path on it (group/sub/repo, owner/repo) and the
/// remote to match; each is optional and empty fields leave the choice to the remotes.
/// </summary>
public sealed record ScmProjectConfiguration
{
    public string Name { get; set; } = "";
    public string Project { get; set; } = "";
    public string Remote { get; set; } = "";

    public Harness.Scm.ScmProjectOverride ToOverride() => new(Name.Trim(), Project.Trim(), Remote.Trim());
}

public sealed record ConfigurationSources(string Global, string Project);

public sealed record LoadedConfiguration(HarnessConfiguration Value, ConfigurationSources Sources);
