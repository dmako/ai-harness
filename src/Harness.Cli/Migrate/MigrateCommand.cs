using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Harness.Cli.Hooks;
using Harness.Cli.Memory;
using Harness.Cli.Setup;
using Harness.Infrastructure.Clients;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Hooks;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;

namespace Harness.Cli.Migrate;

internal sealed class MigrateSettings
{
    public bool DryRun { get; init; }
    public bool Json { get; init; }
    public bool Full { get; init; }

    /// <summary>Projects handled at once; 0 picks the default.</summary>
    public int Jobs { get; init; }
}

/// <summary>
/// One thing migrate looked at: its kind (config, database, knowledge, guidance, hook, mcp, sync, project), where it
/// is, and what became of it: up_to_date, created, updated, migrated, refreshed, synced (would_... in a dry run),
/// skipped or failed.
/// </summary>
internal sealed record MigrateItem(string Kind, string Target, string State, string Detail = "");

/// <summary>What migrate did for the machine or for one registered project.</summary>
internal sealed class MigrateGroup(string id, string root)
{
    public string Id => id;
    public string Root => root;
    public List<MigrateItem> Items { get; } = [];
    public int SchemaFrom { get; set; }
    public int SchemaTo { get; set; }
    public string Status =>
        (Items.Any(item => item.State is "failed"), Items.Any(item => item.State is "skipped")) switch
        {
            (true, _) => "failed",
            (_, true) => "skipped",
            _ => "ok",
        };

    public void Add(string kind, string target, string state, string detail = "") =>
        Items.Add(new(kind, target, state, detail));
}

