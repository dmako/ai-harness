using Harness.Core.Retrieval;
using Harness.Core.Telemetry;

namespace Harness.Infrastructure.Telemetry;

public sealed partial class TelemetryStore
{
    private sealed record Outcome(long Call, string Path, string LanguageName, bool Hit, string From);

    private sealed record ScoredUnit(
        long Call,
        string Provider,
        string Capability,
        string Path,
        int Tier,
        int Position
    );

    public ContextStatistics ContextStats(DateTimeOffset since)
    {
        var from = Stamp(since);
        var stats = new ContextStatistics
        {
            Open = Int(
                Query("SELECT COUNT(*) FROM context_calls WHERE resolved_at IS NULL AND at >= $from", ("$from", from))[
                    0
                ][0]
            ),
        };
        var changes = Query(
                "SELECT o.call_id,o.path,o.language,o.hit,COALESCE(o.called_from,'') FROM context_outcomes o JOIN context_calls c ON c.id=o.call_id WHERE c.at >= $from ORDER BY o.call_id",
                ("$from", from)
            )
            .Select(r => new Outcome(Long(r[0]), Text(r[1]), Text(r[2]), Int(r[3]) is not 0, Text(r[4])))
            .ToArray();
        var answers = changes.GroupBy(r => r.Call).ToArray();
        stats.Calls = answers.Length;
        var work = answers
            .Select(g => g.Where(r => !TaskParsing.IsTestFile(r.Path)).ToArray())
            .Where(a => a.Length > 0)
            .ToArray();
        stats.Changed = work.Sum(a => a.Length);
        stats.Hits = work.Sum(a => a.Count(r => r.Hit));
        stats.HitRate = work.Length is 0 ? 0 : work.Average(a => Rate(a.Count(r => r.Hit), a.Length));
        var tests = changes.Where(r => TaskParsing.IsTestFile(r.Path)).ToArray();
        stats.Tests = [tests.Count(r => r.Hit), tests.Length];
        stats.CalleeMisses = changes.Count(r => !r.Hit && r.From.Length > 0);
        stats.Language = changes
            .GroupBy(r => r.LanguageName)
            .ToDictionary(g => g.Key, g => new[] { g.Count(r => r.Hit), g.Count() }, StringComparer.Ordinal);
        var misses = changes
            .Where(r => !r.Hit)
            .GroupBy(r => r.Path)
            .Select(g => new ContextMiss
            {
                Path = g.Key,
                Language = g.First().LanguageName,
                Times = g.Count(),
                CalledFrom = g.Select(r => r.From).Order(StringComparer.Ordinal).Last() is { Length: > 0 } caller
                    ? caller
                    : null,
            })
            .OrderByDescending(m => m.Times)
            .ThenBy(m => m.Path, StringComparer.Ordinal)
            .Take(15)
            .ToArray();
        stats.Misses = misses.Length is 0 ? null : misses;
        var units = Query(
                "SELECT u.call_id,u.provider,u.capability,u.path,u.tier,u.position FROM context_units u JOIN context_calls c ON c.id=u.call_id WHERE c.at >= $from ORDER BY u.call_id,u.position",
                ("$from", from)
            )
            .Select(r => new ScoredUnit(Long(r[0]), Text(r[1]), Text(r[2]), Text(r[3]), Int(r[4]), Int(r[5])))
            .ToArray();
        ProviderRows(stats, units, changes);
        stats.AfterRead = ReadAfter(from);
        return stats;
    }

