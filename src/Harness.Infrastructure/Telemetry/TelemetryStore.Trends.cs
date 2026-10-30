using System.Globalization;
using Harness.Core.Retrieval;
using Harness.Core.Telemetry;

namespace Harness.Infrastructure.Telemetry;

public sealed partial class TelemetryStore
{
    /// <summary>The last <paramref name="days"/> days up to <paramref name="now"/>, oldest first, every day present.</summary>
    public IReadOnlyList<TrendDay> Trends(int days, DateTimeOffset now)
    {
        var first = now.UtcDateTime.Date.AddDays(1 - days);
        var names = Enumerable
            .Range(0, days)
            .Select(i => first.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToArray();
        var from = Stamp(new DateTimeOffset(first, TimeSpan.Zero));
        // each answer's share of the changed code files it held, by the day it was asked
        var rates = Query(
                "SELECT c.id,substr(c.at,1,10),o.path,o.hit FROM context_outcomes o JOIN context_calls c ON c.id=o.call_id WHERE c.at >= $from",
                ("$from", from)
            )
            .Where(r => !TaskParsing.IsTestFile(Text(r[2])))
            .GroupBy(r => (Call: Long(r[0]), Day: Text(r[1])))
            .Select(g => (g.Key.Day, Rate: Rate(g.Count(r => Int(r[3]) is not 0), g.Count())))
            .ToLookup(a => a.Day, a => a.Rate, StringComparer.Ordinal);
        var done = Query(
                "SELECT substr(at,1,10),COUNT(DISTINCT task_id) FROM task_events WHERE at >= $from AND (event='completed' OR to_status='done') GROUP BY 1",
                ("$from", from)
            )
            .ToDictionary(r => Text(r[0]), r => Int(r[1]), StringComparer.Ordinal);
        return names
            .Select(day =>
            {
                var answers = rates[day].ToArray();
                return new TrendDay
                {
                    Day = day,
                    Answers = answers.Length,
                    HitRate = answers.Length is 0 ? 0 : answers.Average(),
                    Done = done.GetValueOrDefault(day),
                };
            })
            .ToArray();
    }
}
