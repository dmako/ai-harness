using System.ComponentModel;
using System.Text.Json.Nodes;
using Harness.Core.Memory;
using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Harness.Core.Web;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Harness.Infrastructure.Workspaces;
using Microsoft.Data.Sqlite;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService : IUiWriteApi
{
    public async Task<JsonNode?> WriteAsync(UiWriteRequest request, CancellationToken token)
    {
        try
        {
            var endpoint = request.Route.Endpoint;
            if (endpoint.StartsWith("/api/projects/{id}/", StringComparison.Ordinal))
            {
                return await WriteProjectAsync(request, token);
            }
            if (endpoint.StartsWith("/api/knowledge/", StringComparison.Ordinal))
            {
                return WriteKnowledge(request, token);
            }
            if (endpoint is "/api/graphify/sessions/{action}")
            {
                return await WriteGraphifyAsync(request, token);
            }
            return await WriteWorkspaceAsync(request, token);
        }
        catch (UiRequestException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException
            )
        {
            throw new UiRequestException(400, exception.Message);
        }
        catch (Exception exception) when (exception is SqliteException or TimeoutException or Win32Exception)
        {
            throw new UiRequestException(500, exception.Message);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new UiRequestException(500, "operation timed out");
        }
        finally
        {
            catalog.Forget();
        }
    }

    private async Task<JsonNode?> WriteProjectAsync(UiWriteRequest request, CancellationToken token)
    {
        var route = request.Route;
        var endpoint = route.Endpoint["/api/projects/{id}/".Length..];
        if (endpoint is "rename")
        {
            var fields = new UiWriteBody(request.Body, "name", "title");
            if (fields.Optional("name") is null && fields.Optional("title") is null)
            {
                throw new UiRequestException(400, "give a name or a title");
            }
            var renamed = await lifecycle.Registry.RenameAsync(
                route.Value("id"),
                fields.Optional("name"),
                fields.Optional("title"),
                token
            );
            return UiJson.Node(
                new JsonObjectMap
                {
                    ["project_id"] = route.Value("id"),
                    ["name"] = WorkspaceStore.ShortName(renamed),
                    ["title"] = UiProjectCatalog.Text(renamed, "title"),
                }
            );
        }
        var project = await catalog.FindAsync(route.Value("id"), token);
        using var database = Open(project, tasks: true);
        var store = new MemoryStore(database);
        var human = HumanActor.Current();
        return endpoint switch
        {
            "memories/{mid}/supersede" => CorrectMemory(store, route.Value("mid"), request.Body, null, token),
            "memories/{mid}/retire" => RetireMemory(store, route.Value("mid"), request.Body, null, token),
            "memories/{mid}/promote" => await PromoteAsync(project, database, request, token),
            "proposals/{pid}/approve" => ApproveMemory(store, route.Value("pid"), request.Body, token),
            "proposals/{pid}/reject" => RejectMemory(store, route.Value("pid"), request.Body, token),
            "tasks" => CreateTask(database, request.Body, token),
            "tasks/{tid}/edit" => EditTask(database, request, token),
            "blueprints" => SaveBlueprint(database, null, request.Body, token),
            "blueprints/{bid}/edit" => SaveBlueprint(database, route.Value("bid"), request.Body, token),
            "blueprints/{bid}/remove" => RemoveBlueprint(database, request, token),
            "blueprint-proposals/{pid}/approve" => ApproveBlueprint(database, request, token),
            "blueprint-proposals/{pid}/reject" => UiJson.Node(
                new JsonObjectMap
                {
                    ["proposal"] = TaskViews.BlueprintProposal(
                        new TaskStore(database).RejectBlueprint(
                            route.Value("pid"),
                            new UiWriteBody(request.Body, "note").Text("note"),
                            human,
                            token
                        )
                    ),
                }
            ),
            "task-proposals/{pid}/approve" => ApproveTask(database, request, token),
            "task-proposals/{pid}/reject" => RejectTask(database, request, token),
            "findings/{action}" => await TriageProjectAsync(project, database, request, token),
            "arch-proposals/{pid}/approve" => await ApproveFindingAsync(project, database, request, token),
            "arch-proposals/{pid}/reject" => UiJson.Node(
                new JsonObjectMap
                {
                    ["proposal"] = new FindingStore(database).Decide(
                        route.Value("pid"),
                        "rejected",
                        human,
                        new UiWriteBody(request.Body, "note").Text("note"),
                        token: token
                    ),
                }
            ),
            _ => throw new UiRequestException(404, "unknown endpoint"),
        };
    }

    private static JsonNode? CorrectMemory(
        MemoryStore store,
        string id,
        JsonObject body,
        MemoryScope? scope,
        CancellationToken token
    )
    {
        var fields = scope is null
            ? new UiWriteBody(body, "content", "reason")
            : new UiWriteBody(body, "level", "content", "reason");
        if (string.IsNullOrWhiteSpace(fields.Text("content")) || string.IsNullOrWhiteSpace(fields.Text("reason")))
        {
            throw new UiRequestException(400, "a correction needs the new content and a reason");
        }
        var result = store.Supersede(
            id,
            new()
            {
                Content = fields.Text("content"),
                SupersedeReason = fields.Text("reason"),
                Source = new("user", "harness ui correction", HumanActor.Current()),
            },
            token
        );
        return UiJson.Node(
            new JsonObjectMap
            {
                ["status"] = result.Status,
                ["memory"] = MemoryViews.Memory(result.Memory! with { Scope = scope ?? result.Memory.Scope }),
            }
        );
    }

    private static JsonNode? RetireMemory(
        MemoryStore store,
        string id,
        JsonObject body,
        MemoryScope? scope,
        CancellationToken token
    )
    {
        var fields = scope is null
            ? new UiWriteBody(body, "reason")
            : new UiWriteBody(body, "level", "content", "reason");
        var memory = store.Retire(id, HumanActor.Current(), fields.Text("reason"), token);
        return UiJson.Node(
            new JsonObjectMap { ["memory"] = MemoryViews.Memory(memory with { Scope = scope ?? memory.Scope }) }
        );
    }

    private static JsonObject? ApproveMemory(MemoryStore store, string id, JsonObject body, CancellationToken token)
    {
        var fields = new UiWriteBody(body, "content", "supersedes", "keep_both", "note");
        var result = store.Approve(
            id,
            new()
            {
                Content = fields.Text("content"),
                Supersedes = fields.List("supersedes"),
                KeepBoth = fields.Flag("keep_both"),
                Note = fields.Text("note"),
                By = HumanActor.Current(),
            },
            token
        );
        var view = UiJson
            .Node(
                new JsonObjectMap
                {
                    ["status"] = result.Status,
                    ["proposal"] = MemoryViews.Proposal(result.Proposal),
                    ["similar"] = result.Similar.Count is 0
                        ? null
                        : result.Similar.Select(MemoryViews.Similar).ToArray(),
                }
            )!
            .AsObject();
        if (result.Memory is not null)
        {
            view["memory"] = UiJson.Node(MemoryViews.Memory(result.Memory));
        }
        return view;
    }

    private static JsonNode? RejectMemory(MemoryStore store, string id, JsonObject body, CancellationToken token) =>
        UiJson.Node(
            new JsonObjectMap
            {
                ["proposal"] = MemoryViews.Proposal(
                    store.Reject(id, HumanActor.Current(), new UiWriteBody(body, "note").Text("note"), token)
                ),
            }
        );

    private static TaskDefinition TaskForm(JsonObject body)
    {
        var fields = new UiWriteBody(
            body,
            "id",
            "prefix",
            "title",
            "goal",
            "type",
            "priority",
            "blueprint",
            "depends_on",
            "acceptance",
            "scope"
        );
        return new()
        {
            Id = fields.Text("id"),
            Title = fields.Text("title"),
            Goal = fields.Text("goal").Trim(),
            Type = fields.Text("type").Trim(),
            Priority = fields.Text("priority").Trim(),
            Blueprint = fields.Text("blueprint").Trim(),
            DependsOn = fields
                .List("depends_on")
                .Select(s => s.Trim().ToUpperInvariant())
                .Where(s => s.Length > 0)
                .ToArray(),
            Acceptance = Clean(fields.List("acceptance")),
            Scope = Clean(fields.List("scope")),
        };
    }

    private static string Prefix(JsonObject body) =>
        body.FirstOrDefault(p => p.Key.Equals("prefix", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>()
        ?? "";

    private static string[] Clean(IEnumerable<string> strings) =>
        strings.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

    private static JsonNode? CreateTask(HarnessDatabase database, JsonObject body, CancellationToken token) =>
        UiJson.Node(
            new JsonObjectMap
            {
                ["task"] = TaskViews.Task(
                    new TaskStore(database).Create(
                        TaskForm(body),
                        Prefix(body),
                        new(HumanActor.Current(), "ui", "added in harness ui"),
                        token
                    )
                ),
            }
        );

    private static JsonNode? EditTask(HarnessDatabase database, UiWriteRequest request, CancellationToken token) =>
        UiJson.Node(
            new JsonObjectMap
            {
                ["task"] = TaskViews.Task(
                    new TaskStore(database).Edit(
                        request.Route.Value("tid"),
                        TaskForm(request.Body),
                        HumanActor.Current(),
                        token
                    )
                ),
            }
        );

    // A new blueprint (id null) gives its id; an edited one keeps it.
    private static JsonNode? SaveBlueprint(
        HarnessDatabase database,
        string? id,
        JsonObject body,
        CancellationToken token
    )
    {
        var fields = id is null
            ? new UiWriteBody(body, "id", "title", "goal", "exit", "notes")
            : new UiWriteBody(body, "title", "goal", "exit", "notes");
        BlueprintDefinition definition = new(
            id ?? fields.Text("id"),
            fields.Text("title"),
            fields.Text("goal"),
            Clean(fields.List("exit")),
            fields.Text("notes")
        );
        var store = new TaskStore(database);
        var saved = id is null
            ? store.CreateBlueprint(definition, "ui", token)
            : store.EditBlueprint(id, definition, "ui", token);
        return UiJson.Node(new JsonObjectMap { ["blueprint"] = saved.Id });
    }

    // The proposed blueprint as proposed, or as the author restated it ("edit": id for a new one, title, goal,
    // exit, notes).
    private static JsonNode? ApproveBlueprint(HarnessDatabase database, UiWriteRequest request, CancellationToken token)
    {
        var edit = new UiWriteBody(request.Body, "edit").Object("edit");
        BlueprintDefinition? definition = null;
        if (edit is not null)
        {
            var fields = new UiWriteBody(edit, "id", "title", "goal", "exit", "notes");
            definition = new(
                fields.Text("id"),
                fields.Text("title"),
                fields.Text("goal"),
                Clean(fields.List("exit")),
                fields.Text("notes")
            );
        }

        var approved = new TaskStore(database).ApproveBlueprint(
            request.Route.Value("pid"),
            definition,
            HumanActor.Current(),
            token
        );
        return UiJson.Node(
            new JsonObjectMap
            {
                ["proposal"] = TaskViews.BlueprintProposal(approved.Proposal),
                ["blueprint"] = approved.Blueprint.Id,
            }
        );
    }

    private static JsonNode? RemoveBlueprint(HarnessDatabase database, UiWriteRequest request, CancellationToken token)
    {
        var fields = new UiWriteBody(request.Body, "tasks", "move_to");
        var tasks = fields.Text("tasks") switch
        {
            "" => BlueprintTasks.Keep,
            "move" => BlueprintTasks.Move,
            "delete" => BlueprintTasks.Delete,
            var other => throw new UiRequestException(400, "tasks must be move or delete, not " + other),
        };
        var removed = new TaskStore(database).RemoveBlueprint(
            request.Route.Value("bid"),
            tasks,
            fields.Text("move_to"),
            HumanActor.Current(),
            token
        );
        return UiJson.Node(
            new JsonObjectMap
            {
                ["blueprint"] = removed.Id,
                ["moved"] = removed.Moved,
                ["deleted"] = removed.Deleted,
                ["unlinked"] = removed.Unlinked,
            }
        );
    }

    private static JsonNode? ApproveTask(HarnessDatabase database, UiWriteRequest request, CancellationToken token)
    {
        var edit = new UiWriteBody(request.Body, "task").Object("task");
        var result = new TaskStore(database).Approve(
            request.Route.Value("pid"),
            edit is null ? null : TaskForm(edit),
            edit is null ? "" : Prefix(edit),
            HumanActor.Current(),
            token
        );
        return UiJson.Node(
            new JsonObjectMap
            {
                ["proposal"] = TaskViews.Proposal(result.Proposal),
                ["task"] = TaskViews.Task(result.Task),
            }
        );
    }

    private static JsonNode? RejectTask(HarnessDatabase database, UiWriteRequest request, CancellationToken token) =>
        UiJson.Node(
            new JsonObjectMap
            {
                ["proposal"] = TaskViews.Proposal(
                    new TaskStore(database).Reject(
                        request.Route.Value("pid"),
                        new UiWriteBody(request.Body, "note").Text("note"),
                        HumanActor.Current(),
                        token
                    )
                ),
            }
        );

    private JsonNode? WriteKnowledge(UiWriteRequest request, CancellationToken token)
    {
        var adding = request.Route.Endpoint is "/api/knowledge/memories";
        var fields = adding
            ? new UiWriteBody(request.Body, "level", "type", "content", "SourceRef")
            : new UiWriteBody(request.Body, "level", "content", "reason");
        var scope = Wider(fields.Text("level"));
        var type = adding ? MemoryText.ParseType(fields.Text("type")) : "";
        var path = Levels.KnowledgePath(Levels.Load(), scope);
        if (!adding && !File.Exists(path))
        {
            throw new UiRequestException(400, "nothing is recorded at " + scope);
        }
        using var database = Levels.OpenLevel(Levels.Load(), scope, "", token);
        var store = new MemoryStore(database);
        if (!adding)
        {
            return request.Route.Endpoint.EndsWith("/retire", StringComparison.Ordinal)
                ? RetireMemory(store, request.Route.Value("mid"), request.Body, scope, token)
                : CorrectMemory(store, request.Route.Value("mid"), request.Body, scope, token);
        }
        var reference = string.IsNullOrWhiteSpace(fields.Text("SourceRef")) ? "harness ui" : fields.Text("SourceRef");
        var result = store.Put(
            new()
            {
                Type = type,
                Content = fields.Text("content"),
                Source = new("user", reference, HumanActor.Current()),
            },
            token
        );
        JsonObject view = new()
        {
            ["status"] = result.Status,
            ["similar"] = result.Similar.Count is 0
                ? null
                : UiJson.Node(
                    result.Similar.Select(s => MemoryViews.Memory(s.Memory with { Scope = scope })).ToArray()
                ),
        };
        if (result.Memory is not null)
        {
            view["memory"] = UiJson.Node(MemoryViews.Memory(result.Memory with { Scope = scope }));
        }
        return view;
    }

    private static MemoryScope Wider(string level)
    {
        var scope = MemoryScope.Parse(level);
        return scope.Wider ? scope : throw new UiRequestException(400, "level must be user or workspace:<name>");
    }

    private async Task<JsonNode?> PromoteAsync(
        JsonObject project,
        HarnessDatabase database,
        UiWriteRequest request,
        CancellationToken token
    )
    {
        var fields = new UiWriteBody(request.Body, "to", "dry_run", "force");
        var scope = Wider(fields.Text("to"));
        MemoryPromotion result;
        if (fields.Flag("dry_run"))
        {
            var memory = new MemoryStore(database).Get(request.Route.Value("mid"), token);
            if (!memory.Current(new(DateTimeOffset.UtcNow)))
            {
                throw new UiRequestException(400, memory.Id + " is not current (superseded or retired)");
            }
            if (memory.Scope != MemoryScope.Project)
            {
                throw new UiRequestException(
                    400,
                    memory.Id + " applies to " + memory.Scope + " only; promote project-wide memories"
                );
            }
            _ = Levels.KnowledgePath(Levels.Load(), scope);
            var holds = KnowledgeLevels.Across(
                Levels.OtherProjects(
                    UiProjectCatalog.Text(project, "id"),
                    scope.Kind is "workspace" ? scope.Value : null
                ),
                s => s.Similar(memory.Type, memory.Content, token).Select(m => m.Memory).ToArray(),
                token
            );
            result = new(memory, scope.ToString(), holds, "would_create");
        }
        else
        {
            var service = new MemoryService(_ => Task.FromResult(database), Levels, _ => Task.FromResult(""));
            result = await service.PromoteAsync(
                request.Route.Value("mid"),
                scope,
                HumanActor.Current(),
                fields.Flag("force"),
                token
            );
        }
        var view = UiJson
            .Node(
                new JsonObjectMap
                {
                    ["from"] = MemoryViews.Memory(result.From),
                    ["level"] = result.Level,
                    ["holds_in"] = result.HoldsIn.Select(MemoryViews.Found).ToArray(),
                    ["status"] = result.Status,
                    ["retired"] = result.Retired,
                }
            )!
            .AsObject();
        if (result.Memory is not null)
        {
            view["memory"] = UiJson.Node(MemoryViews.Memory(result.Memory));
        }
        if (result.SimilarAtLevel.Count > 0)
        {
            view["similar_at_level"] = UiJson.Node(result.SimilarAtLevel);
        }
        return view;
    }
}
