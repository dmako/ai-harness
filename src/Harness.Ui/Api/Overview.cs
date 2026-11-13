namespace Harness.Ui.Api;

// The overview area: GET /api/projects/{id}/tasks (every task, without details), GET /api/projects/{id}/report?days=N
// (how harness serves the project's agents), GET /api/projects/{id}/trends?days=N (a row per day) and
// GET /api/activity?days=7&limit=N[&project=id] (what happened, newest first). Required members are always in the
// server's answer.

/// <summary>GET /api/projects/{id}/tasks.</summary>
public sealed class TaskList
{
    public required IReadOnlyList<TaskSummary> Tasks { get; init; }
}

/// <summary>GET /api/projects/{id}/report: the context answers' relevance and the findings the user can act on.</summary>
public sealed class ProjectReport
{
    public required int Days { get; init; }
    public required ContextStatistics Context { get; init; }

    /// <summary>Absent when there is nothing to act on.</summary>
    public IReadOnlyList<ReportFinding>? Findings { get; init; }
}

/// <summary>How well get_context's answers held the files agents then changed (hits per changed file).</summary>
public sealed class ContextStatistics
{
    public required int Calls { get; init; }
    public required int Changed { get; init; }
    public required int Hits { get; init; }
    public required double HitRate { get; init; }
    public AfterRead? AfterRead { get; init; }
}

/// <summary>Of the files agents read or edited after an answer (client hooks), how many were in it.</summary>
public sealed class AfterRead
{
    public required int Touched { get; init; }
    public required int Hits { get; init; }
    public required double HitRate { get; init; }
}

/// <summary>Something harness finds over the window: what it is, how severe, and what to do.</summary>
public sealed class ReportFinding
{
    /// <summary>problem, warn or info.</summary>
    public required string Level { get; init; }

    /// <summary>user (the report leaves the developer's out).</summary>
    public required string Audience { get; init; }

    public required string Area { get; init; }
    public required string Message { get; init; }
    public string? Action { get; init; }
}

/// <summary>GET /api/projects/{id}/trends: the window's days, oldest first.</summary>
public sealed class ProjectTrends
{
    public required IReadOnlyList<TrendDay> Days { get; init; }
}

public sealed class TrendDay
{
    public required string Day { get; init; }

    /// <summary>The context answers scored that day (0: no rate).</summary>
    public required int Answers { get; init; }

    public required double HitRate { get; init; }

    /// <summary>Tasks completed that day.</summary>
    public required int Done { get; init; }
}

/// <summary>GET /api/activity: agents' sessions, task and knowledge changes and syncs, newest first.</summary>
public sealed class ActivityFeed
{
    public required int Days { get; init; }
    public required IReadOnlyList<ActivityItem> Items { get; init; }
}

public sealed class ActivityItem
{
    public required string At { get; init; }

    /// <summary>session, task, memory or sync.</summary>
    public required string Kind { get; init; }

    public required string Title { get; init; }
    public string? Detail { get; init; }

    /// <summary>The task's id (task) or the memory's (memory).</summary>
    public string? Ref { get; init; }

    /// <summary>The client (session) or who made the change.</summary>
    public string? Actor { get; init; }

    /// <summary>warn or problem, when something went wrong.</summary>
    public string? Level { get; init; }

    public required string ProjectId { get; init; }
    public required string Project { get; init; }
}
