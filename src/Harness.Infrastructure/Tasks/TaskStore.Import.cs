using Harness.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Tasks;

public sealed partial class TaskStore
{
    public TaskImportResult Import(IReadOnlyList<TaskDefinition> definitions, string source, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var defs = definitions.Select(d => d.Normalize()).ToArray();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            d.Validate();
            if (!seen.Add(d.Id))
                throw new ArgumentException($"task {d.Id} is defined twice");
        }
        return Plan((c, t) =>
            {
                var (existing, edges) = LoadGraph(c, t);
                foreach (var d in defs)
                    edges[d.Id] = d.DependsOn;
                foreach (var d in defs)
                    if (d.DependsOn.FirstOrDefault(dep => !seen.Contains(dep) && !existing.Contains(dep)) is { } missing)
                        throw new ArgumentException($"{d.Id} depends on unknown task {missing}");
                if (FindCycle(edges) is { } cycle)
                    throw new ArgumentException("dependency cycle: " + string.Join(" -> ", cycle));
                var stamp = Stamp();
                List<string> added = [], updated = [], unchanged = [];
                for (var i = 0; i < defs.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var d = defs[i];
                    if (!existing.Contains(d.Id))
                    { Insert(c, t, d, i, source, stamp, "imported", "import", source); added.Add(d.Id); }
                    else if (UpdateDefinition(c, t, d, i, source, stamp))
                        updated.Add(d.Id);
                    else
                        unchanged.Add(d.Id);
                }
                foreach (var d in defs)
                    ReplaceDependencies(c, t, d);
                return new TaskImportResult(added, updated, unchanged, existing.Where(id => !seen.Contains(id)).Order(StringComparer.Ordinal).ToArray(), RecomputeReadiness(c, t));
            }, dryRun, cancellationToken);
    }

    private static (HashSet<string> Existing, Dictionary<string, IReadOnlyList<string>> Edges) LoadGraph(SqliteConnection c, SqliteTransaction t)
    {
        HashSet<string> existing = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<string>> edges = new(StringComparer.Ordinal);
        using (var command = Command(c, t, "SELECT id FROM tasks"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                existing.Add(reader.GetString(0));
        Dictionary<string, List<string>> lists = new(StringComparer.Ordinal);
        using (var command = Command(c, t, "SELECT task_id,depends_on FROM task_dependencies"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!lists.TryGetValue(id, out var deps))
                    lists[id] = deps = [];
                deps.Add(reader.GetString(1));
            }
        foreach (var (id, deps) in lists)
            edges[id] = deps;
        return (existing, edges);
    }

    // Iterative DFS avoids a process stack overflow on large imported dependency chains.
    private static IReadOnlyList<string>? FindCycle(Dictionary<string, IReadOnlyList<string>> edges)
    {
        Dictionary<string, int> colors = new(StringComparer.Ordinal);
        List<string> path = [];
        foreach (var node in edges.Keys.Order(StringComparer.Ordinal))
        {
            if (colors.GetValueOrDefault(node) is not 0)
                continue;
            Stack<(string Node, bool Exit)> pending = new();
            pending.Push((node, false));
            while (pending.TryPop(out var entry))
            {
                if (entry.Exit)
                { colors[entry.Node] = 2; path.RemoveAt(path.Count - 1); continue; }
                if (colors.GetValueOrDefault(entry.Node) is 2)
                    continue;
                if (colors.GetValueOrDefault(entry.Node) is 1)
                    return [.. path.Skip(path.IndexOf(entry.Node)), entry.Node];
                colors[entry.Node] = 1;
                path.Add(entry.Node);
                pending.Push((entry.Node, true));
                if (edges.TryGetValue(entry.Node, out var deps))
                    foreach (var dep in deps.OrderDescending(StringComparer.Ordinal))
                        pending.Push((dep, false));
            }
        }
        return null;
    }

    private static void Insert(SqliteConnection c, SqliteTransaction t, TaskDefinition d, int ordinal, string source, string stamp, string kind, string actor, string detail)
    {
        var status = d.Status is "done" or "blocked" ? d.Status : "backlog";
        Execute(c, t, """
            INSERT INTO tasks(id,milestone,title,type,priority,status,goal,scope,validation,notes,blocked_reason,completion_summary,completed_by,completed_at,source,ordinal,created_at,updated_at)
            VALUES($id,$milestone,$title,$type,$priority,$status,$goal,$scope,$validation,$notes,$blocked,$summary,$by,$done,$source,$ordinal,$stamp,$stamp)
            """, ("$id", d.Id), ("$milestone", d.Milestone), ("$title", d.Title), ("$type", d.Type), ("$priority", d.Priority), ("$status", status),
            ("$goal", d.Goal), ("$scope", Json(d.Scope)), ("$validation", Json(d.Validation)), ("$notes", d.Notes),
            ("$blocked", status is "blocked" ? "imported as blocked" : null), ("$summary", status is "done" ? "imported as done from " + source : null),
            ("$by", status is "done" ? "import" : null), ("$done", status is "done" ? stamp : null), ("$source", source), ("$ordinal", ordinal), ("$stamp", stamp));
        for (var i = 0; i < d.Acceptance.Count; i++)
            InsertCriterion(c, t, d.Id, i + 1, d.Acceptance[i], status is "done" ? "n/a" : "pending", status is "done" ? "imported as done; see completion notes" : "");
        AddEvent(c, t, d.Id, kind, "", status, actor, detail, stamp);
    }

    private static void InsertCriterion(SqliteConnection c, SqliteTransaction t, string id, int index, string text, string result, string evidence) =>
        Execute(c, t, "INSERT INTO task_acceptance(task_id,idx,criterion,result,evidence) VALUES($id,$idx,$text,$result,$evidence)",
            ("$id", id), ("$idx", index), ("$text", text), ("$result", result), ("$evidence", Null(evidence)));

    private static bool UpdateDefinition(SqliteConnection c, SqliteTransaction t, TaskDefinition d, int ordinal, string source, string stamp)
    {
        var cur = Get(c, t, d.Id);
        var old = cur.Definition;
        var same = old.Milestone == d.Milestone && old.Title == d.Title && old.Type == d.Type && old.Priority == d.Priority && old.Goal == d.Goal &&
            old.Scope.SequenceEqual(d.Scope) && old.Validation.SequenceEqual(d.Validation) && old.DependsOn.SequenceEqual(d.DependsOn) && old.Acceptance.SequenceEqual(d.Acceptance);
        if (same)
        {
            if (cur.Ordinal != ordinal)
                Execute(c, t, "UPDATE tasks SET ordinal=$ordinal WHERE id=$id", ("$ordinal", ordinal), ("$id", d.Id));
            return false;
        }
        Execute(c, t, """
            UPDATE tasks SET milestone=$milestone,title=$title,type=$type,priority=$priority,goal=$goal,scope=$scope,validation=$validation,source=$source,ordinal=$ordinal,updated_at=$stamp WHERE id=$id
            """, ("$milestone", d.Milestone), ("$title", d.Title), ("$type", d.Type), ("$priority", d.Priority), ("$goal", d.Goal), ("$scope", Json(d.Scope)),
            ("$validation", Json(d.Validation)), ("$source", source), ("$ordinal", ordinal), ("$stamp", stamp), ("$id", d.Id));
        if (!old.Acceptance.SequenceEqual(d.Acceptance))
        {
            var byText = cur.Criteria.GroupBy(x => x.Criterion, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            Execute(c, t, "DELETE FROM task_acceptance WHERE task_id=$id", ("$id", d.Id));
            for (var i = 0; i < d.Acceptance.Count; i++)
            {
                byText.TryGetValue(d.Acceptance[i], out var previous);
                InsertCriterion(c, t, d.Id, i + 1, d.Acceptance[i], previous?.Result ?? "pending", previous?.Evidence ?? "");
            }
        }
        AddEvent(c, t, d.Id, "definition_updated", "", "", "import", "definition updated from " + source + (cur.Status is "done" ? " (task already done; completion kept)" : ""), stamp);
        return true;
    }
    private static void ReplaceDependencies(SqliteConnection c, SqliteTransaction t, TaskDefinition d)
    {
        Execute(c, t, "DELETE FROM task_dependencies WHERE task_id=$id", ("$id", d.Id));
        foreach (var dep in d.DependsOn)
            Execute(c, t, "INSERT INTO task_dependencies(task_id,depends_on) VALUES($id,$dep)", ("$id", d.Id), ("$dep", dep));
    }
}
