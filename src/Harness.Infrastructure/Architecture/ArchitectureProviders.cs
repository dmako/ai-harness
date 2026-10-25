using System.Text.Json.Serialization;
using Harness.Core.Architecture;
using Harness.Core.Providers;
using Harness.Infrastructure.Configuration;

namespace Harness.Infrastructure.Architecture;

public sealed record ArchitectureCoverage(
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("providers")] IReadOnlyList<string> Providers,
    [property: JsonPropertyName("without"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Without
);

public sealed record ProviderArchitectureRead(
    IReadOnlyList<ArchitectureFinding> Findings,
    bool Answered,
    IReadOnlyList<string> Notes
);

public static class ArchitectureProviders
{
    public static IReadOnlyList<ArchitectureCoverage> Coverage(IProviderRegistry? registry) =>
        CoverageOf(registry?.Capabilities() ?? []);

    public static IReadOnlyList<ArchitectureCoverage> Coverage(HarnessConfiguration configuration) =>
        CoverageOf(
            configuration
                .Providers.Where(pair => pair.Value.IsEnabled())
                .SelectMany(pair =>
                    pair.Value.Capabilities.Keys.Where(capability =>
                            pair.Value.Only.Length is 0 || pair.Value.Only.Contains(capability, StringComparer.Ordinal)
                        )
                        .Select(capability => new CapabilityInfo(capability, pair.Key))
                )
                .Concat(
                    configuration.Architecture.Complexity.Metrics.Enabled
                        ? [new CapabilityInfo(Capabilities.ArchComplexity, MetricsWorker)]
                        : []
                )
                .ToArray()
        );

    /// <summary>The name architecture coverage gives the background Microsoft.CodeAnalysis.Metrics run.</summary>
    public const string MetricsWorker = "metrics-worker";

    private static ArchitectureCoverage[] CoverageOf(IReadOnlyList<CapabilityInfo> capabilities) =>
        Capabilities
            .Architecture.Select(capability =>
            {
                var providers = capabilities
                    .Where(item => item.Capability == capability)
                    .Select(item => item.Provider)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                return new ArchitectureCoverage(
                    capability,
                    providers,
                    providers.Length is 0 ? Capabilities.Without(capability) : null
                );
            })
            .ToArray();

    public static async Task<ProviderArchitectureRead> CyclesAsync(IProviderRegistry? registry, CancellationToken token)
    {
        if (registry is null)
        {
            return new([], false, ["no provider offers arch.cycles; file reference cycles remain available"]);
        }

        var results = await registry
            .CallAsync(new(Capabilities.ArchCycles, Limit: 100), cancellationToken: token)
            .ConfigureAwait(false);
        List<ArchitectureFinding> findings = [];
        List<string> notes = [];
        var answered = false;
        foreach (var result in results)
        {
            if (result.Error is not null)
            {
                notes.Add(result.Provider + ": " + result.Error.Message.Split('\n')[0]);
                continue;
            }
            answered = true;
            foreach (var item in result.Items.Where(item => item.Members?.Count >= 2))
            {
                findings.Add(
                    ArchitectureAnalyzer.Complete(
                        new()
                        {
                            Kind = "cycle",
                            Level = "problem",
                            Key = ArchitectureAnalyzer.Key("cycle", [.. item.Members!.Order(StringComparer.Ordinal)]),
                            Measure = item.Members!.Count,
                            Title =
                                $"{item.Members.Count} units depend on each other in a circle: "
                                + string.Join(", ", item.Members),
                            Why =
                                "Each reaches the others through its dependencies, so none of them can be changed, tested or understood alone.",
                            Fix =
                                "Move what they share into a lower unit, or turn one dependency around (the lower one owns an interface the higher one implements).",
                            Evidence = item.Path is null
                                ? null
                                : new[]
                                {
                                    item.Path
                                        + ":"
                                        + item.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                },
                            Basis = "fact",
                            Confidence = item.Confidence,
                            Source = new(item.Provider, item.Tool.Length is 0 ? null : item.Tool),
                        }
                    )
                );
            }
        }
        if (results.Count is 0)
        {
            notes.Add("no provider offers arch.cycles; file reference cycles remain available");
        }

        return new(findings, answered, notes);
    }
}
