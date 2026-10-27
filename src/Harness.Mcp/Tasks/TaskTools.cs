using System.Globalization;
using System.Text.Json;
using Harness.Core.Memory;
using Harness.Core.Serialization;
using Harness.Core.Tasks;

namespace Harness.Mcp.Tasks;

internal static class TaskTools
{
    private const string ClaimHint =
        "Keep claim_token; pass it to task_update and task_complete. Analyse and clarify while the task is claimed; "
        + "task_update it to in_progress when you start changing code, implement only the stated scope, then to review "
        + "while you check the acceptance criteria, constraints and validation, and complete with every acceptance criterion.";

    private const string ReviewSkippedHint =
        "This task skipped review (recorded in its audit trail): next time, task_update it to in_progress when you "
        + "start changing code and to review while you check the acceptance criteria before task_complete. ";

    public static async Task<object> CallAsync(
        string name,
        IDictionary<string, JsonElement>? arguments,
        ITaskSession session,
        IMemorySession? memory,
        string caller,
        CancellationToken token
    )
    {
        var values = HarnessJson.SerializeToElement(
            arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        );
        var id = Text(values, "id").Trim().ToUpperInvariant();
        var actor = new TaskActor(caller, Text(values, "claim_token").Trim());
        return name switch
        {
            "task_next" => await NextAsync(values, session, actor, token),
            "task_get" => await GetAsync(values, id, session, token),
            "task_claim" => Claimed(await session.ClaimTaskAsync(id, actor, Lease(values), token)),
            "task_update" => await UpdateAsync(values, id, actor, session, token),
            "task_complete" => await CompleteAsync(values, id, actor, session, memory, caller, token),
            "task_propose" => await ProposeAsync(values, session, caller, token),
            "task_blueprints" => new JsonObjectMap
            {
                ["blueprints"] = (await session.BlueprintsAsync(token)).Select(TaskViews.Blueprint).ToArray(),
            },
            "task_blueprint_propose" => await ProposeBlueprintAsync(values, session, caller, token),
            _ => throw new InvalidOperationException("Unknown task tool."),
        };
    }

    /// <summary>The next ready task in the blueprint, claimed when asked to.</summary>
    private static async Task<JsonObjectMap> NextAsync(
        JsonElement values,
        ITaskSession session,
        TaskActor actor,
        CancellationToken token
    )
    {
        var blueprint = Text(values, "blueprint").Trim();
        if (values.TryGetProperty("claim", out var claim) && claim.GetBoolean())
        {
            var next = await session.ClaimNextTaskAsync(blueprint, actor, Lease(values), token);
            return next is null ? Empty() : Claimed(next);
        }
        var ready = await session.NextTaskAsync(blueprint, token);
        return ready is null
            ? Empty()
            : new JsonObjectMap
            {
                ["task"] = TaskViews.Task(ready),
                ["hint"] = "Call task_claim with this id before starting work.",
            };
    }

    private static async Task<Dictionary<string, object?>> GetAsync(
        JsonElement values,
        string id,
        ITaskSession session,
        CancellationToken token
    )
    {
        var task = await session.ReadTaskAsync(id, token);
        Dictionary<string, object?> output = new() { ["task"] = TaskViews.Task(task) };
        if (!values.TryGetProperty("events", out var events) || !events.GetBoolean())
        {
            return output;
        }

        var trail = await session.TaskEventsAsync(id, token);
        if (trail.Count > 0)
        {
            output["events"] = trail.Select(TaskViews.Event).ToArray();
        }
        return output;
    }

    private static async Task<JsonObjectMap> UpdateAsync(
        JsonElement values,
        string id,
        TaskActor actor,
        ITaskSession session,
        CancellationToken token
    )
    {
        var status = Text(values, "status");
        var task = await session.UpdateTaskAsync(
            id,
            actor,
            new(
                status.Length is 0 ? null : TaskWorkflow.ParseStatus(status),
                Text(values, "note"),
                Text(values, "reason"),
                Reports(values)
            ),
            token
        );
        JsonObjectMap result = new() { ["task"] = TaskViews.Task(task) };
        var hint = UpdateHint(task.Status);
        if (hint.Length is not 0)
        {
            result["hint"] = hint;
        }

        return result;
    }

    /// <summary>The next step of the claimed, in progress, review, done path after an update.</summary>
    private static string UpdateHint(string status) =>
        status switch
        {
            "claimed" => "Set status in_progress when you start changing code.",
            "in_progress" =>
                "Set status review when the changes are made and you check the acceptance criteria, constraints and validation.",
            "review" =>
                "Complete with task_complete once every acceptance criterion passes; set status in_progress again if something needs fixing.",
            _ => "",
        };

