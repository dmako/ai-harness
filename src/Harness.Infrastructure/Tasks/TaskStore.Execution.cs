using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Memory;
using Harness.Core.Tasks;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Tasks;

public sealed partial class TaskStore
{
    public static TimeSpan DefaultLease => TimeSpan.FromHours(2);

    public TaskClaimResult Claim(string id, TaskActor actor, TimeSpan lease = default, CancellationToken cancellationToken = default) => database.WithTransaction((c, t) =>
    {
        Refresh(c, t);
        return Claim(c, t, id, actor, lease);
    }, cancellationToken);

    public TaskClaimResult? ClaimNext(string milestone, TaskActor actor, TimeSpan lease = default, CancellationToken cancellationToken = default) => database.WithTransaction((c, t) =>
    {
        Refresh(c, t);
        var next = Next(c, t, milestone);
        return next is null ? null : Claim(c, t, next.Id, actor, lease);
    }, cancellationToken);

    private TaskClaimResult Claim(SqliteConnection c, SqliteTransaction t, string id, TaskActor actor, TimeSpan lease)
    {
        var current = Get(c, t, id);
        if (current.Status is not "ready")
            throw new InvalidOperationException(ClaimRefusal(current));
        if (lease <= TimeSpan.Zero)
            lease = DefaultLease;
        var token = "clm_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var now = _time.GetUtcNow();
        var stamp = HarnessDatabase.FormatTime(now);
        Execute(c, t, "UPDATE tasks SET status='claimed',claimant=$by,claim_token=$token,claimed_at=$stamp,lease_until=$until,updated_at=$stamp WHERE id=$id AND status='ready'",
            ("$by", actor.AuditName), ("$token", token), ("$stamp", stamp), ("$until", HarnessDatabase.FormatTime(now.Add(lease))), ("$id", id));
        AddEvent(c, t, id, "claimed", "ready", "claimed", actor.AuditName, "lease " + GoDuration(lease), stamp);
        return new(Get(c, t, id), token);
    }

    private static string ClaimRefusal(TaskRecord task)
    {
        if (TaskWorkflow.HoldsClaim(task.Status))
            return $"{task.Id}: task is claimed by someone else ({task.Claimant}, lease{(task.LeaseUntil is { } until ? " until " + until.ToRfc3339() : "")})";
        var detail = task.Status switch
        {
            "backlog" => "waiting for " + string.Join(", ", task.Dependencies.Where(d => d.Status is not "done").Select(d => $"{d.Id} ({d.Status})")),
            "blocked" => "blocked: " + task.BlockedReason,
            _ => "status is " + task.Status,
        };
        return $"{task.Id}: task is not ready to claim: {detail}";
    }

    public TaskRecord Release(string id, TaskActor actor, string reason = "", CancellationToken cancellationToken = default) => database.WithTransaction((c, t) =>
    {
        Refresh(c, t);
        var current = Get(c, t, id);
        if (!TaskWorkflow.HoldsClaim(current.Status))
            throw new InvalidOperationException($"{id}: not claimed (status {current.Status})");
        var kind = "released";
        if (actor.Token.Length is 0 || actor.Token != current.ClaimToken)
        {
            if (!actor.Privileged)
                throw new InvalidOperationException($"{id}: claim token does not match the task's current claim");
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("force-releasing someone else's claim requires a reason");
            kind = "force_released";
            reason = "claim by " + current.Claimant + " released: " + reason;
        }
        var stamp = Stamp();
        ClearClaim(c, t, id, "ready", stamp);
        AddEvent(c, t, id, kind, current.Status, "ready", actor.AuditName, reason, stamp);
        RecomputeReadiness(c, t);
        return Get(c, t, id);
    }, cancellationToken);

    public TaskRecord Update(string id, TaskActor actor, TaskUpdate input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return database.WithTransaction((c, t) =>
        {
            Refresh(c, t);
            var current = Get(c, t, id);
            Authorize(current, actor);
            var now = _time.GetUtcNow();
            var stamp = HarnessDatabase.FormatTime(now);
            ApplyReports(c, t, current, input.Acceptance ?? []);
            var note = CredentialRedactor.Redact(input.Note.Trim());
            if (note.Length is not 0)
            {
                var line = "[" + now.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + actor.AuditName + "] " + note;
                Execute(c, t, "UPDATE tasks SET notes=$notes,updated_at=$stamp WHERE id=$id", ("$notes", current.Definition.Notes + (current.Definition.Notes.Length is 0 ? "" : "\n") + line), ("$stamp", stamp), ("$id", id));
                AddEvent(c, t, id, "note", "", "", actor.AuditName, note, stamp);
            }
            if (input.Status is { } status && status != current.Status)
                Transition(c, t, current, status, actor, input.Reason, stamp);
            Execute(c, t, "UPDATE tasks SET lease_until=$until WHERE id=$id AND status IN ('claimed','in_progress','review')",
                ("$until", HarnessDatabase.FormatTime(now.Add(input.Lease <= TimeSpan.Zero ? DefaultLease : input.Lease))), ("$id", id));
            return Get(c, t, id);
        }, cancellationToken);
    }

