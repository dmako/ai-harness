using System.Globalization;
using System.Text.Json;
using Harness.Cli.Memory;
using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;

namespace Harness.Cli.Tasks;

internal sealed class TaskCommandRunner(ProjectLifecycle lifecycle, TextWriter output, CliErrorOutput error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<int> RunAsync(string verb, InspectSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var requestedLease = settings is TaskClaimSettings leaseSettings
                ? TaskCliInput.Lease(leaseSettings.Lease)
                : TimeSpan.Zero;
            var location = await lifecycle.LocateAsync(settings.Project, cancellationToken: cancellationToken);
            if (!System.IO.File.Exists(location.DatabasePath))
            {
                throw new IOException("project not initialized (run 'harness init'): " + location.DatabasePath);
            }

            using var database = HarnessDatabase.Open(
                location.DatabasePath,
                new StorageOptions
                {
                    BusyTimeout = TimeSpan.FromMilliseconds(location.Configuration.Value.Storage.BusyTimeoutMs),
                },
                cancellationToken
            );
            var store = new TaskStore(database);
            var actor = new TaskActor(HumanActor.Current(), Privileged: true);
            await (
                verb switch
                {
                    "claim" => ClaimAsync((TaskClaimSettings)settings, store, actor, requestedLease, cancellationToken),
                    "release" => ReleaseAsync((TaskReasonSettings)settings, store, actor, cancellationToken),
                    "complete" => CompleteAsync(
                        (TaskCompleteSettings)settings,
                        store,
                        actor,
                        database,
                        location,
                        cancellationToken
                    ),
                    "proposals" => ListProposalsAsync((TaskProposalListSettings)settings, store, cancellationToken),
                    "approve" => ApproveAsync((TaskApproveSettings)settings, store, actor, cancellationToken),
                    "reject" => RejectAsync((TaskReadySettings)settings, store, actor, cancellationToken),
                    "import" or "export" => FileAsync(
                        verb,
                        (TaskFileSettings)settings,
                        location.Root,
                        store,
                        cancellationToken
                    ),
                    "list" => ListAsync((TaskListSettings)settings, store, cancellationToken),
                    "blueprints" => BlueprintsAsync(settings, store, cancellationToken),
                    "next" => NextAsync((TaskNextSettings)settings, store, cancellationToken),
                    "show" => ShowAsync((TaskShowSettings)settings, store, cancellationToken),
                    "add" => AddAsync((TaskAddSettings)settings, store, actor, cancellationToken),
                    "block" or "defer" or "ready" => ChangeStateAsync(
                        (TaskIdSettings)settings,
                        store,
                        actor,
                        verb,
                        cancellationToken
                    ),
                    _ => Task.CompletedTask,
                }
            );
            return 0;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or InvalidDataException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or InvalidOperationException
                        or KeyNotFoundException
                        or JsonException
                        or Microsoft.Data.Sqlite.SqliteException
                        or TaskUsageException
            )
        {
            await error.Writer.WriteLineAsync($"harness tasks {verb}: {exception.Message}");
            return exception is TaskUsageException ? 2 : 1;
        }
    }

    private async Task ClaimAsync(
        TaskClaimSettings settings,
        TaskStore store,
        TaskActor actor,
        TimeSpan requestedLease,
        CancellationToken cancellationToken
    )
    {
        var claimed = store.Claim(settings.Id.ToUpperInvariant(), actor, requestedLease, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(
                new JsonObjectMap { ["task"] = TaskViews.Task(claimed.Task), ["claim_token"] = claimed.Token }
            );
        }
        else
        {
            await output.WriteLineAsync(
                $"Claimed {claimed.Task.Id} as {claimed.Task.Claimant} until {Local(claimed.Task.LeaseUntil!.Value)}."
            );
        }
    }

    private async Task ReleaseAsync(
        TaskReasonSettings settings,
        TaskStore store,
        TaskActor actor,
        CancellationToken cancellationToken
    )
    {
        var releasing = store.Get(settings.Id.ToUpperInvariant(), cancellationToken);
        var releaseReason =
            releasing.Claimant == actor.Name && settings.Reason.Length is 0 ? "released by claimant" : settings.Reason;
        var released = store.Release(releasing.Id, actor, releaseReason, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["task"] = TaskViews.Task(released) });
        }
        else
        {
            await output.WriteLineAsync($"Released {released.Id} (now {released.Status}).");
        }
    }

    private async Task CompleteAsync(
        TaskCompleteSettings settings,
        TaskStore store,
        TaskActor actor,
        HarnessDatabase database,
        ProjectLocation location,
        CancellationToken cancellationToken
    )
    {
        var current = store.Get(settings.Id.ToUpperInvariant(), cancellationToken);
        var completed = store.Complete(current.Id, actor, TaskCliInput.Complete(settings, current), cancellationToken);
        if (
            completed.Task.CompletionCommit.Length > 0
            && location.Repository is { } repository
            && ProjectStore.Worktrees(database).FirstOrDefault(t => t.GitDir == repository.GitDir) is { } worktree
        )
        {
            await Harness.Infrastructure.Telemetry.TelemetryStore.ResolveBestEffortAsync(
                database,
                repository,
                worktree.Id,
                completed.Task.CompletionCommit,
                "task",
                cancellationToken
            );
        }

        if (settings.Json)
        {
            await JsonAsync(
                new JsonObjectMap { ["task"] = TaskViews.Task(completed.Task), ["unlocked"] = Nil(completed.Unlocked) }
            );
        }
        else
        {
            await output.WriteLineAsync($"Completed {completed.Task.Id}.");
            if (completed.Unlocked.Count > 0)
            {
                await output.WriteLineAsync("Now ready: " + string.Join(", ", completed.Unlocked));
            }
        }
    }

    private async Task ListProposalsAsync(
        TaskProposalListSettings settings,
        TaskStore store,
        CancellationToken cancellationToken
    )
    {
        var proposalStatus = settings.Status;
        var proposals = store.Proposals(proposalStatus, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["proposals"] = proposals.Select(TaskViews.Proposal).ToArray() });
        }
        else if (proposals.Count is 0)
        {
            await output.WriteLineAsync($"No {proposalStatus} task proposals.");
        }
        else
        {
            foreach (var proposal in proposals)
            {
                await output.WriteLineAsync(
                    $"{proposal.Id}  [{proposal.Status}] {proposal.Task.Title}  (by {proposal.ProposedBy})"
                );
                if (proposal.Task.Goal.Length is not 0)
                {
                    await output.WriteLineAsync("    goal: " + proposal.Task.Goal);
                }

                if (proposal.Reason.Length is not 0)
                {
                    await output.WriteLineAsync("    why:  " + proposal.Reason);
                }

                for (var i = 0; i < proposal.Task.Acceptance.Count; i++)
                {
                    await output.WriteLineAsync(
                        string.Create(CultureInfo.InvariantCulture, $"    {i + 1}. {proposal.Task.Acceptance[i]}")
                    );
                }

                if (proposal.Result.Length is not 0)
                {
                    await output.WriteLineAsync("    became " + proposal.Result);
                }
            }

            if (proposalStatus is "pending")
            {
                await output.WriteLineAsync(
                    "\nharness tasks approve <id> creates the task; harness tasks reject <id> --note \"why\" closes it."
                );
            }
        }
    }

    private async Task ApproveAsync(
        TaskApproveSettings settings,
        TaskStore store,
        TaskActor actor,
        CancellationToken cancellationToken
    )
    {
        var approved = store.Approve(settings.Id, null, settings.Prefix, actor.Name, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(
                new JsonObjectMap
                {
                    ["proposal"] = TaskViews.Proposal(approved.Proposal),
                    ["task"] = TaskViews.Task(approved.Task),
                }
            );
        }
        else
        {
            await output.WriteLineAsync(
                $"Approved {approved.Proposal.Id}: {approved.Task.Id} ({approved.Task.Status}) {approved.Task.Definition.Title}"
            );
        }
    }

    private async Task RejectAsync(
        TaskReadySettings settings,
        TaskStore store,
        TaskActor actor,
        CancellationToken cancellationToken
    )
    {
        var rejected = store.Reject(settings.Id, settings.Note, actor.Name, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["proposal"] = TaskViews.Proposal(rejected) });
        }
        else
        {
            await output.WriteLineAsync($"Rejected {rejected.Id}.");
        }
    }

    private async Task ListAsync(TaskListSettings settings, TaskStore store, CancellationToken cancellationToken)
    {
        string[] statuses;
        try
        {
            statuses = settings.Status.SelectMany(s => s.Split(',')).Select(TaskWorkflow.ParseStatus).ToArray();
        }
        catch (ArgumentException exception)
        {
            throw new TaskUsageException(exception.Message);
        }

        if (statuses.Length is 0 && !settings.All)
        {
            statuses = ["backlog", "ready", "claimed", "in_progress", "review", "blocked"];
        }

        var tasks = store.List(new(statuses, settings.Blueprint), cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["tasks"] = tasks.Select(TaskViews.Task).ToArray() });
        }
        else if (tasks.Count is 0)
        {
            await output.WriteLineAsync("No tasks match.");
        }
        else
        {
            await output.WriteLineAsync($"{"ID", -10} {"STATUS", -11} {"MS", -6} {"PRIORITY", -8} TITLE");
            foreach (var t in tasks)
            {
                var extra = t switch
                {
                    { Status: "blocked" or "deferred" } => $"  [{t.Status}: {t.BlockedReason}]",
                    { Claimant.Length: > 0 } => $"  [{t.Claimant}]",
                    _ => "",
                };
                await output.WriteLineAsync(
                    $"{t.Id, -10} {t.Status, -11} {t.Definition.Blueprint, -6} {t.Definition.Priority, -8} {t.Definition.Title}{extra}"
                );
            }
        }
    }

    private async Task BlueprintsAsync(InspectSettings settings, TaskStore store, CancellationToken cancellationToken)
    {
        var blueprints = store.Blueprints(cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["blueprints"] = blueprints.Select(TaskViews.Blueprint).ToArray() });
        }
        else
        {
            for (var i = 0; i < blueprints.Count; i++)
            {
                var m = blueprints[i];
                var d = m.Definition;
                if (i > 0)
                {
                    await output.WriteLineAsync();
                }

                await output.WriteLineAsync(
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{d.Id, -6} {(d.Title.Length is 0 ? "(not defined in the backlog)" : d.Title)}  [{m.Tasks.GetValueOrDefault("done")}/{m.Total} done]"
                    )
                );
                if (d.Goal.Length is not 0)
                {
                    await output.WriteLineAsync("       goal: " + d.Goal);
                }

                foreach (var e in d.Exit ?? [])
                {
                    await output.WriteLineAsync("       exit: " + e);
                }

                if (d.Notes.Length is not 0)
                {
                    await output.WriteLineAsync("       note: " + d.Notes);
                }
            }
        }
    }

    private async Task NextAsync(TaskNextSettings settings, TaskStore store, CancellationToken cancellationToken)
    {
        var next = store.Next(settings.Blueprint, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["task"] = next is null ? null : TaskViews.Task(next) });
        }
        else if (next is null)
        {
            await output.WriteLineAsync("No ready task.");
        }
        else
        {
            await PrintTaskAsync(next);
            await output.WriteLineAsync($"\nClaim it with: harness tasks claim {next.Id}");
        }
    }

    private async Task ShowAsync(TaskShowSettings settings, TaskStore store, CancellationToken cancellationToken)
    {
        var task = store.Get(settings.Id.ToUpperInvariant(), cancellationToken);
        var events = settings.Events ? store.Events(task.Id, cancellationToken) : [];
        if (settings.Json)
        {
            Dictionary<string, object?> view = new() { ["task"] = TaskViews.Task(task) };
            if (settings.Events)
            {
                view["events"] = events.Select(TaskViews.Event).ToArray();
            }

            await JsonAsync(view);
        }
        else
        {
            await PrintTaskAsync(task);
            if (settings.Events)
            {
                await output.WriteLineAsync("\nEvents:");
                foreach (var e in events)
                {
                    var move = e.From.Length is not 0 || e.To.Length is not 0 ? $" {e.From} -> {e.To}" : "";
                    await output.WriteLineAsync($"  {Local(e.At)}  {e.Event, -18}{move}  by {e.Actor}  {e.Detail}");
                }
            }
        }
    }

    private async Task AddAsync(
        TaskAddSettings settings,
        TaskStore store,
        TaskActor actor,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(settings.Title))
        {
            throw new TaskUsageException("--title is required");
        }

        var created = store.Create(
            new()
            {
                Id = settings.Id,
                Title = settings.Title,
                Goal = settings.Goal.Trim(),
                Type = settings.Type,
                Priority = settings.Priority,
                Blueprint = settings.Blueprint,
                DependsOn = Split(settings.Depends).Select(dependency => dependency.ToUpperInvariant()).ToArray(),
                Scope = Split(settings.Scope),
                Acceptance = settings
                    .Accept.Select(criterion => criterion.Trim())
                    .Where(criterion => criterion.Length is not 0)
                    .ToArray(),
            },
            settings.Prefix,
            new(actor.Name, "cli", "added with harness tasks add"),
            cancellationToken
        );
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["task"] = TaskViews.Task(created) });
        }
        else
        {
            await output.WriteLineAsync($"Added {created.Id} ({created.Status}): {created.Definition.Title}");
            await output.WriteLineAsync("Write it into the backlog file with: harness tasks export");
        }
    }

    private async Task ChangeStateAsync(
        TaskIdSettings settings,
        TaskStore store,
        TaskActor actor,
        string verb,
        CancellationToken cancellationToken
    )
    {
        var reason = settings is TaskReasonSettings withReason ? withReason.Reason : "";
        var note = settings is TaskReadySettings withNote ? withNote.Note : "";
        if (verb is not "ready" && string.IsNullOrWhiteSpace(reason))
        {
            throw new TaskUsageException("--reason is required");
        }

        var id = settings.Id.ToUpperInvariant();
        var changed = verb switch
        {
            "block" => store.Block(id, actor, reason, cancellationToken),
            "defer" => store.Defer(id, actor, reason, cancellationToken),
            _ => store.Unblock(id, actor, note, cancellationToken),
        };
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["task"] = TaskViews.Task(changed) });
        }
        else
        {
            await output.WriteLineAsync(
                verb switch
                {
                    "block" => $"Blocked {id}: {changed.BlockedReason}",
                    "defer" => $"Deferred {id}: {changed.BlockedReason}",
                    _ => changed.Status is "ready"
                        ? $"{id} is ready."
                        : $"{id} unblocked; waiting for dependencies (status {changed.Status}).",
                }
            );
        }
    }

    private async Task FileAsync(
        string verb,
        TaskFileSettings settings,
        string root,
        TaskStore store,
        CancellationToken cancellationToken
    )
    {
        var file = settings.File.Length is 0 ? Path.Combine(root, "docs", "backlog.yaml") : settings.File;
        if (verb is "export")
        {
            var result = store.Export(file, settings.DryRun, cancellationToken);
            if (settings.Json)
            {
                await JsonAsync(
                    new JsonObjectMap
                    {
                        ["dry_run"] = settings.DryRun,
                        ["export"] = new JsonObjectMap
                        {
                            ["path"] = result.Path,
                            ["added"] = result.Added,
                            ["created"] = result.Created,
                        },
                    }
                );
            }
            else if (result.Added.Count is 0 && !result.Created)
            {
                await output.WriteLineAsync($"{file} already holds every task.");
            }
            else
            {
                await output.WriteLineAsync(
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{(settings.DryRun ? "Would write" : "Wrote")} {result.Added.Count} task(s) into {file}: {string.Join(", ", result.Added)}"
                    )
                );
            }

            return;
        }

        var backlog = BacklogCodec.Read(file);
        var relative = Path.GetRelativePath(root, Path.GetFullPath(file));
        var source = relative.StartsWith("..", StringComparison.Ordinal) ? file : relative;
        var imported = store.Import(backlog.Tasks, source, settings.DryRun, cancellationToken);
        var blueprints = store.ImportBlueprints(backlog.Blueprints, source, settings.DryRun, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(
                new JsonObjectMap
                {
                    ["dry_run"] = settings.DryRun,
                    ["added"] = Nil(imported.Added),
                    ["updated"] = Nil(imported.Updated),
                    ["unchanged"] = imported.Unchanged.Count,
                    ["orphaned"] = Nil(imported.Orphaned),
                    ["unlocked"] = Nil(imported.Unlocked),
                    ["blueprints_changed"] = blueprints,
                }
            );
        }
        else
        {
            await output.WriteLineAsync(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{(settings.DryRun ? "Would import" : "Imported")} {source}: {imported.Added.Count} added, {imported.Updated.Count} updated, {imported.Unchanged.Count} unchanged."
                )
            );
            if (blueprints > 0)
            {
                await output.WriteLineAsync(
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"Blueprints added or changed: {blueprints}"
                    )
                );
            }

            if (imported.Updated.Count > 0)
            {
                await output.WriteLineAsync("Updated: " + string.Join(", ", imported.Updated));
            }

            if (imported.Unlocked.Count > 0)
            {
                await output.WriteLineAsync("Ready: " + string.Join(", ", imported.Unlocked));
            }

            if (imported.Orphaned.Count > 0)
            {
                await output.WriteLineAsync(
                    "In the database but not in the file (kept): " + string.Join(", ", imported.Orphaned)
                );
            }
        }
    }

    private Task JsonAsync(object value) => output.WriteLineAsync(HarnessJson.Serialize(value, JsonOptions));

    private static IReadOnlyList<string>? Nil(IReadOnlyList<string> values) => values.Count is 0 ? null : values;

    private static string[] Split(string[] values) =>
        values.SelectMany(v => v.Split(',')).Select(v => v.Trim()).Where(v => v.Length is not 0).ToArray();

    private static string Local(Harness.Core.Memory.MemoryTimestamp time) =>
        DateTimeOffset
            .Parse(time.ToRfc3339(), CultureInfo.InvariantCulture)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private async Task PrintTaskAsync(TaskRecord t)
    {
        var d = t.Definition;
        await output.WriteLineAsync($"{t.Id}  {d.Title}");
        await output.WriteLineAsync(
            $"  status:     {t.Status}{(t.BlockedReason.Length is 0 ? "" : " (" + t.BlockedReason + ")")}"
        );
        await output.WriteLineAsync($"  blueprint:  {d.Blueprint}   priority: {d.Priority}   type: {d.Type}");
        if (d.Goal.Length is not 0)
        {
            await output.WriteLineAsync("  goal:       " + d.Goal);
        }

        if (d.Scope.Count is not 0)
        {
            await output.WriteLineAsync("  scope:      " + string.Join(", ", d.Scope));
        }

        if (t.Dependencies.Count is not 0)
        {
            await output.WriteLineAsync(
                "  depends on: " + string.Join(", ", t.Dependencies.Select(dep => $"{dep.Id} ({dep.Status})"))
            );
        }

        if (t.Claimant.Length is not 0)
        {
            await output.WriteLineAsync(
                $"  claimed by: {t.Claimant}{(t.LeaseUntil is { } until ? ", lease until " + Local(until) : "")}"
            );
        }

        if (t.Criteria.Count is not 0)
        {
            await output.WriteLineAsync("  acceptance:");
            foreach (var a in t.Criteria)
            {
                await output.WriteLineAsync(
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"    {a.Index, 2}. [{a.Result, -7}] {a.Criterion}{(a.Evidence.Length is 0 ? "" : " — " + a.Evidence)}"
                    )
                );
            }
        }

        if (d.Validation.Count is not 0)
        {
            await output.WriteLineAsync("  validation: " + string.Join("; ", d.Validation));
        }

        if (d.Notes.Length is not 0)
        {
            await output.WriteLineAsync("  notes:\n    " + d.Notes.Replace("\n", "\n    ", StringComparison.Ordinal));
        }

        if (t.CompletedAt is { } done)
        {
            await output.WriteLineAsync(
                $"  completed:  {Local(done)} by {t.CompletedBy}{(t.CompletionCommit.Length is 0 ? "" : " (commit " + t.CompletionCommit + ")")}"
            );
            await output.WriteLineAsync("  summary:    " + t.CompletionSummary);
        }
    }
}
