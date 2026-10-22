using Harness.Core.Reports;
using Harness.Core.Telemetry;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;

namespace Harness.Infrastructure.Telemetry;

public sealed partial class TelemetryStore
{
    public LocalReport Report(
        int days,
        DateTimeOffset now,
        IReadOnlyList<string> toolNames,
        IReadOnlyDictionary<string, string>? servers = null
    )
    {
        var since = now.AddDays(-days);
        var report = new LocalReport
        {
            Days = days,
            Context = ContextStats(since),
            Metrics = Metrics(since),
            Trends = Trends(since, now),
            Knowledge = Usage(since, now),
        };
        report.Metrics.Bypasses = Bypasses(since, servers ?? new Dictionary<string, string>());
        report.KnowledgeReads = report
            .Metrics.Tools.Where(t =>
                t.Tool is "decisions_for" or "goals_current" or "constraints_current" or "memory_search" or "memory_get"
            )
            .Sum(t => t.Calls);
        report.KnowledgeWrites = report
            .Metrics.Tools.Where(t => t.Tool is "memory_put" or "memory_supersede" or "memory_propose")
            .Sum(t => t.Calls);
        report.Findings = ReportAnalyzer.Findings(report, now, toolNames);
        report.Id = ProjectStore.Load(database)?.Id ?? "";
        return report;
    }

    public KnowledgeUsage Usage(DateTimeOffset since, DateTimeOffset now)
    {
        var from = Stamp(since);
        var current = Query(
            "SELECT COUNT(*) FROM memories WHERE superseded_by IS NULL AND (valid_from IS NULL OR valid_from <= $at) AND (valid_until IS NULL OR valid_until > $at)",
            ("$at", Stamp(now))
        )[0];
        var p = Query(
            """
            SELECT COUNT(CASE WHEN created_at >= $from THEN 1 END),COUNT(CASE WHEN status='approved' AND decided_at >= $from THEN 1 END),
            COUNT(CASE WHEN status='rejected' AND decided_at >= $from THEN 1 END),COUNT(CASE WHEN status='pending' THEN 1 END),MIN(CASE WHEN status='pending' THEN created_at END) FROM memory_proposals
            """,
            ("$from", from)
        )[0];
        return new()
        {
            Current = Int(current[0]),
            Proposed = Int(p[0]),
            Approved = Int(p[1]),
            Rejected = Int(p[2]),
            Pending = Int(p[3]),
            OldestPending = p[4] is string at
                ? DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture)
                : null,
        };
    }
}
