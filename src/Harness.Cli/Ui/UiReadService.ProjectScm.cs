using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Harness.Cli.Scm;
using Harness.Core.Serialization;
using Harness.Core.Web;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Scm;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Telemetry;
using Harness.Scm;

namespace Harness.Cli.Ui;

/// <summary>
/// A project's SCM card (Agents › Tools &amp; providers › Used here): which SCM connection the project uses and how it
/// was chosen, the connections that serve its remote equally (one may be chosen for this machine), a test of the
/// connection and of the project's reachability, and how agents called the scm_ tools. Tokens never reach the page.
/// </summary>
internal sealed partial class UiReadService
{
    private const string ScmToolPrefix = "scm_";

    /// <summary>The last test of each project's SCM, by project id, while harness ui runs.</summary>
    private static readonly ConcurrentDictionary<string, JsonObjectMap> ProjectScmTests = new(StringComparer.Ordinal);

    /// <summary>GET /api/projects/{id}/scm?days=N.</summary>
    private async Task<JsonNode?> ProjectScmAsync(
        JsonObject project,
        HarnessDatabase database,
        UiReadRequest request,
        CancellationToken token
    )
    {
        var days = Days(request);
        var card = await ProjectScmCardAsync(project, token).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-days);
        var telemetry = new TelemetryStore(database);
        card["stats"] = new JsonObjectMap
        {
            ["days"] = days,
            ["tools"] = telemetry
                .Metrics(since)
                .Tools.Where(tool => tool.Tool.StartsWith(ScmToolPrefix, StringComparison.Ordinal))
                .GroupBy(tool => tool.Tool, StringComparer.Ordinal)
                .Select(group =>
                {
                    var calls = group.Sum(tool => tool.Calls);
                    var errors = group.Sum(tool => tool.Errors);
                    return new JsonObjectMap
                    {
                        ["tool"] = group.Key,
                        ["calls"] = calls,
                        ["errors"] = errors,
                        ["error_rate"] = TelemetryStore.Rate(errors, calls),
                        ["last"] = group.Max(tool => tool.Last),
                    };
                })
                .OrderByDescending(tool => (int)tool["calls"]!)
                .ToList(),
            ["trend"] = new JsonObjectMap { ["days"] = telemetry.ToolDays(ScmToolPrefix, since, now) },
        };
        return UiJson.Node(card);
    }

    /// <summary>The card without its statistics: resolution, how it was chosen, the candidates and the last test.</summary>
    private async Task<JsonObjectMap> ProjectScmCardAsync(JsonObject project, CancellationToken token)
    {
        var id = UiProjectCatalog.Text(project, "id");
        var inspection = await lifecycle
            .InspectAsync(UiProjectCatalog.Text(project, "root"), token)
            .ConfigureAwait(false);
        var store = ScmStore;
        IReadOnlyList<ScmEntry> entries;
        string? choice;
        try
        {
            entries = store.List();
            choice = inspection.Location.Repository is { } repository
                ? store.ChoiceFor(ScmProjectResolver.RepositoryKey(repository))
                : null;
        }
        catch (ScmConfigurationException exception)
        {
            throw new UiRequestException(500, exception.Message);
        }

        var resolution = inspection.Scm;
        var byName = entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        var candidates = (resolution?.Candidates ?? []).Select(endpoint => endpoint.Name).ToList();
        if (choice is not null && !candidates.Contains(choice, StringComparer.OrdinalIgnoreCase))
        {
            // a stale choice stays visible, so it can be changed back to automatic
            candidates.Add(choice);
        }

        var configName = inspection.Location.Configuration.Value.Scm.Name.Trim();
        return new JsonObjectMap
        {
            ["configured"] = entries.Count > 0,
            ["repository"] = inspection.Location.Repository is not null,
            ["scm"] = resolution?.Describe(),
            ["chosen"] = resolution?.Chosen(),
            ["choice"] = choice,
            ["config_name"] = configName.Length > 0 ? configName : null,
            ["candidates"] = candidates
                .Select(name =>
                    byName.TryGetValue(name, out var entry)
                        ? new JsonObjectMap
                        {
                            ["name"] = entry.Name,
                            ["type"] = ScmKinds.Name(entry.Endpoint.Kind),
                            ["url"] = entry.Endpoint.BaseUrl.AbsoluteUri.TrimEnd('/'),
                            ["token"] = ScmCommandRunner.TokenText(entry),
                        }
                        : new JsonObjectMap { ["name"] = name, ["missing"] = true }
                )
                .ToList(),
            ["last_test"] = ProjectScmTests.GetValueOrDefault(id),
        };
    }

    /// <summary>POST /api/projects/{id}/scm/choice and /scm/test.</summary>
    private async Task<JsonNode?> WriteProjectScmAsync(
        JsonObject project,
        UiWriteRequest request,
        CancellationToken token
    )
    {
        try
        {
            var endpoint = request.Route.Endpoint["/api/projects/{id}/".Length..];
            return endpoint switch
            {
                "scm/choice" => await ChooseProjectScmAsync(project, request.Body, token).ConfigureAwait(false),
                "scm/test" => await TestProjectScmAsync(project, token).ConfigureAwait(false),
                _ => throw new UiRequestException(404, "unknown endpoint"),
            };
        }
        catch (ScmConfigurationException exception)
        {
            throw new UiRequestException(400, exception.Message);
        }
    }

    /// <summary>Chooses the project's SCM on this machine (an empty name goes back to automatic); answers the new card.</summary>
    private async Task<JsonNode?> ChooseProjectScmAsync(JsonObject project, JsonObject body, CancellationToken token)
    {
        var name = new UiWriteBody(body, "name").Text("name").Trim();
        var inspection = await lifecycle
            .InspectAsync(UiProjectCatalog.Text(project, "root"), token)
            .ConfigureAwait(false);
        await ScmProjectChoice
            .SetAsync(ScmStore, inspection, name.Length > 0 ? name : null, token)
            .ConfigureAwait(false);
        ProjectScmTests.TryRemove(UiProjectCatalog.Text(project, "id"), out _);
        return UiJson.Node(await ProjectScmCardAsync(project, token).ConfigureAwait(false));
    }

    /// <summary>
    /// Tests the project's SCM connection and whether its project is reachable with that token, as doctor's
    /// 'scm project' check does.
    /// </summary>
    private async Task<JsonNode?> TestProjectScmAsync(JsonObject project, CancellationToken token)
    {
        var inspection = await lifecycle
            .InspectAsync(UiProjectCatalog.Text(project, "root"), token)
            .ConfigureAwait(false);
        var resolution = inspection.Scm;
        if (resolution?.Match is not { } match)
        {
            throw new UiRequestException(
                400,
                "This project has no SCM to test: " + (resolution?.Reason ?? "no SCM is configured.")
            );
        }

        var store = ScmStore;
        var entry =
            store.Find(match.Endpoint.Name)
            ?? throw new UiRequestException(400, $"No SCM is named '{match.Endpoint.Name}'.");
        var outcomes = await ScmDoctor.TestAllAsync(store, [entry], null, token).ConfigureAwait(false);
        var check = await ScmDoctor.ProjectCheckAsync(resolution, outcomes, null, token).ConfigureAwait(false);
        var test = ScmProviders.Wire(outcomes[0].Test, DateTimeOffset.UtcNow);
        var result = new JsonObjectMap
        {
            ["name"] = entry.Name,
            ["test"] = test,
            ["project"] = new JsonObjectMap
            {
                ["level"] = check.Level,
                ["detail"] = check.Detail,
                ["fix"] = check.Fix,
            },
        };
        ProjectScmTests[UiProjectCatalog.Text(project, "id")] = result;
        ScmTests[entry.Name] = test;
        return UiJson.Node(new JsonObjectMap { ["test"] = result });
    }
}
