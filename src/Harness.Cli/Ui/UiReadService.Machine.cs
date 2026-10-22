using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Harness.Core.Memory;
using Harness.Core.Serialization;
using Harness.Core.Web;
using Harness.Infrastructure.Clients;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Harness.Infrastructure.Telemetry;
using Harness.Infrastructure.Workspaces;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    private KnowledgeLevels Levels => new(lifecycle.GlobalConfigurationPath, lifecycle.Registry);

    private JsonNode? Machine()
    {
        JsonObject telemetry = new() { ["enabled"] = false };
        var path = Path.Combine(GlobalDirectory, "telemetry.json");
        try
        {
            if (File.Exists(path) && JsonNode.Parse(FileEditor.Read(path)) is JsonObject state)
            {
                telemetry["enabled"] = state["enabled"]?.DeepClone() ?? JsonValue.Create(false);
                telemetry["endpoint"] = state["endpoint"]?.DeepClone() ?? JsonValue.Create("");
                telemetry["last_sent"] = state["last_sent"]?.DeepClone() ?? JsonValue.Create("0001-01-01T00:00:00Z");
                telemetry["projects"] = state["projects"] is JsonObject projects ? projects.Count : 0;
            }
        }
        catch (Exception exception) when (ReadError(exception))
        { /* Only public opt-in metadata is shown. */
        }
        return UiJson.Node(
            new JsonObjectMap
            {
                ["build"] = BuildInformation.Current(),
                ["paths"] = new JsonObjectMap
                {
                    ["global"] = GlobalDirectory,
                    ["registry"] = Path.Combine(GlobalDirectory, "projects.json"),
                    ["workspaces"] = Path.Combine(GlobalDirectory, "workspaces.json"),
                    ["knowledge"] = Path.Combine(GlobalDirectory, "knowledge"),
                },
                ["telemetry"] = telemetry,
            }
        );
    }

    private async Task<JsonNode?> WorkspacesAsync(CancellationToken token)
    {
        var projects = await catalog.ListAsync(token);
        var result = Workspaces
            .Load()
            .Select(workspace =>
            {
                JsonObject view = new()
                {
                    ["id"] = workspace.Id,
                    ["name"] = workspace.Name,
                    ["created"] = workspace.Created,
                };
                if (workspace.Title.Length > 0)
                {
                    view["title"] = workspace.Title;
                }
                view["members"] = UiJson.Node(
                    workspace
                        .Members.Select(id =>
                        {
                            var resolved = Workspaces.Resolve([id]);
                            var member = resolved.Count > 0 ? resolved[0] : null;
                            var state =
                                projects
                                    .FirstOrDefault(p => UiProjectCatalog.Text(p, "id") == id)
                                    ?["live"]?["state"]?.GetValue<string>()
                                ?? "unknown";
                            JsonObject row = new()
                            {
                                ["project"] = member?.Name ?? id,
                                ["project_id"] = id,
                                ["root"] = member?.Root ?? "",
                                ["state"] = member is null ? "unregistered" : state,
                            };
                            if (member?.Title is { Length: > 0 } title)
                            {
                                row["title"] = title;
                            }
                            return row;
                        })
                        .ToArray()
                );
                return view;
            })
            .ToArray();
        return UiJson.Node(new JsonObjectMap { ["workspaces"] = result });
    }

    private JsonNode? Discover(UiReadRequest request)
    {
        var folder = request.Parameter("folder").Trim();
        if (folder.Length is 0 || !Path.IsPathFullyQualified(folder))
        {
            throw new UiRequestException(400, "folder must be an absolute path");
        }
        return UiJson.Node(
            new JsonObjectMap
            {
                ["folder"] = Path.GetFullPath(folder),
                ["projects"] = Workspaces
                    .Discover(folder)
                    .Select(p => new JsonObjectMap
                    {
                        ["project_id"] = UiProjectCatalog.Text(p, "id"),
                        ["project"] = UiProjectCatalog.Display(p),
                        ["root"] = UiProjectCatalog.Text(p, "root"),
                    }),
            }
        );
    }

    private async Task<JsonNode?> ActivityAsync(UiReadRequest request, CancellationToken token)
    {
        var days = int.TryParse(request.Parameter("days"), out var number) && number is > 0 and <= 90 ? number : 7;
        var limit = int.TryParse(request.Parameter("limit"), out number) && number is > 0 and <= 200 ? number : 40;
        List<JsonObject> items = [];
        foreach (var project in await catalog.ListAsync(token))
        {
            if (
                project["live"]?["state"]?.GetValue<string>() is not "ok"
                || request.Parameter("project") is { Length: > 0 } only && UiProjectCatalog.Text(project, "id") != only
            )
            {
                continue;
            }
            try
            {
                using var database = Open(project);
                foreach (
                    var item in new TelemetryStore(database).Activity(DateTimeOffset.UtcNow.AddDays(-days), limit) ?? []
                )
                {
                    var view = UiJson.Node(item)!.AsObject();
                    view["project_id"] = UiProjectCatalog.Text(project, "id");
                    view["project"] = UiProjectCatalog.Display(project);
                    items.Add(view);
                }
            }
            catch (Exception exception) when (ReadError(exception) || exception is UiRequestException)
            { /* A broken project does not block the machine feed. */
            }
        }
        return UiJson.Node(
            new JsonObjectMap
            {
                ["days"] = days,
                ["items"] = items
                    .OrderByDescending(i => MemoryTimestamp.Parse(UiProjectCatalog.Text(i, "at")))
                    .Take(limit)
                    .ToArray(),
            }
        );
    }

    private HarnessDatabase? OpenLevel(MemoryScope scope)
    {
        string path;
        try
        {
            path = Levels.KnowledgePath(Levels.Load(), scope);
        }
        catch (InvalidOperationException exception)
        {
            throw new UiRequestException(400, exception.Message);
        }
        return File.Exists(path) ? HarnessDatabase.OpenReadOnly(path) : null;
    }

    private JsonNode? Knowledge(UiReadRequest request, CancellationToken token)
    {
        var scope = MemoryScope.Parse(request.Parameter("level").Trim());
        if (scope.Kind is not ("user" or "workspace"))
        {
            throw new UiRequestException(400, "level must be user or workspace:<name>");
        }
        using var database = OpenLevel(scope);
        if (request.Endpoint is "/api/knowledge/memories/{mid}")
        {
            if (database is null)
            {
                throw new UiRequestException(400, "no such memory");
            }
            var detail = new MemoryStore(database).Details(request.Value("mid"), true, token);
            var withScope = detail with
            {
                Memory = detail.Memory with { Scope = scope },
                History = detail.History.Select(m => m with { Scope = scope }).ToArray(),
            };
            var view = UiJson.Node(MemoryViews.Detail(withScope))!.AsObject();
            return view;
        }
        var memories = database is null
            ? []
            : new MemoryStore(database).Search(
                new() { IncludeInactive = request.Parameter("inactive") is "1", Limit = 500 },
                token
            );
        var projects = Levels.OtherProjects("", scope.Kind is "workspace" ? scope.Value : null);
        var rows = memories
            .Select(memory =>
            {
                var view = MemoryViews.Memory(memory with { Scope = scope });
                if (memory.Current(new MemoryTimestamp(DateTimeOffset.UtcNow)))
                {
                    var overrides = KnowledgeLevels.Across(
                        projects,
                        store =>
                            store.Similar(memory.Type, memory.Content, token).Take(1).Select(m => m.Memory).ToArray(),
                        token
                    );
                    if (overrides.Count > 0)
                    {
                        view["overridden_in"] = overrides.Select(p => p.Project).ToArray();
                    }
                }
                return view;
            })
            .ToArray();
        return UiJson.Node(new JsonObjectMap { ["level"] = scope.ToString(), ["memories"] = rows });
    }

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "SQLite lower() and the Go search query use lowercase text."
    )]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Each level database is owned by a using declaration in the loop."
    )]
    private async Task<JsonNode?> SearchAsync(UiReadRequest request, CancellationToken token)
    {
        var query = request.Parameter("q").Trim();
        if (Encoding.UTF8.GetByteCount(query) < 2)
        {
            throw new UiRequestException(400, "type at least two characters");
        }
        List<JsonObject> hits = [];
        Dictionary<string, int> counts = [];
        bool Has(params string[] values) => values.Any(v => v.Contains(query, StringComparison.OrdinalIgnoreCase));
        void Add(
            string kind,
            string title,
            string detail = "",
            JsonObject? project = null,
            string workspace = "",
            string reference = "",
            string scope = ""
        )
        {
            if (counts.GetValueOrDefault(kind) >= 20)
            {
                return;
            }
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            JsonObject hit = new() { ["kind"] = kind, ["title"] = title };
            foreach (
                var (key, value) in new[]
                {
                    ("detail", detail),
                    ("project_id", project is null ? "" : UiProjectCatalog.Text(project, "id")),
                    ("project", project is null ? "" : UiProjectCatalog.Display(project)),
                    ("workspace", workspace),
                    ("ref", reference),
                    ("scope", scope),
                }
            )
            {
                if (value.Length > 0)
                {
                    hit[key] = value;
                }
            }
            hits.Add(hit);
        }
        var projects = await catalog.ListAsync(token);
        foreach (
            var project in projects.Where(p =>
                Has(UiProjectCatalog.Display(p), WorkspaceStore.ShortName(p), UiProjectCatalog.Text(p, "root"))
            )
        )
        {
            Add("project", UiProjectCatalog.Display(project), UiProjectCatalog.Text(project, "root"), project);
        }
        foreach (var workspace in Workspaces.Load().Where(w => Has(w.Name, w.Title)))
        {
            Add(
                "workspace",
                workspace.Title.Length > 0 ? workspace.Title : workspace.Name,
                workspace.Name,
                workspace: workspace.Name
            );
        }
        foreach (var project in projects.Where(p => p["live"]?["state"]?.GetValue<string>() is "ok"))
        {
            try
            {
                using var database = Open(project);
                foreach (var memory in new MemoryStore(database).Search(new() { Text = query, Limit = 8 }, token))
                {
                    Add(
                        "memory",
                        memory.Content,
                        memory.Type,
                        project,
                        reference: memory.Id,
                        scope: memory.Scope.ToString()
                    );
                }
                using var connection = database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT id,title,status FROM tasks WHERE lower(id) LIKE $q OR lower(title) LIKE $q ORDER BY id LIMIT 8";
                command.Parameters.AddWithValue("$q", "%" + query.ToLowerInvariant() + "%");
                using (var reader = await command.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        Add(
                            "task",
                            reader.GetString(0) + " " + reader.GetString(1),
                            reader.GetString(2),
                            project,
                            reference: reader.GetString(0)
                        );
                    }
                }
                command.CommandText =
                    "SELECT id,title FROM milestones WHERE lower(id) LIKE $q OR lower(title) LIKE $q UNION SELECT DISTINCT milestone,'' FROM tasks WHERE lower(milestone) LIKE $q AND milestone NOT IN (SELECT id FROM milestones) ORDER BY 1 LIMIT 5";
                using var milestones = await command.ExecuteReaderAsync(token);
                while (await milestones.ReadAsync(token))
                {
                    Add(
                        "milestone",
                        (milestones.GetString(0) + " " + milestones.GetString(1)).Trim(),
                        project: project,
                        reference: milestones.GetString(0)
                    );
                }
            }
            catch (Exception exception) when (ReadError(exception) || exception is UiRequestException)
            { /* Search remains useful with unreadable peers. */
            }
        }
        foreach (
            var scope in new[] { MemoryScope.Parse("user") }.Concat(
                Workspaces.Load().Select(w => MemoryScope.Parse("workspace:" + w.Name))
            )
        )
        {
            using var database = OpenLevel(scope);
            if (database is null)
            {
                continue;
            }
            foreach (var memory in new MemoryStore(database).Search(new() { Text = query, Limit = 8 }, token))
            {
                Add(
                    "memory",
                    memory.Content,
                    memory.Type,
                    workspace: scope.Kind is "workspace" ? scope.Value : "",
                    reference: memory.Id,
                    scope: scope.ToString()
                );
            }
        }
        return UiJson.Node(
            new JsonObjectMap
            {
                ["query"] = query,
                ["hits"] = hits.OrderBy(h =>
                        UiProjectCatalog.Text(h, "kind") switch
                        {
                            "project" => 0,
                            "workspace" => 1,
                            "memory" => 2,
                            "task" => 3,
                            _ => 4,
                        }
                    )
                    .ToArray(),
                ["per_kind"] = 20,
            }
        );
    }
}
