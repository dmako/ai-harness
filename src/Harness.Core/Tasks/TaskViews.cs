namespace Harness.Core.Tasks;

public static class TaskViews
{
    public static Dictionary<string, object?> Task(TaskRecord t)
    {
        var d = t.Definition;
        Dictionary<string, object?> view = new() { ["id"] = t.Id, ["title"] = d.Title, ["status"] = t.Status, ["priority"] = d.Priority, ["type"] = d.Type };
        Add(view, "milestone", d.Milestone);
        Add(view, "goal", d.Goal);
        Add(view, "scope", d.Scope);
        Add(view, "validation", d.Validation);
        Add(view, "notes", d.Notes);
        Add(view, "blocked_reason", t.BlockedReason);
        if (t.Dependencies.Count is not 0)
            view["depends_on"] = t.Dependencies.Select(x => new { id = x.Id, status = x.Status }).ToArray();
        if (t.Criteria.Count is not 0)
            view["acceptance"] = t.Criteria.Select(x =>
        {
            Dictionary<string, object?> criterion = new() { ["index"] = x.Index, ["criterion"] = x.Criterion, ["result"] = x.Result };
            Add(criterion, "evidence", x.Evidence);
            return criterion;
        }).ToArray();
        if (t.Claimant.Length is not 0)
        {
            Dictionary<string, object?> claim = new() { ["claimant"] = t.Claimant };
            Add(claim, "claimed_at", t.ClaimedAt?.ToRfc3339() ?? "");
            Add(claim, "lease_until", t.LeaseUntil?.ToRfc3339() ?? "");
            view["claim"] = claim;
        }
        if (t.CompletedAt is { } at)
        {
            Dictionary<string, object?> completion = new() { ["summary"] = t.CompletionSummary, ["completed_by"] = t.CompletedBy, ["completed_at"] = at.ToRfc3339() };
            Add(completion, "commit", t.CompletionCommit);
            if (t.CompletionChecks.Count is not 0)
                completion["checks"] = t.CompletionChecks.Select(x =>
            {
                Dictionary<string, object?> check = new() { ["command"] = x.Command, ["result"] = x.Result };
                Add(check, "output", x.Output);
                return check;
            }).ToArray();
            view["completion"] = completion;
        }
        return view;
    }
    public static Dictionary<string, object?> Event(TaskEvent e)
    {
        Dictionary<string, object?> view = new() { ["event"] = e.Event, ["actor"] = e.Actor, ["at"] = e.At.ToRfc3339() };
        Add(view, "from", e.From);
        Add(view, "to", e.To);
        Add(view, "detail", e.Detail);
        return view;
    }
    public static Dictionary<string, object?> Milestone(MilestoneProgress m)
    {
        Dictionary<string, object?> view = new() { ["id"] = m.Definition.Id, ["title"] = m.Definition.Title, ["defined"] = m.Defined, ["tasks"] = m.Tasks, ["total"] = m.Total };
        Add(view, "goal", m.Definition.Goal);
        Add(view, "exit", m.Definition.Exit ?? []);
        Add(view, "notes", m.Definition.Notes);
        return view;
    }
    private static void Add(Dictionary<string, object?> view, string key, string value) { if (value.Length is not 0) view[key] = value; }
    private static void Add(Dictionary<string, object?> view, string key, IReadOnlyList<string> value) { if (value.Count is not 0) view[key] = value; }
}
