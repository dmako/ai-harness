using System.Globalization;
using Harness.Core.Reports;
using Harness.Core.Telemetry;

namespace Harness.Cli.Telemetry;

internal static class ReportText
{
    private static string P(double rate) => ReportAnalyzer.Percent(rate);

    public static async Task ReportAsync(TextWriter output, LocalReport report)
    {
        await output.WriteLineAsync($"{report.Root}  ({report.Id}, last {report.Days} days)");
        if (report.Findings is null or { Count: 0 })
        {
            await output.WriteLineAsync("  no findings");
        }

        foreach (var f in report.Findings ?? [])
        {
            var icon = f.Level switch
            {
                "problem" => "✕",
                "warn" => "▲",
                _ => "·",
            };
            await output.WriteLineAsync($"  {icon} {f.Level, -7} {f.Area, -10} {f.Message}");
            if (!string.IsNullOrEmpty(f.History))
            {
                await output.WriteLineAsync($"    {"", -18}   {f.History}");
            }

            if (!string.IsNullOrEmpty(f.Action))
            {
                await output.WriteLineAsync($"    {"", -18} → {f.Action}");
            }
        }
        if (report.Metrics.Since == default)
        {
            return;
        }

        var s = report.Context;
        var ar = s.AfterRead;
        var k = report.Knowledge;
        await output.WriteLineAsync(
            $"  numbers: context hits {P(s.HitRate)} per answer (code {s.Hits}/{s.Changed}, tests {s.Tests[0]}/{s.Tests[1]}, over {s.Calls} answers) · read after context {P(ar.HitRate)} ({ar.Hits}/{ar.Touched}) · hook events {report.Metrics.Hooks.Events} · knowledge reads {report.KnowledgeReads}, writes {report.KnowledgeWrites} · proposals {k.Proposed} made, {k.Approved} approved, {k.Rejected} rejected, {k.Pending} pending"
        );
    }
}
