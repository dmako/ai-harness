using System.Globalization;
using System.Text.Json;
using Harness.Cli.Memory;
using Harness.Core.Tasks;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Spectre.Console.Cli;

namespace Harness.Cli.Tasks;

internal sealed class TaskListSettings : InspectSettings
{
    [CommandOption("--status")] public string[] Status { get; init; } = [];
    [CommandOption("--milestone")] public string Milestone { get; init; } = "";
    [CommandOption("--all")] public bool All { get; init; }
}
internal sealed class TaskNextSettings : InspectSettings
{
    [CommandOption("--milestone")] public string Milestone { get; init; } = "";
}
internal class TaskIdSettings : InspectSettings
{
    [CommandArgument(0, "<id>")] public string Id { get; init; } = "";
}
internal sealed class TaskShowSettings : TaskIdSettings
{
    [CommandOption("--events")] public bool Events { get; init; }
}
internal sealed class TaskReasonSettings : TaskIdSettings
{
    [CommandOption("--reason")] public string Reason { get; init; } = "";
}
internal sealed class TaskReadySettings : TaskIdSettings
{
    [CommandOption("--note")] public string Note { get; init; } = "";
}
internal sealed class TaskFileSettings : InspectSettings
{
    [CommandArgument(0, "[file]")] public string File { get; init; } = "";
    [CommandOption("--dry-run")] public bool DryRun { get; init; }
}
internal sealed class TaskAddSettings : InspectSettings
{
    [CommandOption("--id")] public string Id { get; init; } = "";
    [CommandOption("--prefix")] public string Prefix { get; init; } = "";
    [CommandOption("--title")] public string Title { get; init; } = "";
    [CommandOption("--goal")] public string Goal { get; init; } = "";
    [CommandOption("--type")] public string Type { get; init; } = "feature";
    [CommandOption("--priority")] public string Priority { get; init; } = "medium";
    [CommandOption("--milestone")] public string Milestone { get; init; } = "";
    [CommandOption("--depends")] public string[] Depends { get; init; } = [];
    [CommandOption("--accept")] public string[] Accept { get; init; } = [];
    [CommandOption("--scope")] public string[] Scope { get; init; } = [];
}
internal sealed class TaskListCommand(TaskCommandRunner runner) : AsyncCommand<TaskListSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskListSettings settings, CancellationToken cancellationToken) => runner.RunAsync("list", settings, cancellationToken); }
internal sealed class TaskMilestonesCommand(TaskCommandRunner runner) : AsyncCommand<InspectSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, InspectSettings settings, CancellationToken cancellationToken) => runner.RunAsync("milestones", settings, cancellationToken); }
internal sealed class TaskShowCommand(TaskCommandRunner runner) : AsyncCommand<TaskShowSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskShowSettings settings, CancellationToken cancellationToken) => runner.RunAsync("show", settings, cancellationToken); }
internal sealed class TaskNextCommand(TaskCommandRunner runner) : AsyncCommand<TaskNextSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskNextSettings settings, CancellationToken cancellationToken) => runner.RunAsync("next", settings, cancellationToken); }
internal sealed class TaskImportCommand(TaskCommandRunner runner) : AsyncCommand<TaskFileSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskFileSettings settings, CancellationToken cancellationToken) => runner.RunAsync("import", settings, cancellationToken); }
internal sealed class TaskExportCommand(TaskCommandRunner runner) : AsyncCommand<TaskFileSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskFileSettings settings, CancellationToken cancellationToken) => runner.RunAsync("export", settings, cancellationToken); }
internal sealed class TaskAddCommand(TaskCommandRunner runner) : AsyncCommand<TaskAddSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskAddSettings settings, CancellationToken cancellationToken) => runner.RunAsync("add", settings, cancellationToken); }
internal sealed class TaskBlockCommand(TaskCommandRunner runner) : AsyncCommand<TaskReasonSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskReasonSettings settings, CancellationToken cancellationToken) => runner.RunAsync("block", settings, cancellationToken); }
internal sealed class TaskDeferCommand(TaskCommandRunner runner) : AsyncCommand<TaskReasonSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskReasonSettings settings, CancellationToken cancellationToken) => runner.RunAsync("defer", settings, cancellationToken); }
internal sealed class TaskReadyCommand(TaskCommandRunner runner) : AsyncCommand<TaskReadySettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskReadySettings settings, CancellationToken cancellationToken) => runner.RunAsync("ready", settings, cancellationToken); }

