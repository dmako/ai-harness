using Harness.Core.Memory;

namespace Harness.Core.Tasks;

/// <summary>
/// How harness ui's Board shows a project's tasks: <paramref name="Sort"/> orders the cards in every column,
/// done tasks finished more than <paramref name="HideDoneDays"/> days ago are left out, and
/// <paramref name="Blueprint"/> ("" all, "—" none) keeps one blueprint's tasks.
/// </summary>
public sealed record BoardSettings(string Sort = "priority", int HideDoneDays = 7, string Blueprint = "");

/// <summary>A Board column: one task status and its cards, in order.</summary>
public sealed record BoardColumn(string Status, IReadOnlyList<TaskRecord> Tasks);

/// <summary>
/// The Board's columns and its choices: a column per status in the order work moves through them, blocked last.
/// Deferred tasks are not on the Board for now: only 'harness tasks defer' sets a task aside, nothing in harness ui
/// or MCP does.
/// </summary>
public static class TaskBoard
{
    public static IReadOnlyList<string> Statuses { get; } =
    ["backlog", "ready", "claimed", "in_progress", "review", "done", "blocked"];

    /// <summary>priority (highest first), id, name (title), created and changed (newest first).</summary>
    public static IReadOnlyList<string> Sorts { get; } = ["priority", "id", "name", "created", "changed"];

    public static IReadOnlyList<int> HideDoneDays { get; } = [1, 3, 7, 14];

    public static BoardSettings Defaults { get; } = new();

    /// <summary>A sort the Board offers, or an error naming those it does.</summary>
    public static string ValidSort(string sort) =>
        Sorts.Contains(sort)
            ? sort
            : throw new ArgumentException(
                $"unknown sort {TaskWorkflow.Quote(sort)}: use one of {string.Join(", ", Sorts)}"
            );

    /// <summary>A done-age the Board offers, or an error naming those it does.</summary>
    public static int ValidHideDoneDays(int days) =>
        HideDoneDays.Contains(days)
            ? days
            : throw new ArgumentException(
                $"hide done tasks older than {days} days: use one of {string.Join(", ", HideDoneDays)}"
            );

    /// <summary>
    /// Every shown status's column, each of its tasks in exactly one (its status's), sorted by
    /// <paramref name="settings"/>; a done task whose completion (else its last change) is older than the done-age
    /// is left out, and so is every deferred one (it has no column).
    /// </summary>
    public static IReadOnlyList<BoardColumn> Columns(
        IReadOnlyList<TaskRecord> tasks,
        BoardSettings settings,
        DateTimeOffset now
    )
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(settings);
        var cutoff = new MemoryTimestamp(now.AddDays(-settings.HideDoneDays));
        var kept = tasks
            .Where(t =>
                settings.Blueprint.Length is 0
                || (t.Definition.Blueprint is { Length: > 0 } b ? b : "—") == settings.Blueprint
            )
            .Where(t => t.Status is not "done" || (t.CompletedAt ?? t.UpdatedAt) >= cutoff)
            .ToArray();
        return Statuses
            .Select(status => new BoardColumn(status, Sorted(kept.Where(t => t.Status == status), settings.Sort)))
            .ToArray();
    }

    private static TaskRecord[] Sorted(IEnumerable<TaskRecord> tasks, string sort) =>
        sort switch
        {
            "id" => [.. tasks.OrderBy(t => t.Id, StringComparer.Ordinal)],
            "name" =>
            [
                .. tasks
                    .OrderBy(t => t.Definition.Title, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.Id, StringComparer.Ordinal),
            ],
            "created" => [.. tasks.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id, StringComparer.Ordinal)],
            "changed" => [.. tasks.OrderByDescending(t => t.UpdatedAt).ThenBy(t => t.Id, StringComparer.Ordinal)],
            _ =>
            [
                .. tasks
                    .OrderBy(t => TaskWorkflow.PriorityRank(t.Definition.Priority))
                    .ThenBy(t => t.Ordinal)
                    .ThenBy(t => t.Id, StringComparer.Ordinal),
            ],
        };
}
