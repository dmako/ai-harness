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

            JsonArray counts = [];
            foreach (var group in own.GroupBy(value => value.Member.Complexity).OrderBy(group => group.Key))
            {
                counts.Add((JsonNode)new JsonArray(group.Key, group.Count()));
            }

            output.Add(
                (JsonNode)
                    new JsonObject
                    {
                        ["language"] = language,
                        ["thresholds"] = new JsonObject
                        {
                            ["note"] = thresholds.Note,
                            ["warn"] = thresholds.Warn,
                            ["problem"] = thresholds.Problem,
                        },
                        ["members"] = own.Length,
                        ["measured"] = own.Count(value => value.Measured),
                        ["counts"] = counts,
                    }
            );
        }

        return new JsonObject { ["languages"] = output };
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
