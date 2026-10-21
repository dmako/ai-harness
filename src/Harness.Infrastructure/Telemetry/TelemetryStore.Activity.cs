using System.Globalization;
using System.Text.Json;
using Harness.Core.Memory;
using Harness.Core.Reports;

namespace Harness.Infrastructure.Telemetry;

public sealed partial class TelemetryStore
{
    public IReadOnlyList<ActivityItem>? Activity(DateTimeOffset since, int limit = 0)
    {
        var from = Stamp(since);
        var result = Sessions(from);
        result.AddRange(Query("""
            SELECT e.at,e.task_id,e.event,COALESCE(e.to_status,''),e.actor,COALESCE(e.detail,''),t.title FROM task_events e JOIN tasks t ON t.id=e.task_id
            WHERE e.at >= $from AND e.event IN ('claimed','completed','status','lease_expired')
            """, ("$from", from)).Select(r =>
            {
                var id = Text(r[1]);
                var title = Text(r[6]);
                var status = Text(r[3]);
                var message = Text(r[2]) switch { "completed" => id + " done: " + title, "claimed" => id + " claimed: " + title, "lease_expired" => id + ": the claim expired (" + title + ")", _ => id + " is " + status.Replace('_', ' ') + ": " + title };
                var level = Text(r[2]) is "lease_expired" || Text(r[2]) is "status" && status is "blocked" ? "warn" : null;
                return new ActivityItem(At(Text(r[0])), "task", message, Text(r[2]) is "status" ? Optional(Clip(Text(r[5]).Trim(), 160)) : null, id, Optional(Text(r[4])), level);
            }));
        result.AddRange(Query("SELECT e.at,e.memory_id,e.event,e.actor,m.type,m.content FROM memory_events e JOIN memories m ON m.id=e.memory_id WHERE e.at >= $from AND e.event IN ('created','superseded','retired')", ("$from", from)).Select(r => new ActivityItem(At(Text(r[0])), "memory", Text(r[4]).Replace('_', ' ') + " " + (Text(r[2]) is "created" ? "recorded" : Text(r[2])), Optional(Clip(Text(r[5]).Trim(), 160)), Text(r[1]), Optional(Text(r[3])))));
        result.AddRange(SyncActivity(from));
        var ordered = result.OrderByDescending(r => MemoryTimestamp.Parse(r.At)).ToArray();
        if (ordered.Length is 0)
            return null;
        return limit > 0 ? ordered.Take(limit).ToArray() : ordered;
    }
    private static string At(string text)
    {
        try
        { return MemoryTimestamp.Parse(text).ToRfc3339Nano(); }
        catch (FormatException)
        {
            return DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? new MemoryTimestamp(at).ToRfc3339Nano() : "0001-01-01T00:00:00Z";
        }
    }
    private static string? Optional(string text) => text.Length is 0 ? null : text;
    private static DateTimeOffset Time(string text) => DateTimeOffset.Parse(At(text), CultureInfo.InvariantCulture);
    private List<ActivityItem> Sessions(string from)
    {
        List<ActivityItem> result = [];
        foreach (var client in Query("SELECT at,tool,client,error IS NOT NULL FROM tool_calls WHERE at >= $from ORDER BY at", ("$from", from)).GroupBy(r => Text(r[2]), StringComparer.Ordinal))
        {
            List<object?[]> current = [];
            void Flush()
            {
                if (current.Count is 0)
                    return;
                var start = Time(Text(current[0][0]));
                var end = Time(Text(current[^1][0]));
                var span = end - start;
                var duration = span switch
                {
                    _ when span.TotalMinutes < 1 => "under a minute",
                    _ when span.TotalHours < 1 => ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min",
                    _ => span.TotalHours.ToString("F1", CultureInfo.InvariantCulture) + " h",
                };
                var tools = current.GroupBy(r => Text(r[1])).Select(g => (Tool: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ThenBy(g => g.Tool, StringComparer.Ordinal).ToArray();
                var parts = tools.Take(4).Select(g => g.Tool + " ×" + g.Count.ToString(CultureInfo.InvariantCulture)).ToList();
                if (tools.Length > 4)
                    parts.Add((tools.Length - 4).ToString(CultureInfo.InvariantCulture) + " more");
                var errors = current.Count(r => Int(r[3]) is not 0);
                var title = string.Create(CultureInfo.InvariantCulture, $"{(client.Key.Length is 0 ? "an agent" : client.Key)} used harness: {current.Count} call(s) over {duration}") + (errors > 0 ? string.Create(CultureInfo.InvariantCulture, $", {errors} failed") : "");
                result.Add(new(At(Text(current[^1][0])), "session", title, string.Join(", ", parts), Actor: Optional(client.Key), Level: errors > 0 ? "warn" : null));
                current.Clear();
            }
            foreach (var call in client)
            {
                if (current.Count > 0 && Time(Text(call[0])) - Time(Text(current[^1][0])) > TimeSpan.FromMinutes(30))
                    Flush();
                current.Add(call);
            }
            Flush();
        }
        return result;
    }
    private List<ActivityItem> SyncActivity(string from)
    {
        List<ActivityItem> result = [];
        var runs = 0;
        foreach (var r in Query("SELECT started_at,status,mode,COALESCE(head_commit,''),added,changed,removed,providers,COALESCE(error,'') FROM sync_runs WHERE started_at >= $from AND status != 'running' ORDER BY started_at", ("$from", from)))
        {
            var status = Text(r[1]);
            var head = Text(r[3]);
            var detail = string.Create(CultureInfo.InvariantCulture, $"{Text(r[2])} at {head[..Math.Min(7, head.Length)]}: +{Int(r[4])} ~{Int(r[5])} −{Int(r[6])} files");
            try
            {
                using var providers = JsonDocument.Parse(Text(r[7]));
                if (providers.RootElement.ValueKind is JsonValueKind.Array)
                {
                    var failed = providers.RootElement.EnumerateArray().Where(p => p.TryGetProperty("status", out var s) && s.GetString() is "failed").Select(p => p.GetProperty("name").GetString()).ToArray();
                    if (failed.Length > 0)
                        detail += "; failed: " + string.Join(", ", failed);
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { /* Source ignores malformed optional provider observations. */ }
            var level = status switch { "failed" => "problem", "partial" => "warn", _ => null };
            if (status is "failed")
                detail = Clip(Text(r[8]).Trim(), 160);
            var item = new ActivityItem(At(Text(r[0])), "sync", "sync " + status, Optional(detail), Level: level);
            if (result.Count > 0 && status is "ok" && result[^1].Level is null && Time(item.At) - Time(result[^1].At) < TimeSpan.FromHours(2))
                result[^1] = item with { Title = (++runs).ToString(CultureInfo.InvariantCulture) + " syncs ok" };
            else
            { runs = 1; result.Add(item); }
        }
        return result;
    }
}
