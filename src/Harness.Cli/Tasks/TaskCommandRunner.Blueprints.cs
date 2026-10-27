using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Harness.Infrastructure.Tasks;

namespace Harness.Cli.Tasks;

internal sealed partial class TaskCommandRunner
{
    private const string NewBlueprintTitle = "New blueprint";

    private static bool IsBlueprintProposal(InspectSettings settings) =>
        settings is TaskIdSettings { Id: var id } && id.StartsWith("bpr_", StringComparison.Ordinal);

    /// <summary>
    /// Adds a blueprint (the next free id and a placeholder name unless given) or restates one; on an edit, what is
    /// not given stays.
    /// </summary>
    private async Task SaveBlueprintAsync(
        BlueprintSaveSettings settings,
        TaskStore store,
        bool add,
        CancellationToken cancellationToken
    )
    {
        BlueprintDefinition saved;
        if (add)
        {
            var id = settings.Id.Trim().Length is 0 ? store.NextBlueprintId(cancellationToken) : settings.Id;
            saved = store.CreateBlueprint(
                new(id, settings.Title ?? NewBlueprintTitle, settings.Goal ?? "", settings.Exit, settings.Notes ?? ""),
                "cli",
                cancellationToken
            );
        }
        else
        {
            var current =
                store.Blueprints(cancellationToken).FirstOrDefault(b => b.Definition.Id == settings.Id.Trim())
                ?? throw new ArgumentException($"unknown blueprint {settings.Id}");
            var d = current.Definition;
            saved = store.EditBlueprint(
                d.Id,
                new(
                    d.Id,
                    settings.Title ?? d.Title,
                    settings.Goal ?? d.Goal,
                    settings.Exit ?? d.Exit,
                    settings.Notes ?? d.Notes
                ),
                "cli",
                cancellationToken
            );
        }

        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["blueprint"] = TaskViews.BlueprintDefinition(saved) });
        }
        else
        {
            await output.WriteLineAsync($"{(add ? "Added" : "Saved")} blueprint {saved.Id}: {saved.Title}");
        }
    }

    private async Task RemoveBlueprintAsync(
        BlueprintRemoveSettings settings,
        TaskStore store,
        TaskActor actor,
        CancellationToken cancellationToken
    )
    {
        var tasks = settings.Tasks switch
        {
            "" when settings.To.Length is not 0 => BlueprintTasks.Move,
            "" => BlueprintTasks.Keep,
            "move" => BlueprintTasks.Move,
            "delete" when settings.To.Length is 0 => BlueprintTasks.Delete,
            "delete" => throw new TaskUsageException("--to moves tasks; it cannot go with --tasks delete"),
            var other => throw new TaskUsageException($"--tasks is move or delete, not {other}"),
        };
        var removed = store.RemoveBlueprint(settings.Id, tasks, settings.To, actor.Name, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(
                new JsonObjectMap
                {
                    ["blueprint"] = removed.Id,
                    ["moved"] = Nil(removed.Moved),
                    ["deleted"] = Nil(removed.Deleted),
                    ["unlinked"] = Nil(removed.Unlinked),
                }
            );
            return;
        }

        await output.WriteLineAsync($"Removed blueprint {removed.Id}.");
        if (removed.Moved.Count is not 0)
        {
            await output.WriteLineAsync(
                $"Moved to {(settings.To.Length is 0 ? "no blueprint" : settings.To)}: {string.Join(", ", removed.Moved)}"
            );
        }

        if (removed.Deleted.Count is not 0)
        {
            await output.WriteLineAsync("Deleted: " + string.Join(", ", removed.Deleted));
        }

        if (removed.Unlinked.Count is not 0)
        {
            await output.WriteLineAsync(
                "No longer depending on a deleted task: " + string.Join(", ", removed.Unlinked)
            );
        }
    }

    private async Task PrintBlueprintProposalsAsync(IReadOnlyList<BlueprintProposal> proposals)
    {
        foreach (var proposal in proposals)
        {
            var d = proposal.Definition;
            var what = proposal.Blueprint.Length is 0 ? "new blueprint" : "change to " + proposal.Blueprint;
            await output.WriteLineAsync(
                $"{proposal.Id}  [{proposal.Status}] {what}: {d.Title}  (by {proposal.ProposedBy})"
            );
            if (d.Goal.Length is not 0)
            {
                await output.WriteLineAsync("    goal: " + d.Goal);
            }

            foreach (var exit in d.Exit ?? [])
            {
                await output.WriteLineAsync("    exit: " + exit);
            }

            if (d.Notes.Length is not 0)
            {
                await output.WriteLineAsync("    note: " + d.Notes);
            }

            if (proposal.Reason.Length is not 0)
            {
                await output.WriteLineAsync("    why:  " + proposal.Reason);
            }

            if (proposal.Result.Length is not 0)
            {
                await output.WriteLineAsync("    became " + proposal.Result);
            }
        }
    }

    private async Task ApproveBlueprintAsync(
        TaskIdSettings settings,
        TaskStore store,
        TaskActor actor,
        CancellationToken cancellationToken
    )
    {
        var approved = store.ApproveBlueprint(settings.Id, null, actor.Name, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(
                new JsonObjectMap
                {
                    ["proposal"] = TaskViews.BlueprintProposal(approved.Proposal),
                    ["blueprint"] = TaskViews.BlueprintDefinition(approved.Blueprint),
                }
            );
        }
        else
        {
            await output.WriteLineAsync(
                $"Approved {approved.Proposal.Id}: blueprint {approved.Blueprint.Id} {approved.Blueprint.Title}"
            );
        }
    }

    private async Task RejectBlueprintAsync(
        TaskReadySettings settings,
        TaskStore store,
        TaskActor actor,
        CancellationToken cancellationToken
    )
    {
        var rejected = store.RejectBlueprint(settings.Id, settings.Note, actor.Name, cancellationToken);
        if (settings.Json)
        {
            await JsonAsync(new JsonObjectMap { ["proposal"] = TaskViews.BlueprintProposal(rejected) });
        }
        else
        {
            await output.WriteLineAsync($"Rejected {rejected.Id}.");
        }
    }
}