    /// <summary>Completes the task, then proposes the memories it reports (best effort, errors listed).</summary>
    private static async Task<Dictionary<string, object?>> CompleteAsync(
        JsonElement values,
        string id,
        TaskActor actor,
        ITaskSession session,
        IMemorySession? memory,
        string caller,
        CancellationToken token
    )
    {
        var completed = await CompleteTaskAsync(values, id, actor, session, token);
        Dictionary<string, object?> result = new()
        {
            ["task"] = TaskViews.Task(completed.Task),
            ["hint"] =
                (completed.ReviewSkipped ? ReviewSkippedHint : "")
                + "Record durable decisions from this task with memory_put, then take the next task with task_next.",
        };
        if (completed.Unlocked.Count > 0)
        {
            result["unlocked"] = completed.Unlocked;
        }

        var proposals = Items(values, "proposals");
        if (proposals.Length > 0)
        {
            await AddProposalsAsync(result, proposals, id, memory, caller, token);
        }
        return result;
    }

    /// <summary>A rejected completion says how to fix it or park the task.</summary>
    private static async Task<TaskCompletionResult> CompleteTaskAsync(
        JsonElement values,
        string id,
        TaskActor actor,
        ITaskSession session,
        CancellationToken token
    )
    {
        try
        {
            var checks = Items(values, "validation")
                .Select(item =>
                    item.ValueKind is JsonValueKind.String
                        ? new TaskCheck(item.GetString()!, "pass")
                        : new TaskCheck(Text(item, "command"), Result(Text(item, "result")), Text(item, "output"))
                )
                .ToArray();
            return await session.CompleteTaskAsync(
                id,
                actor,
                new(
                    Text(values, "summary"),
                    Text(values, "commit").Trim(),
                    Reports(values),
                    checks.Length is 0 ? null : checks
                ),
                token
            );
        }
        catch (InvalidTaskCompletionException exception)
        {
            throw new InvalidTaskCompletionException(
                exception.Message
                    + ". Fix the gaps, or use task_update (status review/blocked, with a note) if the task cannot be finished.",
                exception
            );
        }
    }

    private static async Task AddProposalsAsync(
        Dictionary<string, object?> result,
        JsonElement[] proposals,
        string id,
        IMemorySession? memory,
        string caller,
        CancellationToken token
    )
    {
        List<object> proposed = [];
        List<string> errors = [];
        foreach (var proposal in proposals)
        {
            try
            {
                if (await ProposeMemoryAsync(proposal, id, memory, caller, token) is { } created)
                {
                    proposed.Add(MemoryViews.Proposal(created));
                }
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                errors.Add("memory content rejected: credential validation timed out");
            }
            catch (Exception exception)
                when (exception
                        is IOException
                            or InvalidDataException
                            or ArgumentException
                            or InvalidOperationException
                            or KeyNotFoundException
                            or FormatException
                )
            {
                errors.Add(exception.Message);
            }
        }
        if (proposed.Count > 0)
        {
            result["proposals"] = proposed;
        }

        if (errors.Count > 0)
        {
            result["proposal_errors"] = errors;
        }

        result["hint"] =
            "Proposals wait for the user's approval (memory_proposals); take the next task with task_next.";
    }

    /// <summary>One reported memory as a proposal, sourced to the task when it names no source.</summary>
    private static async Task<MemoryProposal?> ProposeMemoryAsync(
        JsonElement proposal,
        string id,
        IMemorySession? memory,
        string caller,
        CancellationToken token
    )
    {
        if (memory is null)
        {
            throw new InvalidOperationException("memory session unavailable");
        }

        var source = Text(proposal, "source_ref");
        var created = await memory.ProposeMemoryAsync(
            new(
                MemoryText.ParseType(Text(proposal, "type")),
                Text(proposal, "content"),
                MemoryScope.Parse(Text(proposal, "scope")),
                new("agent", string.IsNullOrWhiteSpace(source) ? "task:" + id : source, caller),
                Text(proposal, "rationale"),
                Text(proposal, "supersedes").Trim()
            ),
            token
        );
        return created.Proposal;
    }