    private static void ProviderRows(ContextStatistics stats, ScoredUnit[] units, Outcome[] changes)
    {
        var byCall = changes.GroupBy(c => c.Call).ToDictionary(g => g.Key, g => g.ToArray());
        Dictionary<(string Provider, string Capability, string Language), Tally> rows = [];
        Dictionary<string, Tally> providers = new(StringComparer.Ordinal);
        foreach (var call in units.GroupBy(u => u.Call))
        {
            var outcomes = byCall.GetValueOrDefault(call.Key) ?? [];
            foreach (var pc in call.Select(u => (u.Provider, u.Capability)).Distinct())
            {
                foreach (var change in outcomes)
                {
                    var key = (pc.Provider, pc.Capability, change.LanguageName);
                    if (!rows.TryGetValue(key, out var row))
                    {
                        row = new();
                        rows.Add(key, row);
                    }
                    if (!providers.TryGetValue(pc.Provider, out var provider))
                    {
                        provider = new();
                        providers.Add(pc.Provider, provider);
                    }
                    row.Changed.Add((call.Key, change.Path));
                    provider.Changed.Add((call.Key, change.Path));
                }
            }

            var changed = outcomes.ToLookup(o => o.Path, StringComparer.Ordinal);
            foreach (var unit in call)
            {
                foreach (var change in changed[unit.Path])
                {
                    var row = rows[(unit.Provider, unit.Capability, change.LanguageName)];
                    row.Hits.Add((call.Key, unit.Path));
                    if (unit.Tier is 0)
                    {
                        row.Tier0.Add((call.Key, unit.Path));
                    }

                    row.Positions.Add(unit.Position);
                    providers[unit.Provider].Hits.Add((call.Key, unit.Path));
                }
            }
        }
        var result = rows.Select(pair =>
            {
                var (key, row) = pair;
                row.Positions.Sort();
                return new ContextProviderRow
                {
                    Provider = key.Provider,
                    Capability = key.Capability,
                    Language = key.Language,
                    Changed = row.Changed.Count,
                    Hits = row.Hits.Count,
                    HitRate = Rate(row.Hits.Count, row.Changed.Count),
                    BestTier0 = row.Tier0.Count,
                    MedianPos = row.Positions.Count is 0 ? 0 : row.Positions[row.Positions.Count / 2],
                };
            })
            .OrderByDescending(r => r.HitRate)
            .ThenBy(r => r.Provider + r.Capability + r.Language, StringComparer.Ordinal)
            .ToArray();
        stats.Rows = result.Length is 0 ? null : result;
        stats.Providers = providers.ToDictionary(
            p => p.Key,
            p => new[] { p.Value.Hits.Count, p.Value.Changed.Count },
            StringComparer.Ordinal
        );
    }

    private sealed class Tally
    {
        public HashSet<(long, string)> Changed { get; } = [];
        public HashSet<(long, string)> Hits { get; } = [];
        public HashSet<(long, string)> Tier0 { get; } = [];
        public List<int> Positions { get; } = [];
    }

    private AfterRead ReadAfter(string from)
    {
        var result = new AfterRead();
        var calls = Query(
            "SELECT id,at,session_id FROM context_calls WHERE session_id IS NOT NULL AND at >= $from ORDER BY id",
            ("$from", from)
        );
        result.Linked = calls.Count;
        foreach (var session in calls.GroupBy(r => Text(r[2])))
        {
            var ordered = session
                .OrderBy(r => DateTimeOffset.Parse(Text(r[1]), System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            var events = Query(
                    "SELECT at,path,kind FROM tool_events WHERE session_id=$session AND path != '' AND kind IN ('read','edit') AND at >= $from",
                    ("$session", session.Key),
                    (
                        "$from",
                        Stamp(
                            DateTimeOffset
                                .Parse(Text(ordered[0][1]), System.Globalization.CultureInfo.InvariantCulture)
                                .AddSeconds(-1)
                        )
                    )
                )
                .Select(r =>
                    (
                        At: DateTimeOffset.Parse(Text(r[0]), System.Globalization.CultureInfo.InvariantCulture),
                        Path: Text(r[1]),
                        Edited: Text(r[2]) is "edit"
                    )
                )
                .OrderBy(e => e.At)
                .ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                var at = DateTimeOffset.Parse(Text(ordered[i][1]), System.Globalization.CultureInfo.InvariantCulture);
                var end = at.AddHours(2);
                if (i + 1 < ordered.Length)
                {
                    var next = DateTimeOffset.Parse(
                        Text(ordered[i + 1][1]),
                        System.Globalization.CultureInfo.InvariantCulture
                    );
                    if (next < end)
                    {
                        end = next;
                    }
                }
                var touched = events
                    .Where(e => e.At > at && e.At < end)
                    .GroupBy(e => e.Path, StringComparer.Ordinal)
                    .ToArray();
                if (touched.Length is 0)
                {
                    continue;
                }

                var paths = Query(
                        "SELECT DISTINCT path FROM context_units WHERE call_id=$id",
                        ("$id", Long(ordered[i][0]))
                    )
                    .Select(r => Text(r[0]))
                    .ToHashSet(StringComparer.Ordinal);
                result.Calls++;
                foreach (var group in touched)
                {
                    var edited = group.Any(e => e.Edited);
                    result.Touched++;
                    if (edited)
                    {
                        result.Edited++;
                    }

                    if (paths.Contains(group.Key))
                    {
                        result.Hits++;
                        if (edited)
                        {
                            result.EditHits++;
                        }
                    }
                }
            }
        }
        result.HitRate = Rate(result.Hits, result.Touched);
        return result;
    }
}
