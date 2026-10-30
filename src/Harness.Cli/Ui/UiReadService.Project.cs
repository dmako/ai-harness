using System.Text.Json.Nodes;
using Harness.Core.Memory;
using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Harness.Core.Web;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Harness.Infrastructure.Telemetry;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    private async Task<JsonNode?> ProjectAsync(UiReadRequest request, CancellationToken token)
    {
        var project = await catalog.FindAsync(request.Value("id"), token);
        var endpoint = request.Endpoint["/api/projects/{id}/".Length..];
        return endpoint switch
        {
            "summary" => UiJson.Node(new JsonObjectMap { ["project"] = project }),
            "health" => await HealthAsync(project, request, token),
            "architecture" => await ArchitectureAsync(project, request, token),
            "coupling" => await CouplingAsync(project, request, token),
            "snippet" => await SnippetAsync(project, request, token),
            "structure" => await StructureAsync(project, token),
            "complexity" => await ComplexityAsync(project, token),
            "stack" => await ProjectStackAsync(project, token),
            _ => await ProjectDatabaseAsync(project, endpoint, request, token),
        };
    }

    private async Task<JsonNode?> ProjectDatabaseAsync(
        JsonObject project,
        string endpoint,
        UiReadRequest request,
        CancellationToken token
    )
    {
        using var database = Open(project, endpoint is "tasks" or "tasks-next-id");
        var telemetry = new TelemetryStore(database);
        var tasks = new TaskStore(database);
        return endpoint switch
        {
            "trends" => UiJson.Node(
                new JsonObjectMap { ["days"] = telemetry.Trends(Days(request), DateTimeOffset.UtcNow) }
            ),
            "report" => await ReportAsync(project, database, request, token),
            "providers" => await ProvidersAsync(project, database, request, token),
            "hooks" => await HooksAsync(project, database, token),
            "memories" => Memories(project, database, request, token),
            "memories/{mid}" => MemoryDetail(database, request.Value("mid"), token),
            "applies" => Applying(project, database, request, token),
            "proposals" => Proposals(database, request, token),
            "tasks" => Tasks(tasks, request, token),
            "blueprints" => UiJson.Node(
                new JsonObjectMap { ["blueprints"] = tasks.Blueprints(token).Select(TaskViews.Blueprint) }
            ),
            "tasks/{tid}/events" => UiJson.Node(
                new JsonObjectMap { ["events"] = tasks.Events(request.Value("tid"), token).Select(TaskViews.Event) }
            ),
            "blueprints-next-id" => UiJson.Node(new JsonObjectMap { ["id"] = tasks.NextBlueprintId(token) }),
            "tasks-next-id" => UiJson.Node(
                new JsonObjectMap { ["id"] = tasks.NextId(request.Parameter("prefix"), token) }
            ),
            "blueprint-proposals" => UiJson.Node(
                new JsonObjectMap
                {
                    ["proposals"] = tasks
                        .BlueprintProposals(request.Parameter("status"), token)
                        .Select(TaskViews.BlueprintProposal),
                }
            ),
            "task-proposals" => UiJson.Node(
                new JsonObjectMap
                {
                    ["proposals"] = tasks.Proposals(request.Parameter("status"), token).Select(TaskViews.Proposal),
                }
            ),
            "arch-proposals" => UiJson.Node(
                new JsonObjectMap
                {
                    ["proposals"] = new FindingStore(database).Proposals(
                        request.Parameter("status") is { Length: > 0 } status ? status : "pending",
                        token
                    ),
                }
            ),
            _ => throw new UiRequestException(404, "unknown endpoint"),
        };
    }

    private JsonNode? Memories(
        JsonObject project,
        HarnessDatabase database,
        UiReadRequest request,
        CancellationToken token
    )
    {
        var limit = 100;
        var raw = request.Parameter("limit");
        if (
            raw.Length > 0
            && (
                !int.TryParse(
                    raw,
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out limit
                ) || limit is < 1 or > 500
            )
        )
        {
            throw new UiRequestException(400, "limit must be a whole number from 1 to 500");
        }
        var query = new MemoryQuery
        {
            Text = request.Parameter("q"),
            Types = Split(request.Parameter("type")).Select(MemoryText.ParseType).ToArray(),
            IncludeInactive = request.Parameter("inactive") is "1",
            Limit = limit,
        };
        var store = new MemoryStore(database);
        if (request.Parameter("levels") is "1")
        {
            using var layers = Levels.OpenLayers(store, UiProjectCatalog.Text(project, "id"), token);
            return UiJson.Node(
                new JsonObjectMap
                {
                    ["memories"] = layers.Layers.Search(query, token).Memories.Select(MemoryViews.Memory),
                }
            );
        }
        return UiJson.Node(new JsonObjectMap { ["memories"] = store.Search(query, token).Select(MemoryViews.Memory) });
    }

    private static JsonNode? MemoryDetail(HarnessDatabase database, string id, CancellationToken token)
    {
        try
        {
            return UiJson.Node(MemoryViews.Detail(new MemoryStore(database).Details(id, true, token)));
        }
        catch (InvalidOperationException exception)
        {
            throw new UiRequestException(400, exception.Message);
        }
    }

    private static JsonNode? Applying(
        JsonObject project,
        HarnessDatabase database,
        UiReadRequest request,
        CancellationToken token
    )
    {
        var scope = request.Parameter("scope").Trim();
        var result = new MemoryStore(database).Applying(
            scope.Length is 0 ? "project" : scope,
            project["live"]?["branch"]?.GetValue<string>() ?? "",
            token
        );
        return UiJson.Node(
            new JsonObjectMap
            {
                ["scope"] = result.Scope,
                ["branch"] = result.Branch,
                ["goals"] = result.Goals.Select(MemoryViews.Memory),
                ["constraints"] = result.Constraints.Select(MemoryViews.Memory),
                ["decisions"] = result.Decisions.Select(MemoryViews.Memory),
            }
        );
    }

    private static JsonNode? Proposals(HarnessDatabase database, UiReadRequest request, CancellationToken token)
    {
        var store = new MemoryStore(database);
        var proposals = store
            .Proposals(request.Parameter("status"), 200, token)
            .Select(proposal =>
            {
                var view = MemoryViews.Proposal(proposal);
                if (proposal.Status is "pending")
                {
                    var similar = store.GetProposal(proposal.Id, token).Related;
                    if (similar.Count > 0)
                    {
                        view["similar"] = similar.Select(MemoryViews.Similar).ToArray();
                    }
                }
                return view;
            })
            .ToArray();
        return UiJson.Node(new JsonObjectMap { ["proposals"] = proposals });
    }

    private static JsonNode? Tasks(TaskStore store, UiReadRequest request, CancellationToken token)
    {
        var statuses = Split(request.Parameter("status"));
        if (
            statuses.FirstOrDefault(status => !TaskWorkflow.Statuses.Contains(status, StringComparer.Ordinal)) is
            { } status
        )
        {
            throw new UiRequestException(400, "invalid status " + status);
        }
        return UiJson.Node(
            new JsonObjectMap
            {
                ["tasks"] = store
                    .List(new() { Blueprint = request.Parameter("blueprint"), Statuses = statuses }, token)
                    .Select(TaskViews.Task),
            }
        );
    }

    private async Task<JsonNode?> ReportAsync(
        JsonObject project,
        HarnessDatabase database,
        UiReadRequest request,
        CancellationToken token
    )
    {
        var root = UiProjectCatalog.Text(project, "root");
        var location = await lifecycle.LocateAsync(root, cancellationToken: token);
        var tools = typeof(Harness.Mcp.HarnessMcpServer)
            .Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Harness.Mcp.Schemas.", StringComparison.Ordinal))
            .Select(n => n["Harness.Mcp.Schemas.".Length..^5])
            .Order(StringComparer.Ordinal)
            .ToArray();
        var report = new TelemetryStore(database).Report(
            Days(request),
            DateTimeOffset.UtcNow,
            tools,
            ProviderServerMap.Read(root, location.Configuration.Value.Providers)
        );
        report.Id = UiProjectCatalog.Text(project, "id");
        report.Root = root;
        // the user's findings; the developer's and the tuning numbers go only to the opt-in telemetry
        return UiJson.Node(report.ForUser());
    }
}