/// <summary>
/// harness migrate: after an update, brings the machine's state (global config, knowledge databases, the MCP entries
/// and hooks harness installed for the user) and then every registered project (init's config and database
/// migration, its installed guidance, hooks and MCP entries) up to date, and syncs every worktree that still exists.
/// Projects run in parallel; only what is already installed is refreshed, nothing is added.
/// </summary>
internal sealed class MigrateCommand(
    ProjectLifecycle lifecycle,
    TextWriter output,
    CliErrorOutput error,
    Telemetry.ExportCommandRunner export
)
{
    // client config files (~/.claude.json holds every project's local entries) are edited one at a time
    private static readonly SemaphoreSlim Edits = new(1, 1);
    private static readonly SemaphoreSlim Printing = new(1, 1);
    private readonly ClientPaths _paths = ClientPaths.Current();
    private int _running;
    private int _peak;

    private ClientConfiguration Clients => new(_paths);

    internal static int DefaultJobs => Math.Clamp(Environment.ProcessorCount, 1, 4);

    public async Task<int> ExecuteAsync(MigrateSettings settings, CancellationToken token)
    {
        var jobs = settings.Jobs is 0 ? DefaultJobs : settings.Jobs;
        if (jobs < 1)
        {
            await error.Writer.WriteLineAsync("harness migrate: --jobs must be at least 1");
            return 2;
        }

        var machine = await MachineAsync(settings.DryRun, token);
        if (!settings.Json)
        {
            await PrintAsync(machine, "Machine", token);
        }

        var entries = Registered();
        var projects = new MigrateGroup[entries.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, entries.Count),
            new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = token },
            async (index, cancellation) =>
            {
                var running = Interlocked.Increment(ref _running);
                InterlockedMax(ref _peak, running);
                try
                {
                    projects[index] = await ProjectAsync(entries[index], settings, cancellation);
                }
                finally
                {
                    Interlocked.Decrement(ref _running);
                }

                if (!settings.Json)
                {
                    await PrintAsync(projects[index], "Project " + projects[index].Id, cancellation);
                }
            }
        );

        var ok = machine.Status is "ok" && projects.All(project => project.Status is "ok");
        if (settings.Json)
        {
            await output.WriteLineAsync(Json(machine, projects, settings.DryRun, jobs).ToJsonString(JsonOptions));
        }
        else
        {
            await SummaryAsync(machine, projects, settings.DryRun);
        }

        if (!settings.DryRun)
        {
            export.SendLater();
        }

        return ok ? 0 : 1;
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    /// <summary>The registry's projects: id, root and the worktrees recorded for it, root first.</summary>
    private List<(string Id, string Root, string[] Worktrees)> Registered()
    {
        List<(string, string, string[])> entries = [];
        foreach (var entry in lifecycle.Registry.Load()["projects"]!.AsArray().OfType<JsonObject>())
        {
            var root = entry["root"]?.GetValue<string>() ?? "";
            var trees = (entry["worktrees"] as JsonArray)?.Select(tree => tree?.GetValue<string>() ?? "") ?? [];
            entries.Add(
                (
                    entry["id"]?.GetValue<string>() ?? "",
                    root,
                    [
                        .. new[] { root }
                            .Concat(trees)
                            .Where(tree => tree.Length > 0)
                            .Distinct(GitRepository.PathComparer),
                    ]
                )
            );
        }

        return entries;
    }

    // ---- the machine ----

    private async Task<MigrateGroup> MachineAsync(bool dryRun, CancellationToken token)
    {
        var machine = new MigrateGroup(
            "machine",
            Path.GetDirectoryName(Path.GetFullPath(lifecycle.GlobalConfigurationPath))!
        );
        try
        {
            var (action, failed) = await lifecycle.UpdateGlobalConfigurationAsync(dryRun, token);
            machine.Add("config", action.Path, failed ? "failed" : Done(action.Verb, dryRun), action.Note);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            machine.Add("config", lifecycle.GlobalConfigurationPath, "failed", Message(exception));
        }

        var knowledge = Path.Combine(machine.Root, "knowledge");
        if (Directory.Exists(knowledge))
        {
            foreach (var path in Directory.GetFiles(knowledge, "*.db").Order(StringComparer.Ordinal))
            {
                Database(machine, "knowledge", path, dryRun);
            }
        }

        await Edits.WaitAsync(token);
        try
        {
            foreach (var client in Clients.All)
            {
                foreach (var entry in Clients.Detect(client, "").Entries.Where(entry => entry.Scope is "user"))
                {
                    Entry(machine, client, entry, "", dryRun);
                }
            }

            foreach (var client in new[] { "claude-code", "codex" })
            {
                foreach (var target in ClientHookCleanup.Targets(_paths, client, "user", ""))
                {
                    ClientHook(machine, target, dryRun);
                }
            }
        }
        finally
        {
            Edits.Release();
        }

        return machine;
    }

    /// <summary>A database brought to the current schema (with the usual backup), or what that would do.</summary>
    private static void Database(MigrateGroup group, string kind, string path, bool dryRun)
    {
        try
        {
            var (applied, latest, problem) = Schema(path);
            if (problem.Length > 0)
            {
                group.Add(kind, path, "failed", problem);
                return;
            }

            if (applied >= latest)
            {
                group.Add(kind, path, "up_to_date", "schema " + latest);
                return;
            }

            if (!dryRun)
            {
                HarnessDatabase.Open(path).Dispose();
            }

            group.Add(kind, path, dryRun ? "would_migrate" : "migrated", $"schema {applied} to {latest}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            group.Add(kind, path, "failed", Message(exception));
        }
    }

    /// <summary>The schema a database has and the one this harness writes; a problem when it cannot be used.</summary>
    private static (int Applied, int Latest, string Problem) Schema(string path)
    {
        if (!File.Exists(path))
        {
            return (0, HarnessDatabase.LatestSchemaVersion, "");
        }

        using var database = HarnessDatabase.OpenReadOnly(path);
        var state = database.InspectMigrationState();
        return (state.Applied, state.Latest, state.Error is null ? "" : state.Error.Message);
    }

    // ---- a project ----

    private async Task<MigrateGroup> ProjectAsync(
        (string Id, string Root, string[] Worktrees) entry,
        MigrateSettings settings,
        CancellationToken token
    )
    {
        var group = new MigrateGroup(entry.Id, entry.Root);
        if (!Directory.Exists(entry.Root))
        {
            group.Add(
                "project",
                entry.Root,
                "skipped",
                "folder missing: restore it, or 'harness projects forget " + entry.Id + "'"
            );
            return group;
        }

        try
        {
            await InitAsync(group, entry.Root, settings.DryRun, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            group.Add("project", entry.Root, "failed", Message(exception));
            return group;
        }

        await RefreshAsync(group, entry.Root, settings.DryRun, token);
        if (group.Status is "failed")
        {
            return group;
        }

        // one worktree at a time: they share the project's database
        foreach (var tree in entry.Worktrees)
        {
            await SyncAsync(group, entry.Id, tree, settings, token);
        }

        return group;
    }

    /// <summary>What 'harness init' would change; init runs only when something is out of date.</summary>
    private async Task InitAsync(MigrateGroup group, string root, bool dryRun, CancellationToken token)
    {
        var plan = await lifecycle.InitAsync(root, dryRun: true, token);
        var database = plan.Location.DatabasePath;
        var (applied, latest, problem) = Schema(database);
        if (problem.Length > 0)
        {
            throw new InvalidOperationException(database + ": " + problem);
        }

        group.SchemaFrom = applied;
        group.SchemaTo = latest;
        var global = Path.GetFullPath(lifecycle.GlobalConfigurationPath);
        // the global config is the machine's; the database is reported on its own below
        var changes = plan
            .Actions.Where(action =>
                action.Verb is "create" or "update"
                && !GitRepository.PathEquals(Path.GetFullPath(action.Path), global)
                && !GitRepository.PathEquals(Path.GetFullPath(action.Path), Path.GetFullPath(database))
            )
            .ToArray();
        foreach (var action in changes)
        {
            group.Add("config", action.Path, Done(action.Verb, dryRun), action.Note);
        }

        foreach (
            var action in plan.Actions.Where(action =>
                action.Verb is "exists" && action.Note.Contains("not updated", StringComparison.Ordinal)
            )
        )
        {
            group.Add("config", action.Path, "failed", action.Note);
        }

        var pending = !plan.Location.Initialized || applied < latest;
        group.Add(
            "database",
            database,
            (pending, dryRun, plan.Location.Initialized) switch
            {
                (false, _, _) => "up_to_date",
                (true, true, false) => "would_create",
                (true, true, true) => "would_migrate",
                (true, false, false) => "created",
                _ => "migrated",
            },
            pending && plan.Location.Initialized ? $"schema {applied} to {latest}" : "schema " + latest
        );
        if (!dryRun && (changes.Length > 0 || pending))
        {
            await lifecycle.InitAsync(root, dryRun: false, token);
        }
    }

    /// <summary>The guidance blocks, Git and client hooks and MCP entries the project already has, restated.</summary>
    private async Task RefreshAsync(MigrateGroup group, string root, bool dryRun, CancellationToken token)
    {
        await Edits.WaitAsync(token);
        try
        {
            Each(group, "guidance", root, dryRun, () => ProjectInstructions.Refresh(root, dryRun));
            try
            {
                var repository = await GitRepository.DiscoverAsync(root, token);
                var hooks = await GitHooks.ForAsync(repository, token);
                Each(group, "hook", hooks.DirectoryPath, dryRun, () => hooks.Refresh(dryRun));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                group.Add("hook", root, "failed", Message(exception));
            }

            foreach (var client in new[] { "claude-code", "codex" })
            {
                foreach (
                    var target in ClientHookCleanup
                        .Targets(_paths, client, "all", root)
                        .Where(target => target.Scope is not "user")
                )
                {
                    ClientHook(group, target, dryRun);
                }
            }

            foreach (var client in Clients.All)
            {
                var status = Clients.Detect(client, root);
                foreach (var entry in status.Entries.Where(entry => entry.Scope is not "user"))
                {
                    Entry(group, client, entry, root, dryRun);
                }
            }
        }
        finally
        {
            Edits.Release();
        }
    }

    private async Task SyncAsync(
        MigrateGroup group,
        string id,
        string tree,
        MigrateSettings settings,
        CancellationToken token
    )
    {
        if (!Directory.Exists(tree))
        {
            group.Add(
                "sync",
                tree,
                "skipped",
                "worktree folder missing: restore it, or 'harness projects forget " + id + "'"
            );
            return;
        }

        if (settings.DryRun)
        {
            group.Add("sync", tree, "would_sync", settings.Full ? "full" : "incremental");
            return;
        }

        try
        {
            var result = await SyncCommand.RunAsync(lifecycle, tree, settings.Full ? "full" : "incremental", "", token);
            group.Add("sync", tree, "synced", Synced(result));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            group.Add("sync", tree, "failed", Message(exception));
        }
    }

    private static string Synced(Infrastructure.Sync.ProjectSyncResult result)
    {
        if (result.CoveredBy is not 0)
        {
            return "already synced to this HEAD by a concurrent run";
        }

        if (result.Files is not { } run)
        {
            return result.Registration.Worktree.Branch;
        }

        var files = run.Files;
        return $"{run.Mode}: {files.Added.Count} added, {files.Changed.Count} changed, {files.Removed.Count} removed, {files.Unchanged} unchanged";
    }

    // ---- refreshing what is installed ----

    /// <summary>A harness MCP entry restated: its own command while that still exists, else this harness.</summary>
    private void Entry(MigrateGroup group, IAgentClient client, ClientEntry entry, string root, bool dryRun)
    {
        var label = client.Id + " (" + entry.Scope + ") " + entry.ConfigPath;
        try
        {
            var target = client
                .Targets(entry.Scope, root)
                .First(target => string.Equals(target.Path, entry.ConfigPath, StringComparison.Ordinal));
            var spec = entry.Problem.Length is 0 ? new ServerSpec(entry.Command, entry.Args) : Current(entry.Scope);
            Change(group, "mcp", label, Clients.Install(target, spec, dryRun), dryRun);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            group.Add("mcp", label, "failed", Message(exception));
        }
    }

    /// <summary>This harness as an MCP server, written home-relative in a project's shared file.</summary>
    private ServerSpec Current(string scope)
    {
        var spec = SetupCommandRunner.Executable("");
        var prefix = Path.TrimEndingDirectorySeparator(_paths.Home) + Path.DirectorySeparatorChar;
        return scope is "project" && spec.Command.StartsWith(prefix, StringComparison.Ordinal)
            ? spec with
            {
                Command = "${HOME}/" + spec.Command[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/'),
            }
            : spec;
    }

    /// <summary>A harness client hook restated: its own executable while that still exists, else this harness.</summary>
    private void ClientHook(MigrateGroup group, ClientTarget target, bool dryRun)
    {
        var label = target.Client + " (" + target.Scope + ") " + target.Path;
        try
        {
            var installed = ClientHookCleanup.Installed(target.Path);
            if (installed.Length is 0)
            {
                return;
            }

            var (executable, _) = ClientHookCleanup.SplitExecutable(installed);
            var expanded = Clients.Expand(executable);
            var command =
                !Path.IsPathRooted(expanded) || File.Exists(expanded)
                    ? ClientHookCleanup.Command(executable, target.Client)
                    : HookCommandRunner.HookCommand("", target.Client);
            Change(group, "hook", label, ClientHookCleanup.Install(target, command, dryRun), dryRun);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            group.Add("hook", label, "failed", Message(exception));
        }
    }

    private static void Each(
        MigrateGroup group,
        string kind,
        string where,
        bool dryRun,
        Func<IReadOnlyList<FileChange>> changes
    )
    {
        try
        {
            foreach (var change in changes())
            {
                Change(group, kind, change.Path, change, dryRun);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            group.Add(kind, where, "failed", Message(exception));
        }
    }

    private static void Change(MigrateGroup group, string kind, string label, FileChange change, bool dryRun)
    {
        switch (change.Action)
        {
            case "unchanged":
                group.Add(kind, label, "up_to_date");
                break;
            case "add" or "update":
                group.Add(kind, label, dryRun ? "would_refresh" : "refreshed");
                break;
            case "absent":
                break;
            default:
                group.Add(kind, label, "failed", change.Reason.Length > 0 ? change.Reason : change.Action);
                break;
        }
    }

    private static string Done(string verb, bool dryRun) =>
        (verb, dryRun) switch
        {
            ("create", true) => "would_create",
            ("update", true) => "would_update",
            ("create", false) => "created",
            ("update", false) => "updated",
            _ => "up_to_date",
        };

    private static string Message(Exception exception) => exception.Message.Split('\n')[0].Trim();

    // ---- output ----

    private async Task PrintAsync(MigrateGroup group, string heading, CancellationToken token)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{heading}  {group.Root}\n");
        foreach (var item in group.Items)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"  {item.Kind, -9} {item.State.Replace('_', ' '), -13} {item.Target}{(item.Detail.Length > 0 ? " — " + item.Detail : "")}\n"
            );
        }

        // a project's lines are printed together as it finishes, never mixed with another's
        await Printing.WaitAsync(token);
        try
        {
            await output.WriteAsync(text.ToString());
        }
        finally
        {
            Printing.Release();
        }
    }

    private async Task SummaryAsync(MigrateGroup machine, MigrateGroup[] projects, bool dryRun)
    {
        var failed = projects.Where(project => project.Status is "failed").ToArray();
        var skipped = projects.Where(project => project.Status is "skipped").ToArray();
        await output.WriteLineAsync(
            $"\n{projects.Length} project(s): {projects.Length - failed.Length - skipped.Length} ok, {skipped.Length} skipped, {failed.Length} failed"
                + (machine.Status is "ok" ? "" : "; the machine phase " + machine.Status)
        );
        foreach (var group in new[] { machine }.Concat(projects))
        {
            foreach (var item in group.Items.Where(item => item.State is "failed" or "skipped"))
            {
                await output.WriteLineAsync($"  {item.State} {group.Id}: {item.Target} — {item.Detail}");
            }
        }

        if (dryRun)
        {
            await output.WriteLineAsync("\nDry run: nothing was changed.");
        }
    }

    private JsonObject Json(MigrateGroup machine, MigrateGroup[] projects, bool dryRun, int jobs) =>
        new()
        {
            ["dry_run"] = dryRun,
            ["ok"] = machine.Status is "ok" && projects.All(project => project.Status is "ok"),
            ["jobs"] = jobs,
            ["peak_jobs"] = Volatile.Read(ref _peak),
            ["machine"] = new JsonObject
            {
                ["status"] = machine.Status,
                ["directory"] = machine.Root,
                ["items"] = Items(machine),
            },
            ["projects"] = new JsonArray([
                .. projects.Select(project =>
                    (JsonNode)
                        new JsonObject
                        {
                            ["id"] = project.Id,
                            ["root"] = project.Root,
                            ["status"] = project.Status,
                            ["schema_from"] = project.SchemaFrom,
                            ["schema_to"] = project.SchemaTo,
                            ["items"] = Items(project),
                        }
                ),
            ]),
        };

    private static JsonArray Items(MigrateGroup group) =>
        new([
            .. group.Items.Select(item =>
                (JsonNode)
                    new JsonObject
                    {
                        ["kind"] = item.Kind,
                        ["target"] = item.Target,
                        ["state"] = item.State,
                        ["detail"] = item.Detail,
                    }
            ),
        ]);
}
