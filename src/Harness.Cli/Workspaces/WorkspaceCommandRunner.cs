using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Cli.Memory;
using Harness.Core.Serialization;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Workspaces;

namespace Harness.Cli.Workspaces;

internal sealed class WorkspaceCommandRunner(ProjectLifecycle lifecycle, TextWriter output, CliErrorOutput error)
{
    private WorkspaceStore Store => new(lifecycle.GlobalConfigurationPath, lifecycle.Registry);

    public async Task<int> RunAsync(string area, string verb, InspectSettings settings, CancellationToken token)
    {
        try
        {
            return await (
                (area, verb) switch
                {
                    ("workspace", "list") => ListAsync(settings),
                    ("workspace", "create") => CreateAsync((WorkspaceCreateSettings)settings, token),
                    ("workspace", "discover") => DiscoverAsync((WorkspaceDiscoverSettings)settings),
                    ("workspace", "show") => ShowAsync((WorkspaceSettings)settings),
                    ("workspace", "join" or "leave") => JoinAsync((WorkspaceSettings)settings, verb is "join", token),
                    ("workspace", "delete") => DeleteAsync((WorkspaceSettings)settings, token),
                    ("workspace", "rename") => RenameAsync((WorkspaceRenameSettings)settings, token),
                    ("projects", "forget") => ForgetAsync((ProjectForgetSettings)settings, token),
                    ("projects", "rename") => RenameProjectAsync((RenameSettings)settings, token),
                    _ => ProjectsAsync(settings, token),
                }
            ).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or InvalidDataException
                        or InvalidOperationException
                        or UnauthorizedAccessException
                        or JsonException
                        or ArgumentException
                        or Microsoft.Data.Sqlite.SqliteException
            )
        {
            await error.Writer.WriteLineAsync($"harness {area} {verb}: {exception.Message}");
            return 1;
        }
    }

