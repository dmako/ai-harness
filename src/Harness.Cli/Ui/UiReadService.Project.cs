using System.Text.Json.Nodes;
using Harness.Core.Memory;
using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Harness.Core.Web;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;
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
            "coupling" => await ViewAsync(project, endpoint, () => CouplingAsync(project, token), token),
            "snippet" => await SnippetAsync(project, request, token),
            "structure" => await ViewAsync(project, endpoint, () => StructureAsync(project, token), token),
            "complexity" => await ViewAsync(project, endpoint, () => ComplexityAsync(project, token), token),
            "stack" => await ViewAsync(project, endpoint, () => ProjectStackAsync(project, token), token),
            "agents/{key}/log" => AgentLog(project, request),
            _ => await ProjectDatabaseAsync(project, endpoint, request, token),
        };
    }

    /// <summary>
    /// A project's architecture view, from harness ui's view cache while the project and its workspaces' members
    /// are unchanged.
    /// </summary>
    private Task<JsonNode?> ViewAsync(
        JsonObject project,
        string endpoint,
        Func<Task<JsonNode?>> compute,
        CancellationToken token
    )
    {
        if (views is null)
        {
            return compute();
        }

        var id = UiProjectCatalog.Text(project, "id");
        List<ViewMember> members =
        [
            new(id, UiProjectCatalog.Text(project, "root"), UiProjectCatalog.Text(project, "db_path")),
        ];
        members.AddRange(
            Workspaces
                .Members(id)
                .Where(member => member.Id != id)
                .Select(member => new ViewMember(member.Id, member.Root, member.DatabasePath))
        );
        return views.GetAsync("project:" + id + ":" + endpoint, members, compute, token);
    }

    private async Task<JsonNode?> ProjectDatabaseAsync(
        JsonObject project,
        string endpoint,
        UiReadRequest request,
        CancellationToken token
    )
    {
        using var database = Open(
            project,
            endpoint
                is "tasks"
                    or "tasks-next-id"
                    or "board"
                    or "tasks/{tid}/sessions"
                    or "tasks/{tid}/sessions/{client}/{sid}/tail"
        );
        var telemetry = new TelemetryStore(database);
        var tasks = new TaskStore(database);
        return endpoint switch
        {
            "trends" => UiJson.Node(
                new JsonObjectMap { ["days"] = telemetry.Trends(Days(request), DateTimeOffset.UtcNow) }
            ),
            "report" => await ReportAsync(project, database, request, token),
            "providers" => await ProvidersAsync(project, database, request, token),
            "scm" => await ProjectScmAsync(project, database, request, token),
            "hooks" => await HooksAsync(project, database, token),
            "memories" => Memories(project, database, request, token),
            "memories/{mid}" => MemoryDetail(database, request.Value("mid"), token),
            "applies" => Applying(project, database, request, token),
            "proposals" => Proposals(database, request, token),
            "tasks" => Tasks(tasks, request, token),
            "board" => Board(UiProjectCatalog.Text(project, "id"), tasks, token),
            "blueprints" => UiJson.Node(
                new JsonObjectMap { ["blueprints"] = tasks.Blueprints(token).Select(TaskViews.Blueprint) }
            ),
            "tasks/{tid}/events" => UiJson.Node(
                new JsonObjectMap { ["events"] = tasks.Events(request.Value("tid"), token).Select(TaskViews.Event) }
            ),
            "tasks/{tid}/sessions" => TaskSessions(project, database, tasks, request.Value("tid"), token),
            "tasks/{tid}/sessions/{client}/{sid}/tail" => TranscriptTail(tasks, request, token),
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
        var view = SharedView(store, token);
        // shared=removed: the memories a Git change took out of project knowledge, the Knowledge Inbox's items
        var shared = request.Parameter("shared");
        Func<MemoryRecord, bool> wanted = shared.Length is 0
            ? _ => true
            : memory => string.Equals(view(memory)["shared"] as string, shared, StringComparison.Ordinal);
        if (request.Parameter("levels") is "1")
        {
            using var layers = Levels.OpenLayers(store, UiProjectCatalog.Text(project, "id"), token);
            return UiJson.Node(
                new JsonObjectMap
                {
                    ["memories"] = layers.Layers.Search(query, token).Memories.Where(wanted).Select(view),
                }
            );
        }
        return UiJson.Node(new JsonObjectMap { ["memories"] = store.Search(query, token).Where(wanted).Select(view) });
    }

    /// <summary>A memory's view with whether it is the team's: project, removed (from it by Git) or local ("").</summary>
    private static Func<MemoryRecord, Dictionary<string, object?>> SharedView(
        MemoryStore store,
        CancellationToken token
    )
    {
        var states = store.SharedStates(token);
        return memory =>
        {
            var view = MemoryViews.Memory(memory);
            view["shared"] = states.GetValueOrDefault(memory.Id, SharedState.Local);
            return view;
        };
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
        var view = SharedView(new MemoryStore(database), token);
        return UiJson.Node(
            new JsonObjectMap
            {
                ["scope"] = result.Scope,
                ["branch"] = result.Branch,
                ["goals"] = result.Goals.Select(view),
                ["constraints"] = result.Constraints.Select(view),
                ["decisions"] = result.Decisions.Select(view),
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