    public TaskCompletionResult Complete(string id, TaskActor actor, TaskCompletion input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return database.WithTransaction((c, t) =>
        {
            Refresh(c, t);
            var current = Get(c, t, id);
            Authorize(current, actor);
            if (!TaskWorkflow.HoldsClaim(current.Status))
                throw new InvalidTaskCompletionException($"{id}: invalid completion: status is {current.Status}; claim it first");
            ValidateCompletion(current, input);
            var stamp = Stamp();
            ApplyReports(c, t, current, input.Acceptance ?? []);
            var checks = input.Checks?.Select(check => new Dictionary<string, object?>
            {
                ["command"] = CredentialRedactor.Redact(check.Command),
                ["result"] = check.Result,
            }.Concat(check.Output.Length is 0 ? [] : new[] { new KeyValuePair<string, object?>("output", CredentialRedactor.Redact(check.Output)) }).ToDictionary(StringComparer.Ordinal)).ToArray();
            Execute(c, t, """
                UPDATE tasks SET status='done',claimant=NULL,claim_token=NULL,lease_until=NULL,completion_summary=$summary,completion_commit=$commit,
                completion_checks=$checks,completed_by=$by,completed_at=$stamp,updated_at=$stamp WHERE id=$id
                """, ("$summary", CredentialRedactor.Redact(input.Summary.Trim())), ("$commit", Null(input.Commit)), ("$checks", JsonSerializer.Serialize(checks, JsonOptions)),
                ("$by", actor.AuditName), ("$stamp", stamp), ("$id", id));
            AddEvent(c, t, id, "completed", current.Status, "done", actor.AuditName, input.Summary.Trim(), stamp);
            var unlocked = RecomputeReadiness(c, t);
            return new TaskCompletionResult(Get(c, t, id), unlocked);
        }, cancellationToken);
    }

    private static void ValidateCompletion(TaskRecord task, TaskCompletion input)
    {
        List<string> problems = [];
        if (string.IsNullOrWhiteSpace(input.Summary))
            problems.Add("summary is required");
        if (input.Commit.Length is not 0 && !CommitPattern().IsMatch(input.Commit))
            problems.Add($"commit {TaskWorkflow.Quote(input.Commit)} is not a hex commit id");
        problems.AddRange(task.Dependencies.Where(d => d.Status is not "done").Select(d => $"dependency {d.Id} is {d.Status}"));
        Dictionary<long, TaskReport> reported = [];
        foreach (var report in input.Acceptance ?? [])
        {
            if (report.Index < 1 || report.Index > task.Criteria.Count)
            { problems.Add(string.Create(CultureInfo.InvariantCulture, $"acceptance index {report.Index} does not exist (task has {task.Criteria.Count} criteria)")); continue; }
            if (reported.ContainsKey(report.Index))
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"acceptance {report.Index} reported twice"));
            reported[report.Index] = report;
        }
        foreach (var criterion in task.Criteria)
        {
            reported.TryGetValue(criterion.Index, out var report);
            var problem = report switch
            {
                null => $"not reported: {criterion.Criterion}",
                { Result: "fail" } => $"failed: {criterion.Criterion}",
                { Result: "n/a" } when string.IsNullOrWhiteSpace(report.Evidence) => "is n/a without an explanation",
                { Result: not ("pass" or "n/a") } => $"result must be pass, fail or n/a, got {TaskWorkflow.Quote(report.Result)}",
                _ => "",
            };
            if (problem.Length is not 0)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"acceptance {criterion.Index} {problem}"));
        }
        problems.AddRange((input.Checks ?? []).Where(ch => ch.Result is not "pass").Select(ch => $"validation {TaskWorkflow.Quote(ch.Command)} did not pass ({ch.Result})"));
        if (problems.Count is not 0)
            throw new InvalidTaskCompletionException($"{task.Id}: invalid completion: {string.Join("; ", problems)}");
    }

    private static void ApplyReports(SqliteConnection c, SqliteTransaction t, TaskRecord task, IReadOnlyList<TaskReport> reports)
    {
        foreach (var report in reports)
        {
            if (report.Index < 1 || report.Index > task.Criteria.Count)
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{task.Id}: acceptance index {report.Index} does not exist (task has {task.Criteria.Count} criteria)"));
            if (report.Result is not ("pending" or "pass" or "fail" or "n/a"))
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{task.Id}: acceptance {report.Index} result must be pass, fail, n/a or pending"));
            Execute(c, t, "UPDATE task_acceptance SET result=$result,evidence=$evidence WHERE task_id=$id AND idx=$idx", ("$result", report.Result), ("$evidence", Null(CredentialRedactor.Redact(report.Evidence.Trim()))), ("$id", task.Id), ("$idx", report.Index));
        }
    }

    private static string GoDuration(TimeSpan duration)
    {
        if (duration.Ticks < 10)
            return (duration.Ticks * 100).ToString(CultureInfo.InvariantCulture) + "ns";
        if (duration.Ticks < TimeSpan.TicksPerMillisecond)
            return (duration.Ticks / 10d).ToString("0.##", CultureInfo.InvariantCulture) + "µs";
        if (duration.Ticks < TimeSpan.TicksPerSecond)
            return (duration.Ticks / 10000d).ToString("0.####", CultureInfo.InvariantCulture) + "ms";
        var hours = (long)duration.TotalHours;
        var prefix = hours is 0 ? "" : hours.ToString(CultureInfo.InvariantCulture) + "h";
        return prefix + (hours is 0 && duration.Minutes is 0 ? "" : duration.Minutes.ToString(CultureInfo.InvariantCulture) + "m") +
            (duration.Seconds + ((double)(duration.Ticks % TimeSpan.TicksPerSecond) / TimeSpan.TicksPerSecond)).ToString("0.#######", CultureInfo.InvariantCulture) + "s";
    }
    [GeneratedRegex(@"\A[0-9a-f]{7,64}\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CommitPattern();
}