    private async Task<int> ListAsync(InspectSettings settings)
    {
        var workspaces = Store.Load();
        var projects = lifecycle.Registry.Load()["projects"]!.AsArray().OfType<JsonObject>().ToArray();
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["workspaces"] = workspaces.Select(Wire) });
            return 0;
        }
        if (workspaces.Count is 0)
        {
            await output.WriteLineAsync(
                "No workspaces yet: 'harness workspace create <name> --discover <folder>' groups the projects under a folder."
            );
        }

        foreach (var workspace in workspaces)
        {
            var names = workspace.Members.Select(id =>
                projects.FirstOrDefault(project => project["id"]?.GetValue<string>() == id) is { } row
                    ? WorkspaceStore.ShortName(row)
                    : id
            );
            await output.WriteLineAsync(
                $"{workspace.Name, -20} {workspace.Members.Count} project(s): {string.Join(", ", names)}{(workspace.Title.Length > 0 ? " (" + workspace.Title + ")" : "")}"
            );
        }
        return 0;
    }

    private async Task<int> CreateAsync(WorkspaceCreateSettings settings, CancellationToken token)
    {
        var workspace = await Store.CreateAsync(settings.Name, token);
        if (settings.Title.Length > 0)
        {
            workspace = await Store.RenameAsync(workspace.Name, "", settings.Title, token);
        }

        await output.WriteLineAsync("Created workspace " + workspace.Name + ".");
        if (settings.Discover.Length is 0)
        {
            await output.WriteLineAsync(
                "Add projects: harness workspace join " + workspace.Name + " --project <repository>"
            );
            return 0;
        }
        var projects = Store.Discover(settings.Discover);
        foreach (var project in projects)
        {
            await output.WriteLineAsync(
                "  + " + WorkspaceStore.ShortName(project) + "  " + project["root"]!.GetValue<string>()
            );
        }

        if (projects.Count is 0)
        {
            await output.WriteLineAsync(
                "No registered projects under "
                    + Path.GetFullPath(settings.Discover)
                    + " ('harness init' in a repository registers it)."
            );
            return 0;
        }
        await Store.JoinAsync(
            workspace.Name,
            projects.Select(project => project["id"]!.GetValue<string>()).ToArray(),
            token
        );
        await output.WriteLineAsync(
            $"Joined {projects.Count} project(s); 'harness workspace leave {workspace.Name} --project <repository>' removes one."
        );
        return 0;
    }

    private async Task<int> DiscoverAsync(WorkspaceDiscoverSettings settings)
    {
        var projects = Store.Discover(settings.Directory);
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["projects"] = projects });
        }
        else
        {
            foreach (var project in projects)
            {
                await output.WriteLineAsync(
                    WorkspaceStore.ShortName(project) + "  " + project["root"]!.GetValue<string>()
                );
            }
        }
        return 0;
    }

    private async Task<int> ShowAsync(WorkspaceSettings settings)
    {
        var workspace = Store.Find(settings.Name);
        if (settings.Json)
        {
            await JsonAsync(Wire(workspace));
            return 0;
        }
        await output.WriteLineAsync($"{workspace.Name}  ({workspace.Id}, {workspace.Members.Count} project(s))");
        if (workspace.Title.Length > 0)
        {
            await output.WriteLineAsync("  shown as " + HarnessJson.Serialize(workspace.Title));
        }

        var projects = lifecycle.Registry.Load()["projects"]!.AsArray().OfType<JsonObject>().ToArray();
        foreach (var id in workspace.Members)
        {
            var row = projects.FirstOrDefault(row => row["id"]?.GetValue<string>() == id);
            var name = row is null ? id : WorkspaceStore.ShortName(row);
            var root = row?["root"]?.GetValue<string>() ?? "(no longer registered on this machine)";
            await output.WriteLineAsync($"  {name, -24} {root}");
        }
        return 0;
    }

    private async Task<JsonObject> RegisteredAsync(string directory, CancellationToken token)
    {
        var location = await lifecycle.LocateAsync(directory, cancellationToken: token);
        return lifecycle.Registry.Load()["projects"]!
                .AsArray()
                .OfType<JsonObject>()
                .FirstOrDefault(project => project["root"]?.GetValue<string>() == location.StateRoot)
            ?? throw new InvalidOperationException(
                location.StateRoot + " is not a harness project yet: run 'harness init' there"
            );
    }

    private async Task<int> JoinAsync(WorkspaceSettings settings, bool join, CancellationToken token)
    {
        var project = await RegisteredAsync(settings.Project, token);
        var id = project["id"]!.GetValue<string>();
        var name = WorkspaceStore.ShortName(project);
        if (join)
        {
            var added = await Store.JoinAsync(settings.Name, [id], token);
            await output.WriteLineAsync(
                name + (added.Count is 0 ? " is already in " : " joined ") + settings.Name + "."
            );
        }
        else
        {
            var removed = await Store.LeaveAsync(settings.Name, id, token);
            await output.WriteLineAsync(
                name
                    + (
                        removed
                            ? " left " + settings.Name + " (its state is untouched)."
                            : " is not in " + settings.Name + "."
                    )
            );
        }
        return 0;
    }

    private async Task<int> DeleteAsync(WorkspaceSettings settings, CancellationToken token)
    {
        await Store.DeleteAsync(settings.Name, token);
        await output.WriteLineAsync(
            "Deleted workspace " + settings.Name + " (its projects and their state are untouched)."
        );
        return 0;
    }

    private async Task<int> RenameAsync(WorkspaceRenameSettings settings, CancellationToken token)
    {
        string? title = settings.ClearTitle ? "" : null;
        if (!settings.ClearTitle && settings.Title.Length > 0)
        {
            title = settings.Title;
        }

        if (settings.To.Length is 0 && title is null)
        {
            await error.Writer.WriteLineAsync(
                "harness workspace rename: give --to <new name>, --title <display name> or --clear-title"
            );
            return 2;
        }
        var workspace = await Store.RenameAsync(settings.Name, settings.To, title, token);
        await output.WriteLineAsync(
            "Workspace "
                + workspace.Name
                + (workspace.Title.Length > 0 ? ", shown as " + HarnessJson.Serialize(workspace.Title) : "")
                + "."
        );
        if (workspace.Name != settings.Name)
        {
            await output.WriteLineAsync(
                "Agents and commands now use workspace:" + workspace.Name + "; its members and knowledge are unchanged."
            );
        }

        return 0;
    }

    private async Task<int> ForgetAsync(ProjectForgetSettings settings, CancellationToken token)
    {
        if (!await lifecycle.Registry.ForgetAsync(settings.Id, token))
        {
            throw new InvalidOperationException(settings.Id + " is not registered ('harness projects' lists them)");
        }

        await output.WriteLineAsync(
            "Forgot "
                + settings.Id
                + " (its repository and state are untouched; init, sync or an agent there registers it again)."
        );
        return 0;
    }

    private async Task<int> RenameProjectAsync(RenameSettings settings, CancellationToken token)
    {
        string? name = settings.ResetName ? "" : null;
        if (settings.To.Length > 0 && !settings.ResetName)
        {
            name = settings.To;
        }

        string? title = settings.ClearTitle ? "" : null;
        if (settings.Title.Length > 0 && !settings.ClearTitle)
        {
            title = settings.Title;
        }

        if (name is null && title is null)
        {
            await error.Writer.WriteLineAsync(
                "harness projects rename: give --to, --reset-name, --title or --clear-title"
            );
            return 2;
        }
        var project = await RegisteredAsync(settings.Project, token);
        var row = await lifecycle.Registry.RenameAsync(project["id"]!.GetValue<string>(), name, title, token);
        await output.WriteLineAsync(
            row["root"]!.GetValue<string>()
                + ": short name "
                + WorkspaceStore.ShortName(row)
                + (
                    row["title"]?.GetValue<string>() is { Length: > 0 } value
                        ? ", shown as " + HarnessJson.Serialize(value)
                        : ""
                )
                + "."
        );
        return 0;
    }

    private async Task<int> ProjectsAsync(InspectSettings settings, CancellationToken token)
    {
        var projects = lifecycle.Registry.Load()["projects"]!
            .AsArray()
            .OfType<JsonObject>()
            .Select(project => (JsonObject)project.DeepClone())
            .ToArray();
        var workspaces = Store.Load();
        foreach (var project in projects)
        {
            var live = await ProjectOverview.InspectAsync(lifecycle, project, token);
            project["live"] = live;
            var membership = workspaces
                .Where(workspace =>
                    workspace.Members.Contains(project["id"]!.GetValue<string>(), StringComparer.Ordinal)
                )
                .Select(workspace => JsonValue.Create(workspace.Name))
                .ToArray<JsonNode?>();
            if (membership.Length > 0)
            {
                project["workspaces"] = new JsonArray(membership);
            }

            if (!settings.Json)
            {
                if (project != projects[0])
                {
                    await output.WriteLineAsync();
                }

                await PrintProjectAsync(project, live);
            }
        }
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["projects"] = projects });
        }
        else if (projects.Length is 0)
        {
            await output.WriteLineAsync("No projects registered yet: 'harness init' in a repository registers it.");
        }

        return 0;
    }

    private async Task PrintProjectAsync(JsonObject project, JsonObject live)
    {
        string Value(JsonObject row, string key) => row[key]?.GetValue<string>() ?? "";
        var state = Value(live, "state").Replace('_', ' ');
        if (Value(live, "detail") is { Length: > 0 } detail)
        {
            state += ": " + detail;
        }

        if (Value(live, "state") is "ok")
        {
            state += string.Create(CultureInfo.InvariantCulture, $" (schema {live["schema"]}/{live["schema_latest"]})");
        }

        await output.WriteLineAsync(Value(project, "id") + "  " + Value(project, "root"));
        await Row("state", state);
        var commit = Value(live, "commit");
        if (commit.Length > 0)
        {
            var branch = Value(live, "branch");
            var git = commit[..Math.Min(commit.Length, 12)] + " on " + (branch.Length is 0 ? "detached HEAD" : branch);
            if (Value(live, "synced_at") is { Length: > 0 } synced)
            {
                git += ", synced " + Ago(synced);
            }

            if (Value(live, "stale") is { Length: > 0 } stale)
            {
                git += " — " + stale;
            }

            await Row("git", git);
        }
        if (Value(live, "state") is "ok")
        {
            var answers = live["context_answers_30d"]!.GetValue<int>();
            var hitRate = live["context_hit_rate_30d"]!.GetValue<double>();
            await Row(
                "context",
                answers > 0
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"{hitRate * 100:0}% hit rate over {answers} scored answer(s), last 30 days"
                    )
                    : "no scored get_context answers in the last 30 days"
            );
            if (live["pending_proposals"]!.GetValue<int>() is > 0 and var pending)
            {
                await Row(
                    "proposals",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{pending} pending (harness proposals list --project {Value(project, "root")})"
                    )
                );
            }
        }
        List<string> used = [];
        if (Value(project, "last_mcp") is { Length: > 0 } mcp)
        {
            used.Add("agent (MCP) " + Ago(mcp));
        }

        if (Value(project, "last_sync") is { Length: > 0 } sync)
        {
            used.Add("sync " + Ago(sync));
        }

        if (used.Count > 0)
        {
            await Row("used", string.Join(", ", used));
        }

        foreach (var key in new[] { "clients", "workspaces", "worktrees" })
        {
            if (project[key] is JsonArray values && values.Count > (key is "worktrees" ? 1 : 0))
            {
                await Row(key, string.Join(", ", values.Select(value => value!.GetValue<string>())));
            }
        }

        await Row("harness", Value(project, "harness_version"));
        Task Row(string name, string value) => output.WriteLineAsync($"    {name, -9} {value}");
    }

    private static string Ago(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return value;
        }

        var elapsed = DateTimeOffset.UtcNow - at;
        return elapsed.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes} min ago"),
            < 2880 => string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours} h ago"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalDays} days ago"),
        };
    }

    private Task JsonAsync(object value) =>
        output.WriteLineAsync(HarnessJson.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private static object Wire(Workspace workspace) =>
        new JsonObjectMap
        {
            ["id"] = workspace.Id,
            ["name"] = workspace.Name,
            ["title"] = workspace.Title.Length > 0 ? workspace.Title : null,
            ["members"] = workspace.Members,
            ["created"] = workspace.Created,
        };
}
