using System.Globalization;
using Harness.Core.Telemetry;
using Harness.Infrastructure.Findings;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Telemetry;

public sealed partial class TelemetryStore
{
    private List<object?[]> Query(string sql, params (string Key, object Value)[] args)
    {
        using var c = database.OpenConnection();
        using var command = FindingStore.Command(c, null, sql, args);
        using var reader = command.ExecuteReader();
        List<object?[]> result = [];
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            result.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }
        return result;
    }
    private static int Int(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static long Long(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);
    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    public static double Rate(int n, int of) => of is 0 ? 0 : (double)n / of;
    public static long Percentile(IEnumerable<long> values, int percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length is 0 ? 0 : sorted[Math.Clamp(((percentile * sorted.Length + 99) / 100) - 1, 0, sorted.Length - 1)];
    }
    public MetricsReport Metrics(DateTimeOffset since)
    {
        var from = Stamp(since);
        var report = new MetricsReport { Since = since };
        var hook = Query("SELECT COUNT(*),COUNT(DISTINCT session_id),MAX(at) FROM tool_events WHERE at >= $from", ("$from", from))[0];
        report.Hooks = new() { Events = Int(hook[0]), Sessions = Int(hook[1]), Last = hook[2] as string, Clients = Query("SELECT DISTINCT client FROM tool_events WHERE at >= $from ORDER BY client", ("$from", from)).Select(r => Text(r[0])).ToArray() };
        if (report.Hooks.Clients?.Count is 0)
        {
            report.Hooks.Clients = null;
        }

        report.Tools = Query("SELECT tool,kind,client,duration_ms,response_bytes,empty,error IS NOT NULL,at FROM tool_calls WHERE at >= $from ORDER BY at", ("$from", from))
            .GroupBy(r => (Tool: Text(r[0]), Kind: Text(r[1]))).Select(g =>
            {
                var rows = g.ToArray();
                var errors = rows.Count(r => Int(r[6]) is not 0);
                var empty = rows.Count(r => Int(r[6]) is 0 && Int(r[5]) is not 0);
                var clients = rows.Select(r => Text(r[2])).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return new ToolMetrics { Tool = g.Key.Tool, Kind = g.Key.Kind.Length is 0 ? null : g.Key.Kind, Calls = rows.Length, Errors = errors, Empty = empty, ErrorRate = Rate(errors, rows.Length), EmptyRate = Rate(empty, rows.Length), P50MS = Percentile(rows.Select(r => Long(r[3])), 50), P95MS = Percentile(rows.Select(r => Long(r[3])), 95), AvgBytes = (int)(rows.Sum(r => Long(r[4])) / rows.Length), Clients = clients.Length is 0 ? null : clients, Last = Text(rows[^1][7]) };
            }).OrderByDescending(r => r.Calls).ThenBy(r => r.Tool + r.Kind, StringComparer.Ordinal).ToArray();
        var shares = Query("""
            SELECT u.provider,u.capability,COUNT(DISTINCT o.call_id || '|' || o.path),COUNT(DISTINCT CASE WHEN u.path=o.path THEN o.call_id || '|' || o.path END)
            FROM context_units u JOIN context_outcomes o ON o.call_id=u.call_id JOIN context_calls c ON c.id=u.call_id WHERE c.at >= $from GROUP BY u.provider,u.capability
            """, ("$from", from)).ToDictionary(r => (Text(r[0]), Text(r[1])), r => Rate(Int(r[3]), Int(r[2])));
        report.Providers = Query("SELECT provider,capability,duration_ms,items,error IS NOT NULL,timeout,at FROM provider_calls WHERE at >= $from ORDER BY at", ("$from", from))
            .GroupBy(r => (Provider: Text(r[0]), Capability: Text(r[1]))).Select(g =>
            {
                var rows = g.ToArray();
                var errors = rows.Count(r => Int(r[4]) is not 0 || Int(r[5]) is not 0);
                var timeouts = rows.Count(r => Int(r[5]) is not 0);
                var empty = rows.Count(r => Int(r[4]) is 0 && Int(r[5]) is 0 && Int(r[3]) is 0);
                return new ProviderMetrics { Provider = g.Key.Provider, Capability = g.Key.Capability, Calls = rows.Length, Errors = errors, Timeouts = timeouts, Empty = empty, ErrorRate = Rate(errors, rows.Length), EmptyRate = Rate(empty, rows.Length), P50MS = Percentile(rows.Select(r => Long(r[2])), 50), P95MS = Percentile(rows.Select(r => Long(r[2])), 95), HitShare = shares.GetValueOrDefault(g.Key, -1), Last = Text(rows[^1][6]) };
            }).OrderBy(r => r.Provider, StringComparer.Ordinal).ThenBy(r => r.Capability, StringComparer.Ordinal).ToArray();
        return report;
    }
    public IReadOnlyList<ProviderBypass>? Bypasses(DateTimeOffset since, IReadOnlyDictionary<string, string> servers)
    {
        if (servers.Count is 0)
        {
            return null;
        }

        var rows = Query("SELECT mcp_server,mcp_tool,COUNT(*),MAX(at) FROM tool_events WHERE kind='mcp' AND at >= $from GROUP BY mcp_server,mcp_tool", ("$from", Stamp(since)))
            .Where(r => servers.ContainsKey(Text(r[0]))).Select(r => new ProviderBypass { Server = Text(r[0]), Provider = servers[Text(r[0])], Tool = Text(r[1]), Calls = Int(r[2]), Last = Text(r[3]) }).OrderByDescending(r => r.Calls).ThenBy(r => r.Server + r.Tool, StringComparer.Ordinal).ToArray();
        return rows.Length is 0 ? null : rows;
    }
    public IReadOnlyList<ProviderTrend> Trends(DateTimeOffset since, DateTimeOffset now, long slowMs = 3000)
    {
        List<string> days = [];
        for (var day = since.UtcDateTime.Date; day <= now.UtcDateTime; day = day.AddDays(1))
        {
            days.Add(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return Query("SELECT provider,capability,at,duration_ms,error IS NOT NULL OR timeout=1 FROM provider_calls WHERE at >= $from ORDER BY id", ("$from", Stamp(since)))
            .GroupBy(r => (Provider: Text(r[0]), Capability: Text(r[1]))).Select(g =>
            {
                var trend = new ProviderTrend { Provider = g.Key.Provider, Capability = g.Key.Capability, Days = days.Select(d => new ProviderDay { Day = d }).ToArray() };
                var byDay = trend.Days.ToDictionary(d => d.Day, StringComparer.Ordinal);
                foreach (var row in g)
                {
                    var at = Text(row[2]);
                    var failed = Int(row[4]) is not 0;
                    var slow = Long(row[3]) >= slowMs;
                    if (byDay.TryGetValue(at[..Math.Min(at.Length, 10)], out var d))
                    {
                        d.Calls++;
                        if (failed)
                        {
                            d.Errors++;
                        }

                        if (slow)
                        {
                            d.Slow++;
                        }
                    }
                    trend.LastCall = at;
                    trend.CallsAfterError++;
                    trend.CallsAfterSlow++;
                    if (failed)
                    { trend.LastError = at; trend.CallsAfterError = 0; }
                    if (slow)
                    { trend.LastSlow = at; trend.CallsAfterSlow = 0; }
                }
                return trend;
            }).OrderBy(t => t.Provider + t.Capability, StringComparer.Ordinal).ToArray();
    }
}