internal sealed class TaskCommandRunner(ProjectLifecycle lifecycle, TextWriter output, CliErrorOutput error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public async Task<int> RunAsync(string verb, InspectSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var location = await lifecycle.LocateAsync(settings.Project, cancellationToken: cancellationToken);
            if (!System.IO.File.Exists(location.DatabasePath))
                throw new IOException("project not initialized (run 'harness init'): " + location.DatabasePath);
            using var database = HarnessDatabase.Open(location.DatabasePath, new StorageOptions { BusyTimeout = TimeSpan.FromMilliseconds(location.Configuration.Value.Storage.BusyTimeoutMs) }, cancellationToken);
            var store = new TaskStore(database);
            var actor = new TaskActor(HumanActor.Current(), Privileged: true);
            switch (verb)
            {
                case "import" or "export":
                    await FileAsync(verb, (TaskFileSettings)settings, location.Root, store, cancellationToken);
                    break;
                case "list":
                    var listSettings = (TaskListSettings)settings;
                    string[] statuses;
                    try
                    { statuses = listSettings.Status.SelectMany(s => s.Split(',')).Select(TaskWorkflow.ParseStatus).ToArray(); }
                    catch (ArgumentException exception) { throw new TaskUsageException(exception.Message); }
                    if (statuses.Length is 0 && !listSettings.All)
                        statuses = ["backlog", "ready", "claimed", "in_progress", "review", "blocked"];
                    var tasks = store.List(new(statuses, listSettings.Milestone), cancellationToken);
                    if (settings.Json)
                        await JsonAsync(new { tasks = tasks.Select(TaskViews.Task).ToArray() });
                    else if (tasks.Count is 0)
                        await output.WriteLineAsync("No tasks match.");
                    else
                    {
                        await output.WriteLineAsync($"{"ID",-10} {"STATUS",-11} {"MS",-6} {"PRIORITY",-8} TITLE");
                        foreach (var t in tasks)
                        {
                            var extra = t switch
                            {
                                { Status: "blocked" or "deferred" } => $"  [{t.Status}: {t.BlockedReason}]",
                                { Claimant.Length: > 0 } => $"  [{t.Claimant}]",
                                _ => "",
                            };
                            await output.WriteLineAsync($"{t.Id,-10} {t.Status,-11} {t.Definition.Milestone,-6} {t.Definition.Priority,-8} {t.Definition.Title}{extra}");
                        }
                    }
                    break;
                case "milestones":
                    var milestones = store.Milestones(cancellationToken);
                    if (settings.Json)
                        await JsonAsync(new { milestones = milestones.Select(TaskViews.Milestone).ToArray() });
                    else
                        for (var i = 0; i < milestones.Count; i++)
                        {
                            var m = milestones[i];
                            var d = m.Definition;
                            if (i > 0)
                                await output.WriteLineAsync();
                            await output.WriteLineAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{d.Id,-6} {(d.Title.Length is 0 ? "(not defined in the backlog)" : d.Title)}  [{m.Tasks.GetValueOrDefault("done")}/{m.Total} done]"));
                            if (d.Goal.Length is not 0)
                                await output.WriteLineAsync("       goal: " + d.Goal);
                            foreach (var e in d.Exit ?? [])
                                await output.WriteLineAsync("       exit: " + e);
                            if (d.Notes.Length is not 0)
                                await output.WriteLineAsync("       note: " + d.Notes);
                        }
                    break;
                case "next":
                    var next = store.Next(((TaskNextSettings)settings).Milestone, cancellationToken);
                    if (settings.Json)
                        await JsonAsync(new { task = next is null ? null : TaskViews.Task(next) });
                    else if (next is null)
                        await output.WriteLineAsync("No ready task.");
                    else
                    { await PrintTaskAsync(next); await output.WriteLineAsync($"\nClaim it with: harness tasks claim {next.Id}"); }
                    break;
                case "show":
                    var show = (TaskShowSettings)settings;
                    var task = store.Get(show.Id.ToUpperInvariant(), cancellationToken);
                    var events = show.Events ? store.Events(task.Id, cancellationToken) : [];
                    if (settings.Json)
                    {
                        Dictionary<string, object?> view = new() { ["task"] = TaskViews.Task(task) };
                        if (show.Events)
                            view["events"] = events.Select(TaskViews.Event).ToArray();
                        await JsonAsync(view);
                    }
                    else
                    {
                        await PrintTaskAsync(task);
                        if (show.Events)
                        {
                            await output.WriteLineAsync("\nEvents:");
                            foreach (var e in events)
                            {
                                var move = e.From.Length is not 0 || e.To.Length is not 0 ? $" {e.From} -> {e.To}" : "";
                                await output.WriteLineAsync($"  {Local(e.At)}  {e.Event,-18}{move}  by {e.Actor}  {e.Detail}");
                            }
                        }
                    }
                    break;
                case "add":
                    var add = (TaskAddSettings)settings;
                    if (string.IsNullOrWhiteSpace(add.Title))
                        throw new TaskUsageException("--title is required");
                    var created = store.Create(new()
                    {
                        Id = add.Id,
                        Title = add.Title,
                        Goal = add.Goal.Trim(),
                        Type = add.Type,
                        Priority = add.Priority,
                        Milestone = add.Milestone,
                        DependsOn = Split(add.Depends).Select(d => d.ToUpperInvariant()).ToArray(),
                        Scope = Split(add.Scope),
                        Acceptance = add.Accept.Select(a => a.Trim()).Where(a => a.Length is not 0).ToArray()
                    },
                        add.Prefix, new(actor.Name, "cli", "added with harness tasks add"), cancellationToken);
                    if (settings.Json)
                        await JsonAsync(new { task = TaskViews.Task(created) });
                    else
                    { await output.WriteLineAsync($"Added {created.Id} ({created.Status}): {created.Definition.Title}"); await output.WriteLineAsync("Write it into the backlog file with: harness tasks export"); }
                    break;
                case "block" or "defer" or "ready":
                    var aside = (TaskIdSettings)settings;
                    var reason = settings is TaskReasonSettings withReason ? withReason.Reason : "";
                    var note = settings is TaskReadySettings withNote ? withNote.Note : "";
                    if (verb is not "ready" && string.IsNullOrWhiteSpace(reason))
                        throw new TaskUsageException("--reason is required");
                    var id = aside.Id.ToUpperInvariant();
                    var changed = verb switch { "block" => store.Block(id, actor, reason, cancellationToken), "defer" => store.Defer(id, actor, reason, cancellationToken), _ => store.Unblock(id, actor, note, cancellationToken) };
                    if (settings.Json)
                        await JsonAsync(new { task = TaskViews.Task(changed) });
                    else
                        await output.WriteLineAsync(verb switch { "block" => $"Blocked {id}: {changed.BlockedReason}", "defer" => $"Deferred {id}: {changed.BlockedReason}", _ => changed.Status is "ready" ? $"{id} is ready." : $"{id} unblocked; waiting for dependencies (status {changed.Status})." });
                    break;
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or Microsoft.Data.Sqlite.SqliteException or TaskUsageException)
        {
            await error.Writer.WriteLineAsync($"harness tasks {verb}: {exception.Message}");
            return exception is TaskUsageException ? 2 : 1;
        }
    }

    private async Task FileAsync(string verb, TaskFileSettings settings, string root, TaskStore store, CancellationToken cancellationToken)
    {
        var file = settings.File.Length is 0 ? Path.Combine(root, "docs", "backlog.yaml") : settings.File;
        if (verb is "export")
        {
            var result = store.Export(file, settings.DryRun, cancellationToken);
            if (settings.Json)
                await JsonAsync(new { dry_run = settings.DryRun, export = new { path = result.Path, added = result.Added, created = result.Created } });
            else if (result.Added.Count is 0 && !result.Created)
                await output.WriteLineAsync($"{file} already holds every task.");
            else
                await output.WriteLineAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(settings.DryRun ? "Would write" : "Wrote")} {result.Added.Count} task(s) into {file}: {string.Join(", ", result.Added)}"));
            return;
        }
        var backlog = BacklogCodec.Read(file);
        var relative = Path.GetRelativePath(root, Path.GetFullPath(file));
        var source = relative.StartsWith("..", StringComparison.Ordinal) ? file : relative;
        var imported = store.Import(backlog.Tasks, source, settings.DryRun, cancellationToken);
        var milestones = store.ImportMilestones(backlog.Milestones, source, settings.DryRun, cancellationToken);
        if (settings.Json)
            await JsonAsync(new { dry_run = settings.DryRun, added = Nil(imported.Added), updated = Nil(imported.Updated), unchanged = imported.Unchanged.Count, orphaned = Nil(imported.Orphaned), unlocked = Nil(imported.Unlocked), milestones_changed = milestones });
        else
        {
            await output.WriteLineAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(settings.DryRun ? "Would import" : "Imported")} {source}: {imported.Added.Count} added, {imported.Updated.Count} updated, {imported.Unchanged.Count} unchanged."));
            if (milestones > 0)
                await output.WriteLineAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Milestones added or changed: {milestones}"));
            if (imported.Updated.Count > 0)
                await output.WriteLineAsync("Updated: " + string.Join(", ", imported.Updated));
            if (imported.Unlocked.Count > 0)
                await output.WriteLineAsync("Ready: " + string.Join(", ", imported.Unlocked));
            if (imported.Orphaned.Count > 0)
                await output.WriteLineAsync("In the database but not in the file (kept): " + string.Join(", ", imported.Orphaned));
        }
    }
    private Task JsonAsync(object value) => output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));
    private static IReadOnlyList<string>? Nil(IReadOnlyList<string> values) => values.Count is 0 ? null : values;
    private static string[] Split(string[] values) => values.SelectMany(v => v.Split(',')).Select(v => v.Trim()).Where(v => v.Length is not 0).ToArray();
    private static string Local(Harness.Core.Memory.MemoryTimestamp time) => DateTimeOffset.Parse(time.ToRfc3339(), CultureInfo.InvariantCulture).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private async Task PrintTaskAsync(TaskRecord t)
    {
        var d = t.Definition;
        await output.WriteLineAsync($"{t.Id}  {d.Title}");
        await output.WriteLineAsync($"  status:     {t.Status}{(t.BlockedReason.Length is 0 ? "" : " (" + t.BlockedReason + ")")}");
        await output.WriteLineAsync($"  milestone:  {d.Milestone}   priority: {d.Priority}   type: {d.Type}");
        if (d.Goal.Length is not 0)
            await output.WriteLineAsync("  goal:       " + d.Goal);
        if (d.Scope.Count is not 0)
            await output.WriteLineAsync("  scope:      " + string.Join(", ", d.Scope));
        if (t.Dependencies.Count is not 0)
            await output.WriteLineAsync("  depends on: " + string.Join(", ", t.Dependencies.Select(dep => $"{dep.Id} ({dep.Status})")));
        if (t.Claimant.Length is not 0)
            await output.WriteLineAsync($"  claimed by: {t.Claimant}{(t.LeaseUntil is { } until ? ", lease until " + Local(until) : "")}");
        if (t.Criteria.Count is not 0)
        {
            await output.WriteLineAsync("  acceptance:");
            foreach (var a in t.Criteria)
                await output.WriteLineAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"    {a.Index,2}. [{a.Result,-7}] {a.Criterion}{(a.Evidence.Length is 0 ? "" : " — " + a.Evidence)}"));
        }
        if (d.Validation.Count is not 0)
            await output.WriteLineAsync("  validation: " + string.Join("; ", d.Validation));
        if (d.Notes.Length is not 0)
            await output.WriteLineAsync("  notes:\n    " + d.Notes.Replace("\n", "\n    ", StringComparison.Ordinal));
        if (t.CompletedAt is { } done)
        {
            await output.WriteLineAsync($"  completed:  {Local(done)} by {t.CompletedBy}{(t.CompletionCommit.Length is 0 ? "" : " (commit " + t.CompletionCommit + ")")}");
            await output.WriteLineAsync("  summary:    " + t.CompletionSummary);
        }
    }
}

public sealed class TaskUsageException : Exception
{
    public TaskUsageException() { }
    public TaskUsageException(string message) : base(message) { }
    public TaskUsageException(string message, Exception innerException) : base(message, innerException) { }
}
