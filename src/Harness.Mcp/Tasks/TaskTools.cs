using System.Globalization;
using System.Text.Json;
using Harness.Core.Memory;
using Harness.Core.Tasks;

namespace Harness.Mcp.Tasks;

internal static class TaskTools
{
    private const string ClaimHint = "Keep claim_token; pass it to task_update and task_complete. Implement only the stated scope, run the validation, then complete with every acceptance criterion.";
    public static async Task<object> CallAsync(string name, IDictionary<string, JsonElement>? arguments, ITaskSession session, IMemorySession? memory, string caller, CancellationToken token)
    {
        var values = JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        var id = Text(values, "id").Trim().ToUpperInvariant();
        var actor = new TaskActor(caller, Text(values, "claim_token").Trim());
        switch (name)
        {
            case "task_next":
                var milestone = Text(values, "milestone").Trim();
                if (values.TryGetProperty("claim", out var claim) && claim.GetBoolean())
                {
                    var next = await session.ClaimNextTaskAsync(milestone, actor, Lease(values), token);
                    return next is null ? Empty() : Claimed(next);
                }
                var ready = await session.NextTaskAsync(milestone, token);
                return ready is null ? Empty() : new { task = TaskViews.Task(ready), hint = "Call task_claim with this id before starting work." };
            case "task_get":
                var task = await session.ReadTaskAsync(id, token);
                Dictionary<string, object?> output = new() { ["task"] = TaskViews.Task(task) };
                if (values.TryGetProperty("events", out var events) && events.GetBoolean())
                {
                    var trail = await session.TaskEventsAsync(id, token);
                    if (trail.Count > 0)
                    {
                        output["events"] = trail.Select(TaskViews.Event).ToArray();
                    }
                }
                return output;
            case "task_claim":
                return Claimed(await session.ClaimTaskAsync(id, actor, Lease(values), token));
            case "task_update":
                var status = Text(values, "status");
                return new { task = TaskViews.Task(await session.UpdateTaskAsync(id, actor, new(status.Length is 0 ? null : TaskWorkflow.ParseStatus(status), Text(values, "note"), Text(values, "reason"), Reports(values)), token)) };
            case "task_complete":
                TaskCompletionResult completed;
                try
                {
                    var checks = Items(values, "validation").Select(item => item.ValueKind is JsonValueKind.String
                        ? new TaskCheck(item.GetString()!, "pass") : new TaskCheck(Text(item, "command"), Result(Text(item, "result")), Text(item, "output"))).ToArray();
                    completed = await session.CompleteTaskAsync(id, actor, new(Text(values, "summary"), Text(values, "commit").Trim(), Reports(values), checks.Length is 0 ? null : checks), token);
                }
                catch (InvalidTaskCompletionException exception)
                { throw new InvalidTaskCompletionException(exception.Message + ". Fix the gaps, or use task_update (status review/blocked, with a note) if the task cannot be finished.", exception); }
                Dictionary<string, object?> result = new() { ["task"] = TaskViews.Task(completed.Task), ["hint"] = "Record durable decisions from this task with memory_put, then take the next task with task_next." };
                if (completed.Unlocked.Count > 0)
                {
                    result["unlocked"] = completed.Unlocked;
                }

                var proposals = Items(values, "proposals");
                if (proposals.Length > 0)
                {
                    List<object> proposed = [];
                    List<string> errors = [];
                    foreach (var proposal in proposals)
                    {
                        try
                        {
                            if (memory is null)
                            {
                                throw new InvalidOperationException("memory session unavailable");
                            }

                            var source = Text(proposal, "source_ref");
                            var created = await memory.ProposeMemoryAsync(new(MemoryText.ParseType(Text(proposal, "type")), Text(proposal, "content"), MemoryScope.Parse(Text(proposal, "scope")),
                                new("agent", string.IsNullOrWhiteSpace(source) ? "task:" + id : source, caller), Text(proposal, "rationale"), Text(proposal, "supersedes").Trim()), token);
                            if (created.Proposal is not null)
                            {
                                proposed.Add(MemoryViews.Proposal(created.Proposal));
                            }
                        }
                        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                        { errors.Add("memory content rejected: credential validation timed out"); }
                        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
                        { errors.Add(exception.Message); }
                    }
                    if (proposed.Count > 0)
                    {
                        result["proposals"] = proposed;
                    }

                    if (errors.Count > 0)
                    {
                        result["proposal_errors"] = errors;
                    }

                    result["hint"] = "Proposals wait for the user's approval (memory_proposals); take the next task with task_next.";
                }
                return result;
            case "task_propose":
                var definition = new TaskDefinition
                {
                    Title = Text(values, "title"),
                    Goal = Text(values, "goal").Trim(),
                    Acceptance = Strings(values, "acceptance"),
                    Type = Text(values, "type").Trim(),
                    Priority = Text(values, "priority").Trim(),
                    Milestone = Text(values, "milestone"),
                    Scope = Strings(values, "scope"),
                    DependsOn = Strings(values, "depends_on").Select(d => d.Trim().ToUpperInvariant()).ToArray()
                };
                return new { proposal = TaskViews.Proposal(await session.ProposeTaskAsync(definition, Text(values, "reason"), caller, token)), note = "Waiting for the user: tell them it is in harness ui, Work › Inbox (or 'harness tasks proposals'). Do not start it until it is a task." };
            default:
                throw new InvalidOperationException("Unknown task tool.");
        }
    }
    private static object Empty() => new { task = (object?)null, hint = "No ready task. Everything is done, waiting on dependencies, claimed or blocked." };
    private static object Claimed(TaskClaimResult claim) => new { task = TaskViews.Task(claim.Task), claim_token = claim.Token, hint = ClaimHint };
    private static TimeSpan Lease(JsonElement values)
    {
        var input = values.TryGetProperty("claim", out _) ? "taskNextInput" : "taskClaimInput";
        var minutes = Integer(values, "lease_minutes", input);
        return TimeSpan.FromMinutes(minutes > 0 ? Math.Min(minutes, 1440) : 120);
    }
    private static string Text(JsonElement values, string key) => values.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
    private static string[] Strings(JsonElement values, string key) => Items(values, key).Select(v => v.GetString() ?? "").ToArray();
    private static JsonElement[] Items(JsonElement values, string key) => values.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    private static TaskReport[] Reports(JsonElement values) => Items(values, "acceptance").Select(item => new TaskReport(Integer(item, "index", "reportInput"), Result(Text(item, "result")), Text(item, "evidence"))).ToArray();
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
            formatted = Math.Abs(number) < 1e21 ? decimal.Parse(formatted, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("0", CultureInfo.InvariantCulture) : formatted.Replace('E', 'e');
            throw new ArgumentException($"json: cannot unmarshal number {formatted}}} overflows into Go struct field server.{input}.{key} of type int");
        }
        return (long)number;
    }
}
