using System.Globalization;
using System.Text.RegularExpressions;
using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Tasks;

public sealed partial class TaskStore
{
    public int ImportBlueprints(
        IReadOnlyList<BlueprintDefinition> definitions,
        string source,
        bool dryRun = false,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var defs = definitions
            .Select(d => d with { Id = d.Id.Trim(), Title = d.Title.Trim(), Exit = d.Exit ?? [] })
            .ToArray();
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (var i = 0; i < defs.Length; i++)
        {
            if (defs[i].Id.Length is 0)
            {
                throw new ArgumentException(
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"blueprint {i + 1}: id is required"
                    )
                );
            }

            if (!seen.Add(defs[i].Id))
            {
                throw new ArgumentException($"blueprint {defs[i].Id} is defined twice");
            }
        }
        var changed = 0;
        return Plan(
            (c, t) =>
            {
                var stamp = Stamp();
                for (var i = 0; i < defs.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var d = defs[i];
                    var same = false;
                    var ordinal = -1;
                    using (
                        var command = Command(
                            c,
                            t,
                            "SELECT title,goal,exit,notes,ordinal FROM blueprints WHERE id=$id",
                            ("$id", d.Id)
                        )
                    )
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            same =
                                reader.GetString(0) == d.Title
                                && reader.GetString(1) == d.Goal
                                && reader.GetString(2) == Json(d.Exit)
                                && reader.GetString(3) == d.Notes;
                            ordinal = reader.GetInt32(4);
                        }
                    }
                    if (same)
                    {
                        if (ordinal != i)
                        {
                            Execute(
                                c,
                                t,
                                "UPDATE blueprints SET ordinal=$ordinal WHERE id=$id",
                                ("$ordinal", i),
                                ("$id", d.Id)
                            );
                        }

                        continue;
                    }
                    changed++;
                    Execute(
                        c,
                        t,
                        """
                        INSERT INTO blueprints(id,title,goal,exit,notes,source,ordinal,updated_at) VALUES($id,$title,$goal,$exit,$notes,$source,$ordinal,$stamp)
                        ON CONFLICT(id) DO UPDATE SET title=excluded.title,goal=excluded.goal,exit=excluded.exit,notes=excluded.notes,source=excluded.source,ordinal=excluded.ordinal,updated_at=excluded.updated_at
                        """,
                        ("$id", d.Id),
                        ("$title", d.Title),
                        ("$goal", d.Goal),
                        ("$exit", Json(d.Exit)),
                        ("$notes", d.Notes),
                        ("$source", source),
                        ("$ordinal", i),
                        ("$stamp", stamp)
                    );
                }
                return changed;
            },
            dryRun,
            cancellationToken
        );
    }

    // Unlike task reads, blueprint progress does not refresh leases or readiness.
    public IReadOnlyList<BlueprintProgress> Blueprints(CancellationToken cancellationToken = default) =>
        database.ReadTransaction(
            (c, t) =>
            {
                Dictionary<string, BlueprintProgress> byId = new(StringComparer.Ordinal);
                List<string> order = [];
                using (
                    var command = Command(c, t, "SELECT id,title,goal,exit,notes FROM blueprints ORDER BY ordinal,id")
                )
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var d = new BlueprintDefinition(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            HarnessJson.Deserialize<string[]>(reader.GetString(3)) ?? [],
                            reader.GetString(4)
                        );
                        byId[d.Id] = new(d, true, new Dictionary<string, int>(StringComparer.Ordinal));
                        order.Add(d.Id);
                    }
                }

                using (
                    var command = Command(
                        c,
                        t,
                        "SELECT blueprint,status,COUNT(*),MIN(ordinal) FROM tasks WHERE blueprint<>'' GROUP BY blueprint,status ORDER BY MIN(ordinal)"
                    )
                )
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var id = reader.GetString(0);
                        if (!byId.TryGetValue(id, out var m))
                        {
                            byId[id] = m = new(
                                new(id, "", Exit: []),
                                false,
                                new Dictionary<string, int>(StringComparer.Ordinal)
                            );
                            order.Add(id);
                        }
                        ((Dictionary<string, int>)m.Tasks)[reader.GetString(1)] = reader.GetInt32(2);
                    }
                }

                return (IReadOnlyList<BlueprintProgress>)order.Select(id => byId[id]).ToArray();
            },
            cancellationToken
        );

    /// <summary>
    /// The next free blueprint id: the number after the highest one of the last numbered blueprint's prefix
    /// (M16 → M17), or BP-001 while none is numbered.
    /// </summary>
    public string NextBlueprintId(CancellationToken cancellationToken = default) =>
        database.ReadTransaction((c, t) => NextBlueprintId(BlueprintIds(c, t)), cancellationToken);

    private static string NextBlueprintId(IReadOnlyList<string> ids)
    {
        var numbered = ids.Select(id => NumberedId.Match(id)).Where(m => m.Success).ToArray();
        if (numbered.Length is 0)
        {
            return "BP-001";
        }

        var prefix = numbered[^1].Groups[1].Value;
        var same = numbered.Where(m => m.Groups[1].Value == prefix).ToArray();
        var next = same.Max(m => decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) + 1;
        var width = same.Max(m => m.Groups[2].Value.Length);
        return prefix + next.ToString(new string('0', width), CultureInfo.InvariantCulture);
    }

    // Described blueprints by ordinal, then those only tasks name, in task order.
    private static List<string> BlueprintIds(SqliteConnection c, SqliteTransaction t)
    {
        List<string> ids = [];
        using var command = Command(
            c,
            t,
            """
            SELECT id FROM (SELECT id,0 AS kind,ordinal FROM blueprints
            UNION ALL SELECT blueprint,1,MIN(ordinal) FROM tasks WHERE blueprint<>'' AND blueprint NOT IN (SELECT id FROM blueprints) GROUP BY blueprint)
            ORDER BY kind,ordinal,id
            """
        );
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>
    /// The blueprint an agent named: its id, matched exactly, then ignoring case, then by title; a name that matches
    /// none stays as given (the task starts that blueprint).
    /// </summary>
    private static string ResolveBlueprint(SqliteConnection c, SqliteTransaction t, string name)
    {
        name = name.Trim();
        if (name.Length is 0)
        {
            return "";
        }

        var ids = BlueprintIds(c, t);
        if (ids.Contains(name))
        {
            return name;
        }

        if (ids.Find(id => string.Equals(id, name, StringComparison.OrdinalIgnoreCase)) is { } id)
        {
            return id;
        }

        using var command = Command(
            c,
            t,
            "SELECT id FROM blueprints WHERE title=$name COLLATE NOCASE ORDER BY ordinal,id LIMIT 1",
            ("$name", name)
        );
        return command.ExecuteScalar() as string ?? name;
    }

    /// <summary>Describes a new blueprint, placed after the others.</summary>
    public BlueprintDefinition CreateBlueprint(
        BlueprintDefinition definition,
        string source,
        CancellationToken cancellationToken = default
    ) => database.WithTransaction((c, t) => CreateBlueprint(c, t, definition, source), cancellationToken);

    private BlueprintDefinition CreateBlueprint(
        SqliteConnection c,
        SqliteTransaction t,
        BlueprintDefinition definition,
        string source
    )
    {
        var d = CleanBlueprint(definition);
        using (var exists = Command(c, t, "SELECT 1 FROM blueprints WHERE id=$id", ("$id", d.Id)))
        {
            if (exists.ExecuteScalar() is not null)
            {
                throw new InvalidOperationException($"blueprint {d.Id} already exists");
            }
        }

        SaveBlueprint(c, t, d, source);
        return d;
    }

    /// <summary>
    /// Restates a blueprint's title, goal, exit criteria and notes; its id stays. A blueprint only tasks named
    /// becomes described.
    /// </summary>
    public BlueprintDefinition EditBlueprint(
        string id,
        BlueprintDefinition definition,
        string source,
        CancellationToken cancellationToken = default
    ) => database.WithTransaction((c, t) => EditBlueprint(c, t, id, definition, source), cancellationToken);

    private BlueprintDefinition EditBlueprint(
        SqliteConnection c,
        SqliteTransaction t,
        string id,
        BlueprintDefinition definition,
        string source
    )
    {
        var d = CleanBlueprint(definition with { Id = id });
        if (!BlueprintIds(c, t).Contains(d.Id))
        {
            throw new ArgumentException($"unknown blueprint {d.Id}");
        }

        SaveBlueprint(c, t, d, source);
        return d;
    }

    private static BlueprintDefinition CleanBlueprint(BlueprintDefinition d)
    {
        d = d with
        {
            Id = d.Id.Trim(),
            Title = d.Title.Trim(),
            Goal = d.Goal.Trim(),
            Exit = (d.Exit ?? []).Select(e => e.Trim()).Where(e => e.Length is not 0).ToArray(),
            Notes = d.Notes.Trim(),
        };
        if (d.Id.Length is 0)
        {
            throw new ArgumentException("a blueprint needs an id");
        }

        if (d.Id.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"blueprint id {TaskWorkflow.Quote(d.Id)} has whitespace");
        }

        return d.Title.Length is 0 ? throw new ArgumentException($"blueprint {d.Id} needs a title") : d;
    }

    private void SaveBlueprint(SqliteConnection c, SqliteTransaction t, BlueprintDefinition d, string source) =>
        Execute(
            c,
            t,
            """
            INSERT INTO blueprints(id,title,goal,exit,notes,source,ordinal,updated_at)
            VALUES($id,$title,$goal,$exit,$notes,$source,(SELECT COALESCE(MAX(ordinal),-1)+1 FROM blueprints),$stamp)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title,goal=excluded.goal,exit=excluded.exit,notes=excluded.notes,source=excluded.source,updated_at=excluded.updated_at
            """,
            ("$id", d.Id),
            ("$title", d.Title),
            ("$goal", d.Goal),
            ("$exit", Json(d.Exit)),
            ("$notes", d.Notes),
            ("$source", source),
            ("$stamp", Stamp())
        );

    /// <summary>
    /// Removes a blueprint. Its tasks, if any, are moved to <paramref name="moveTo"/> ("" for none) or deleted;
    /// with neither the removal is refused. Deleting is refused while an agent holds one of them, and tasks outside
    /// that depended on a deleted one lose that dependency.
    /// </summary>
    public BlueprintRemoval RemoveBlueprint(
        string id,
        BlueprintTasks tasks,
        string moveTo,
        string by,
        CancellationToken cancellationToken = default
    ) =>
        database.WithTransaction(
            (c, t) =>
            {
                Refresh(c, t);
                id = id.Trim();
                moveTo = moveTo.Trim();
                var known = BlueprintIds(c, t);
                if (!known.Contains(id))
                {
                    throw new ArgumentException($"unknown blueprint {id}");
                }

                var mine = BlueprintTaskStates(c, t, id);
                var stamp = Stamp();
                IReadOnlyList<string> moved = [],
                    deleted = [],
                    unlinked = [];
                if (mine.Count is not 0)
                {
                    switch (tasks)
                    {
                        case BlueprintTasks.Move:
                            if (moveTo == id || (moveTo.Length is not 0 && !known.Contains(moveTo)))
                            {
                                throw new ArgumentException(
                                    moveTo == id
                                        ? $"cannot move the tasks of {id} to itself"
                                        : $"unknown blueprint {moveTo}"
                                );
                            }

                            moved = MoveTasks(c, t, mine, id, moveTo, by, stamp);
                            break;
                        case BlueprintTasks.Delete:
                            if (mine.FirstOrDefault(x => TaskWorkflow.HoldsClaim(x.Status)) is { Id: not null } held)
                            {
                                throw new InvalidOperationException(
                                    $"{held.Id} is {held.Status}: the tasks cannot be deleted while an agent holds one"
                                );
                            }

                            deleted = mine.Select(x => x.Id).ToArray();
                            unlinked = DeleteTasks(c, t, deleted, id, by, stamp);
                            break;
                        default:
                            throw new InvalidOperationException(
                                string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"blueprint {id} has {mine.Count} task(s): move them or delete them"
                                )
                            );
                    }
                }

                Execute(c, t, "DELETE FROM blueprints WHERE id=$id", ("$id", id));
                RecomputeReadiness(c, t);
                return new BlueprintRemoval(id, moved, deleted, unlinked);
            },
            cancellationToken
        );

    /// <summary>
    /// Moves one task to another blueprint (empty: none), whatever its status, with the move in its audit trail: a
    /// drag between blueprints in harness ui.
    /// </summary>
    public TaskRecord MoveToBlueprint(
        string id,
        string blueprint,
        string by,
        CancellationToken cancellationToken = default
    ) =>
        database.WithTransaction(
            (c, t) =>
            {
                var task = Get(c, t, id);
                var to = blueprint.Trim();
                var from = task.Definition.Blueprint;
                if (to == from)
                {
                    return task;
                }

                if (to.Length is not 0 && !BlueprintIds(c, t).Contains(to))
                {
                    throw new ArgumentException($"unknown blueprint {to}");
                }

                var stamp = Stamp();
                Execute(
                    c,
                    t,
                    "UPDATE tasks SET blueprint=$to,updated_at=$stamp WHERE id=$id",
                    ("$to", to),
                    ("$stamp", stamp),
                    ("$id", task.Id)
                );
                AddEvent(
                    c,
                    t,
                    task.Id,
                    "moved",
                    task.Status,
                    task.Status,
                    by,
                    $"moved from {(from.Length is 0 ? "no blueprint" : from)} to {(to.Length is 0 ? "no blueprint" : to)}",
                    stamp
                );
                return Get(c, t, task.Id);
            },
            cancellationToken
        );

    private static List<(string Id, string Status)> BlueprintTaskStates(
        SqliteConnection c,
        SqliteTransaction t,
        string id
    )
    {
        List<(string Id, string Status)> tasks = [];
        using var command = Command(
            c,
            t,
            "SELECT id,status FROM tasks WHERE blueprint=$id ORDER BY ordinal,id",
            ("$id", id)
        );
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tasks.Add((reader.GetString(0), reader.GetString(1)));
        }

        return tasks;
    }

    private static string[] MoveTasks(
        SqliteConnection c,
        SqliteTransaction t,
        IReadOnlyList<(string Id, string Status)> tasks,
        string from,
        string to,
        string by,
        string stamp
    )
    {
        foreach (var (task, status) in tasks)
        {
            Execute(
                c,
                t,
                "UPDATE tasks SET blueprint=$to,updated_at=$stamp WHERE id=$id",
                ("$to", to),
                ("$stamp", stamp),
                ("$id", task)
            );
            AddEvent(
                c,
                t,
                task,
                "moved",
                status,
                status,
                by,
                $"blueprint {from} removed; moved to {(to.Length is 0 ? "no blueprint" : to)}",
                stamp
            );
        }

        return tasks.Select(x => x.Id).ToArray();
    }

    // Deletes the tasks with their criteria, edges and audit trail. Tasks outside the set that depended on one lose
    // that dependency, recorded in their own trail; they are returned.
    private static string[] DeleteTasks(
        SqliteConnection c,
        SqliteTransaction t,
        IReadOnlyList<string> ids,
        string blueprint,
        string by,
        string stamp
    )
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        List<(string Task, string On)> outside = [];
        using (
            var command = Command(c, t, "SELECT task_id,depends_on FROM task_dependencies ORDER BY task_id,depends_on")
        )
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var (task, on) = (reader.GetString(0), reader.GetString(1));
                if (set.Contains(on) && !set.Contains(task))
                {
                    outside.Add((task, on));
                }
            }
        }

        foreach (var (task, on) in outside)
        {
            Execute(
                c,
                t,
                "DELETE FROM task_dependencies WHERE task_id=$task AND depends_on=$on",
                ("$task", task),
                ("$on", on)
            );
            AddEvent(
                c,
                t,
                task,
                "dependency removed",
                "",
                "",
                by,
                $"{on} was deleted with blueprint {blueprint}",
                stamp
            );
        }

        string[] statements =
        [
            "DELETE FROM task_dependencies WHERE task_id=$id OR depends_on=$id",
            "DELETE FROM task_acceptance WHERE task_id=$id",
            "DELETE FROM task_events WHERE task_id=$id",
            "DELETE FROM tasks WHERE id=$id",
        ];
        foreach (var id in ids)
        {
            foreach (var sql in statements)
            {
                Execute(c, t, sql, ("$id", id));
            }
        }

        return outside.Select(x => x.Task).Distinct(StringComparer.Ordinal).ToArray();
    }

    [GeneratedRegex(@"\A(.*?)([0-9]+)\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex NumberedId { get; }
}
