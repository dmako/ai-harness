using Harness.Core.Tasks;

namespace Harness.Infrastructure.Tasks;

public sealed partial class TaskStore
{
    public int ImportMilestones(IReadOnlyList<MilestoneDefinition> definitions, string source, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var defs = definitions.Select(d => d with { Id = d.Id.Trim(), Title = d.Title.Trim(), Exit = d.Exit ?? [] }).ToArray();
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (var i = 0; i < defs.Length; i++)
        {
            if (defs[i].Id.Length is 0)
            {
                throw new ArgumentException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"milestone {i + 1}: id is required"));
            }

            if (!seen.Add(defs[i].Id))
            {
                throw new ArgumentException($"milestone {defs[i].Id} is defined twice");
            }
        }
        var changed = 0;
        return Plan((c, t) =>
            {
                var stamp = Stamp();
                for (var i = 0; i < defs.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var d = defs[i];
                    var same = false;
                    var ordinal = -1;
                    using (var command = Command(c, t, "SELECT title,goal,exit,notes,ordinal FROM milestones WHERE id=$id", ("$id", d.Id)))
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            same = reader.GetString(0) == d.Title && reader.GetString(1) == d.Goal && reader.GetString(2) == Json(d.Exit) && reader.GetString(3) == d.Notes;
                            ordinal = reader.GetInt32(4);
                        }
                    }
                    if (same)
                    {
                        if (ordinal != i)
                        {
                            Execute(c, t, "UPDATE milestones SET ordinal=$ordinal WHERE id=$id", ("$ordinal", i), ("$id", d.Id));
                        }

                        continue;
                    }
                    changed++;
                    Execute(c, t, """
                        INSERT INTO milestones(id,title,goal,exit,notes,source,ordinal,updated_at) VALUES($id,$title,$goal,$exit,$notes,$source,$ordinal,$stamp)
                        ON CONFLICT(id) DO UPDATE SET title=excluded.title,goal=excluded.goal,exit=excluded.exit,notes=excluded.notes,source=excluded.source,ordinal=excluded.ordinal,updated_at=excluded.updated_at
                        """, ("$id", d.Id), ("$title", d.Title), ("$goal", d.Goal), ("$exit", Json(d.Exit)), ("$notes", d.Notes), ("$source", source), ("$ordinal", i), ("$stamp", stamp));
                }
                return changed;
            }, dryRun, cancellationToken);
    }

    // Unlike task reads, milestone progress does not refresh leases/readiness in the Go contract.
    public IReadOnlyList<MilestoneProgress> Milestones(CancellationToken cancellationToken = default) => database.ReadTransaction((c, t) =>
    {
        Dictionary<string, MilestoneProgress> byId = new(StringComparer.Ordinal);
        List<string> order = [];
        using (var command = Command(c, t, "SELECT id,title,goal,exit,notes FROM milestones ORDER BY ordinal,id"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var d = new MilestoneDefinition(reader.GetString(0), reader.GetString(1), reader.GetString(2), System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [], reader.GetString(4));
                byId[d.Id] = new(d, true, new Dictionary<string, int>(StringComparer.Ordinal));
                order.Add(d.Id);
            }
        }

        using (var command = Command(c, t, "SELECT milestone,status,COUNT(*),MIN(ordinal) FROM tasks WHERE milestone<>'' GROUP BY milestone,status ORDER BY MIN(ordinal)"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!byId.TryGetValue(id, out var m))
                {
                    byId[id] = m = new(new(id, "", Exit: []), false, new Dictionary<string, int>(StringComparer.Ordinal));
                    order.Add(id);
                }
                ((Dictionary<string, int>)m.Tasks)[reader.GetString(1)] = reader.GetInt32(2);
            }
        }

        return (IReadOnlyList<MilestoneProgress>)order.Select(id => byId[id]).ToArray();
    }, cancellationToken);
}
