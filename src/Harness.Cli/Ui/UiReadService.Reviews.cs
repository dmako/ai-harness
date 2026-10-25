using System.Text.Json.Nodes;
using Harness.Core.Architecture;
using Harness.Core.Findings;
using Harness.Core.Graphify;
using Harness.Core.Memory;
using Harness.Core.Serialization;
using Harness.Core.Web;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Graphify;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    private async Task<JsonNode?> WriteWorkspaceAsync(UiWriteRequest request, CancellationToken token)
    {
        var name = request.Route.Value("name");
        var action = request.Route.Endpoint.Split('/')[^1];
        if (request.Route.Endpoint.EndsWith("/findings/{action}", StringComparison.Ordinal))
        {
            return await TriageWorkspaceAsync(request, token);
        }
        if (request.Route.Endpoint is "/api/workspaces")
        {
            var fields = new UiWriteBody(request.Body, "name", "title", "project_ids");
            Registered(fields.List("project_ids"));
            var workspace = await Workspaces.CreateAsync(fields.Text("name").Trim(), token);
            if (!string.IsNullOrWhiteSpace(fields.Text("title")))
            {
                workspace = await Workspaces.RenameAsync(workspace.Name, "", fields.Text("title"), token);
            }
            var added = fields.List("project_ids").Length is 0
                ? []
                : await Workspaces.JoinAsync(workspace.Name, fields.List("project_ids"), token);
            return UiJson.Node(new JsonObjectMap { ["workspace"] = workspace.Name, ["added"] = added });
        }
        return action switch
        {
            "join" => await JoinAsync(name, request.Body, token),
            "leave" => await LeaveAsync(name, request.Body, token),
            "delete" => await DeleteWorkspaceAsync(name, request.Body, token),
            "rename" => await RenameWorkspaceAsync(name, request.Body, token),
            _ => throw new UiRequestException(404, "unknown endpoint"),
        };
    }

    private async Task<JsonNode?> DeleteWorkspaceAsync(string name, JsonObject body, CancellationToken token)
    {
        _ = new UiWriteBody(body);
        await Workspaces.DeleteAsync(name, token);
        return UiJson.Node(new JsonObjectMap { ["deleted"] = name });
    }

    private async Task<JsonNode?> RenameWorkspaceAsync(string name, JsonObject body, CancellationToken token)
    {
        var fields = new UiWriteBody(body, "name", "title");
        if (string.IsNullOrWhiteSpace(fields.Text("name")) && fields.Optional("title") is null)
        {
            throw new UiRequestException(400, "give a new name or a title");
        }
        var workspace = await Workspaces.RenameAsync(name, fields.Text("name"), fields.Optional("title"), token);
        return UiJson.Node(new JsonObjectMap { ["workspace"] = workspace.Name, ["title"] = workspace.Title });
    }

    private void Registered(IReadOnlyList<string> ids)
    {
        var registered = lifecycle.Registry.Load()["projects"]!
            .AsArray()
            .Select(p => p!["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        if (ids.FirstOrDefault(id => !registered.Contains(id)) is { } unknown)
        {
            throw new UiRequestException(400, unknown + " is not a project registered on this machine");
        }
    }

    private async Task<JsonNode?> JoinAsync(string name, JsonObject body, CancellationToken token)
    {
        var ids = new UiWriteBody(body, "project_ids").List("project_ids");
        if (ids.Length is 0)
        {
            throw new UiRequestException(400, "project_ids is empty: choose the projects to add");
        }
        Registered(ids);
        return UiJson.Node(
            new JsonObjectMap { ["workspace"] = name, ["added"] = await Workspaces.JoinAsync(name, ids, token) }
        );
    }

    private async Task<JsonNode?> LeaveAsync(string name, JsonObject body, CancellationToken token)
    {
        var id = new UiWriteBody(body, "project_id").Text("project_id");
        if (!await Workspaces.LeaveAsync(name, id, token))
        {
            throw new UiRequestException(400, id + " is not a member of " + name);
        }
        return UiJson.Node(new JsonObjectMap { ["workspace"] = name, ["removed"] = id });
    }

    private async Task<JsonNode?> TriageProjectAsync(
        JsonObject project,
        HarnessDatabase database,
        UiWriteRequest request,
        CancellationToken token
    )
    {
        var body = new UiWriteBody(request.Body, "key", "reason", "project");
        var findings = await new FindingSets(
            UiProjectCatalog.Text(project, "root"),
            UiProjectCatalog.Text(project, "id"),
            database,
            lifecycle.GlobalConfigurationPath,
            lifecycle.Registry
        ).ReadAsync("project", token);
        var found = findings.Groups.SelectMany(g => g.Findings).Where(f => f.Status is not "gone").ToArray();
        return Triage(
            database,
            database,
            UiProjectCatalog.Text(project, "id"),
            found,
            request.Route.Value("action"),
            body,
            false,
            "this project",
            "",
            token
        );
    }

    private async Task<JsonNode?> TriageWorkspaceAsync(UiWriteRequest request, CancellationToken token)
    {
        var body = new UiWriteBody(request.Body, "key", "reason", "project");
        var workspace = Workspaces.Find(request.Route.Value("name"));
        var members = Workspaces.Resolve(workspace.Members);
        var found = (await new ArchitectureReader(members).SystemAsync(token, workspace.Name, Levels)).Findings;
        using var marks = Levels.OpenLevel(Levels.Load(), new("workspace", workspace.Name), "", token);
        var projectId = body.Text("project");
        if (projectId.Length is 0)
        {
            projectId = found.FirstOrDefault(f => f.Key == body.Text("key"))?.Project ?? "";
        }
        var member = members.FirstOrDefault(m => m.Id == projectId);
        if (request.Route.Value("action") is not "task")
        {
            return Triage(
                marks,
                marks,
                projectId,
                found,
                request.Route.Value("action"),
                body,
                true,
                "workspace " + workspace.Name,
                "",
                token
            );
        }
        if (member is null)
        {
            throw new UiRequestException(400, "choose the member the task belongs to");
        }
        using var tasks = Open(await catalog.FindAsync(member.Id, token), true);
        return Triage(
            marks,
            tasks,
            member.Id,
            found,
            "task",
            body,
            true,
            "workspace " + workspace.Name,
            member.Title.Length > 0 ? member.Title : member.Name,
            token
        );
    }

    private static JsonNode? Triage(
        HarnessDatabase marks,
        HarnessDatabase tasks,
        string projectId,
        IReadOnlyList<ArchitectureFinding> found,
        string action,
        UiWriteBody body,
        bool wider,
        string where,
        string member,
        CancellationToken token
    )
    {
        var review = new FindingReview(marks, tasks, projectId);
        var key = body.Text("key");
        var human = HumanActor.Current();
        if (action is "unmark")
        {
            review.Unmark(key, human, token);
            return new JsonObject { ["mark"] = null };
        }
        var finding =
            found.FirstOrDefault(f => f.Key == key)
            ?? throw new UiRequestException(400, "no such finding now (it may be gone): " + key);
        if (action is "mark")
        {
            return UiJson.Node(
                new JsonObjectMap
                {
                    ["mark"] = review.Mark(
                        finding,
                        body.Text("reason"),
                        human,
                        wider ? MemoryScope.Project : null,
                        token: token
                    ),
                }
            );
        }
        if (action is not "task")
        {
            throw new UiRequestException(400, "unknown action " + action + " (mark, task or unmark)");
        }
        var store = new FindingStore(marks);
        var old = store.MarkOf(key, token);
        if (old?.State is "task")
        {
            throw new UiRequestException(400, "already a task: " + old.TaskId);
        }
        var taskStore = new TaskStore(tasks);
        var id = taskStore.NextId("ARCH", token);
        taskStore.Import(
            [FindingReview.Definition(id, finding, where, member)],
            "finding:" + key,
            cancellationToken: token
        );
        if (old?.MemoryId is { Length: > 0 } memory)
        {
            try
            {
                new MemoryStore(marks).Retire(memory, human, "made a task (" + id + ") in harness ui", token);
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
            { /* Already retired memories stay retired. */
            }
        }
        return UiJson.Node(
            new JsonObjectMap
            {
                ["mark"] = store.Put(
                    finding,
                    new()
                    {
                        State = "task",
                        Reason = body.Text("reason").Trim(),
                        TaskId = id,
                        TaskProject = projectId,
                        MarkedBy = human,
                    },
                    token
                ),
            }
        );
    }

    private async Task<JsonNode?> ApproveFindingAsync(
        JsonObject project,
        HarnessDatabase database,
        UiWriteRequest request,
        CancellationToken token
    )
    {
        var fields = new UiWriteBody(request.Body, "title", "body");
        var proposal = await new FindingSets(
            UiProjectCatalog.Text(project, "root"),
            UiProjectCatalog.Text(project, "id"),
            database,
            lifecycle.GlobalConfigurationPath,
            lifecycle.Registry
        ).ApproveAsync(
            request.Route.Value("pid"),
            HumanActor.Current(),
            fields.Text("title"),
            fields.Text("body"),
            token
        );
        return UiJson.Node(new JsonObjectMap { ["proposal"] = proposal });
    }

    private async Task<JsonNode?> WriteGraphifyAsync(UiWriteRequest request, CancellationToken token)
    {
        var action = request.Route.Value("action");
        if (action is not ("start" or "restart" or "stop"))
        {
            throw new UiRequestException(400, "action must be start, restart or stop");
        }
        var fields = new UiWriteBody(request.Body, "project", "session");
        var sessionId = fields.Text("session");
        var root =
            sessionId.Length > 0
                ? GlobalDirectory
                : UiProjectCatalog.Text(await catalog.FindAsync(fields.Text("project"), token), "root");
        if (sessionId.Length > 0 && action is not "stop")
        {
            throw new UiRequestException(400, "a session by ID can only be stopped");
        }
        var configuration = ConfigurationLoader
            .LoadFiles(
                lifecycle.GlobalConfigurationPath,
                sessionId.Length > 0 ? "" : ConfigurationPaths.ProjectPath(root)
            )
            .Value;
        var (command, watch) = GraphifyDiscovery.FromConfig(configuration.Providers);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var manager = new GraphifySessionManager(
            new GraphifyOptions(root, command)
            {
                WatchArguments = watch,
                StartedBy = "harness ui (by hand)",
                RecordsPath = Path.Combine(GlobalDirectory, "graphify-sessions.json"),
            }
        );
        var listed = await manager.ListAsync(false, deadline.Token);
        if (sessionId.Length > 0)
        {
            if (!listed.Any(s => s.Id == sessionId))
            {
                throw new UiRequestException(400, "no such session");
            }
            await manager.StopAsync(sessionId, deadline.Token);
            sessions.Forget();
            return UiJson.Node(new JsonObjectMap { ["stopped"] = 1 });
        }
        var mine = listed
            .Where(s => s.Reachable && GraphifyDiscovery.PathComparer.Equals(s.Root, manager.Root))
            .ToArray();
        var changed = 0;
        if (action is "stop" or "restart")
        {
            foreach (var session in mine)
            {
                await manager.StopAsync(session.Id, deadline.Token);
                changed++;
            }
        }
        if (action is "start" or "restart" && (action is "restart" || mine.Length is 0))
        {
            changed = await manager.StartAsync(deadline.Token) is not null ? 1 : 0;
        }
        sessions.Forget();
        return UiJson.Node(new JsonObjectMap { ["changed"] = changed });
    }
}
