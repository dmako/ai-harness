using System.Text.Json.Nodes;
using Harness.Core.Architecture;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Architecture.Metrics;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Git;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    /// <summary>
    /// How every member's cyclomatic complexity is distributed, per enabled language, with that language's
    /// thresholds: the data to choose thresholds from. Exact .NET values replace the count when measured for HEAD.
    /// </summary>
    private async Task<JsonNode?> ComplexityAsync(JsonObject project, CancellationToken token)
    {
        var id = UiProjectCatalog.Text(project, "id");
        var root = UiProjectCatalog.Text(project, "root");
        var member = new WorkspaceMember("", id, root, UiProjectCatalog.Text(project, "db_path"));
        ComplexityConfiguration complexity;
        try
        {
            complexity = ConfigurationLoader
                .LoadFiles(lifecycle.GlobalConfigurationPath, ConfigurationPaths.ProjectPath(root))
                .Value.Architecture.Complexity;
        }
        catch (InvalidDataException)
        { /* The findings keep the default thresholds too. */
            complexity = new();
        }

        var languages = complexity.Thresholds();
        var exact = complexity.Metrics.Enabled ? await ExactAsync(root, id, token) : null;
        var values = await new ArchitectureReader([member]).ComplexityValuesAsync(member, languages, exact, token);
        JsonArray output = [];
        foreach (var (language, thresholds) in languages.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var own = values.Where(value => value.Member.Language == language).ToArray();
            if (own.Length is 0)
            {
                continue;
            }

            output.Add((JsonNode)Distribution(language, thresholds, own));
        }

        return new JsonObject { ["languages"] = output, ["bin_width"] = BinWidth };
    }

    private const int BinWidth = 5;
    private const int TopFunctions = 5;

    /// <summary>
    /// One language's functions in bins of five from 1 to the most complex (every bin, empty ones too), its most
    /// complex functions and each file's most complex one, as harness ui draws them beside the change coupling.
    /// </summary>
    private static JsonObject Distribution(
        string language,
        ComplexityThresholds thresholds,
        (ComplexityMember Member, bool Measured)[] own
    )
    {
        var max = own.Max(value => value.Member.Complexity);
        var counts = new int[Math.Max(1, (max - 1) / BinWidth + 1)];
        foreach (var (member, _) in own)
        {
            counts[Math.Max(0, member.Complexity - 1) / BinWidth]++;
        }

        JsonObject files = [];
        foreach (
            var file in own.GroupBy(value => value.Member.Path).OrderBy(group => group.Key, StringComparer.Ordinal)
        )
        {
            files[file.Key] = file.Max(value => value.Member.Complexity);
        }

        return new JsonObject
        {
            ["language"] = language,
            ["thresholds"] = new JsonObject
            {
                ["note"] = thresholds.Note,
                ["warn"] = thresholds.Warn,
                ["problem"] = thresholds.Problem,
            },
            ["functions"] = own.Length,
            ["max"] = max,
            ["measured"] = own.Count(value => value.Measured),
            ["bins"] = new JsonArray(
                counts
                    .Select(
                        (count, index) =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["from"] = index * BinWidth + 1,
                                    ["to"] = (index + 1) * BinWidth,
                                    ["count"] = count,
                                }
                    )
                    .ToArray()
            ),
            ["top"] = new JsonArray(
                own.Select(value => value.Member)
                    .OrderByDescending(member => member.Complexity)
                    .Take(TopFunctions)
                    .Select(member =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["name"] = member.Name,
                                ["path"] = member.Path,
                                ["line"] = member.Line,
                                ["complexity"] = member.Complexity,
                            }
                    )
                    .ToArray()
            ),
            ["files"] = files,
        };
    }

    private async Task<IReadOnlyList<MeasuredMember>?> ExactAsync(string root, string id, CancellationToken token)
    {
        try
        {
            var head = await (await GitRepository.DiscoverAsync(root, token)).ReadHeadAsync(token);
            return MetricsStore.For(lifecycle.GlobalConfigurationPath, id).Load(head.Commit) is { State: "done" } run
                ? run.Members
                : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        { /* Without HEAD the count from source stands. */
            return null;
        }
    }
}