    private static async Task<JsonObjectMap> ProposeAsync(
        JsonElement values,
        ITaskSession session,
        string caller,
        CancellationToken token
    )
    {
        var definition = new TaskDefinition
        {
            Title = Text(values, "title"),
            Goal = Text(values, "goal").Trim(),
            Acceptance = Strings(values, "acceptance"),
            Type = Text(values, "type").Trim(),
            Priority = Text(values, "priority").Trim(),
            Blueprint = Text(values, "blueprint"),
            Scope = Strings(values, "scope"),
            DependsOn = Strings(values, "depends_on").Select(d => d.Trim().ToUpperInvariant()).ToArray(),
        };
        return new JsonObjectMap
        {
            ["proposal"] = TaskViews.Proposal(
                await session.ProposeTaskAsync(definition, Text(values, "reason"), caller, token)
            ),
            ["note"] =
                "Waiting for the user: tell them it is in harness ui, Work › Inbox (or 'harness tasks proposals'). Do not start it until it is a task.",
        };
    }

    /// <summary>
    /// A new blueprint as given, or a change to one: the fields left out keep their current value, so the proposal
    /// holds the whole blueprint as it would be.
    /// </summary>
    private static async Task<JsonObjectMap> ProposeBlueprintAsync(
        JsonElement values,
        ITaskSession session,
        string caller,
        CancellationToken token
    )
    {
        var target = Text(values, "blueprint").Trim();
        var current = new BlueprintDefinition(target, "");
        if (target.Length is not 0)
        {
            current =
                (await session.BlueprintsAsync(token)).FirstOrDefault(b => b.Definition.Id == target)?.Definition
                ?? throw new ArgumentException($"unknown blueprint {target}");
        }

        string Given(string key, string now) => values.TryGetProperty(key, out _) ? Text(values, key) : now;
        var definition = new BlueprintDefinition(
            target,
            Given("title", current.Title),
            Given("goal", current.Goal),
            values.TryGetProperty("exit", out _) ? Strings(values, "exit") : current.Exit,
            Given("notes", current.Notes)
        );
        return new JsonObjectMap
        {
            ["proposal"] = TaskViews.BlueprintProposal(
                await session.ProposeBlueprintAsync(target, definition, Text(values, "reason"), caller, token)
            ),
            ["note"] =
                "Waiting for the user: tell them it is in harness ui, Work › Inbox (or 'harness tasks proposals'). Do not use it until it is approved.",
        };
    }

    private static JsonObjectMap Empty() =>
        new JsonObjectMap
        {
            ["task"] = (object?)null,
            ["hint"] = "No ready task. Everything is done, waiting on dependencies, claimed or blocked.",
        };

    private static JsonObjectMap Claimed(TaskClaimResult claim) =>
        new JsonObjectMap
        {
            ["task"] = TaskViews.Task(claim.Task),
            ["claim_token"] = claim.Token,
            ["hint"] = ClaimHint,
        };

    private static TimeSpan Lease(JsonElement values)
    {
        var input = values.TryGetProperty("claim", out _) ? "taskNextInput" : "taskClaimInput";
        var minutes = Integer(values, "lease_minutes", input);
        return TimeSpan.FromMinutes(minutes > 0 ? Math.Min(minutes, 1440) : 120);
    }

    private static string Text(JsonElement values, string key) =>
        values.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";

    private static string[] Strings(JsonElement values, string key) =>
        Items(values, key).Select(v => v.GetString() ?? "").ToArray();

    private static JsonElement[] Items(JsonElement values, string key) =>
        values.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : [];

    private static TaskReport[] Reports(JsonElement values) =>
        Items(values, "acceptance")
            .Select(item => new TaskReport(
                Integer(item, "index", "reportInput"),
                Result(Text(item, "result")),
                Text(item, "evidence")
            ))
            .ToArray();

    private static string Result(string value) => TaskWorkflow.NormalizeResult(value);

    private static long Integer(JsonElement values, string key, string input)
    {
        if (!values.TryGetProperty(key, out var value))
        {
            return 0;
        }

        var number = value.GetDouble();
        if (number >= 9223372036854775808d || number <= -9223372036854775808d)
        {
            var formatted = number.ToString("G", CultureInfo.InvariantCulture);
            formatted =
                Math.Abs(number) < 1e21
                    ? decimal.Parse(formatted, NumberStyles.Float, CultureInfo.InvariantCulture)
                        .ToString("0", CultureInfo.InvariantCulture)
                    : formatted.Replace('E', 'e');
            throw new ArgumentException(
                $"json: cannot unmarshal number {formatted}}} overflows input field {input}.{key} of type int"
            );
        }
        return (long)number;
    }
}
