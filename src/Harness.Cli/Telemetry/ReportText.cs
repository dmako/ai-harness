using System.Globalization;
using Harness.Core.Reports;
using Harness.Core.Telemetry;

namespace Harness.Cli.Telemetry;

internal static class ReportText
{
    private static string P(double rate) => ReportAnalyzer.Percent(rate);
    private static string Cap(string capability) => capability.StartsWith("code.", StringComparison.Ordinal) ? capability[5..] : capability;
    private static string Bytes(int value) => value < 1024 ? value.ToString(CultureInfo.InvariantCulture) + "B" : string.Create(CultureInfo.InvariantCulture, $"{value / 1024d:F1}K");
    public static async Task MetricsAsync(TextWriter output, MetricsReport metrics, int days)
    {
        await output.WriteLineAsync($"MCP tools over the last {days} days (by calls):");
        if (metrics.Tools.Count is 0)
            await output.WriteLineAsync("  no tool calls recorded yet");
        else
        {
            await output.WriteLineAsync($"  {"tool",-22} {"kind",-10} {"calls",6} {"errors",6} {"empty",6} {"p50",7} {"p95",7} {"avg size",8}  clients");
            foreach (var r in metrics.Tools)
                await output.WriteLineAsync($"  {r.Tool,-22} {r.Kind,-10} {r.Calls,6} {P(r.ErrorRate),6} {P(r.EmptyRate),6} {r.P50MS,6}ms {r.P95MS,6}ms {Bytes(r.AvgBytes),8}  {string.Join(",", r.Clients ?? [])}");
        }
        await output.WriteLineAsync("\nProvider capabilities:");
        if (metrics.Providers.Count is 0)
            await output.WriteLineAsync("  no provider calls recorded yet");
        else
        {
            await output.WriteLineAsync($"  {"provider",-16} {"capability",-22} {"calls",6} {"errors",6} {"timeouts",8} {"empty",6} {"p50",7} {"p95",7}  context hits");
            foreach (var r in metrics.Providers)
                await output.WriteLineAsync($"  {r.Provider,-16} {Cap(r.Capability),-22} {r.Calls,6} {P(r.ErrorRate),6} {r.Timeouts,8} {P(r.EmptyRate),6} {r.P50MS,6}ms {r.P95MS,6}ms  {(r.HitShare < 0 ? "—" : P(r.HitShare))}");
        }
        await output.WriteLineAsync("\nClient hook events (harness hooks install --kind claude-code,codex):");
        if (metrics.Hooks.Events is 0)
        { await output.WriteLineAsync("  none recorded: the client hooks are not installed, or no agent ran since"); return; }
        await output.WriteLineAsync($"  {metrics.Hooks.Events} event(s) over {metrics.Hooks.Sessions} session(s) from {string.Join(", ", metrics.Hooks.Clients ?? [])}, last {metrics.Hooks.Last}");
        if (metrics.Bypasses is not { Count: > 0 })
        { await output.WriteLineAsync($"  no direct calls to provider MCP servers in {days} days"); return; }
        await output.WriteLineAsync("\nDirect provider calls that bypassed harness (candidate capabilities):");
        await output.WriteLineAsync($"  {"server",-22} {"provider",-16} {"tool",-24} {"calls",6}");
        foreach (var b in metrics.Bypasses)
            await output.WriteLineAsync($"  {b.Server,-22} {b.Provider,-16} {b.Tool,-24} {b.Calls,6}");
    }
    public static async Task ContextAsync(TextWriter output, ContextStatistics stats, int days)
    {
        var ar = stats.AfterRead;
        await output.WriteLineAsync($"get_context over the last {days} days: {stats.Calls} answer(s) scored, {stats.Open} waiting for a commit");
        if (stats.Changed is 0)
        {
            if (ar.Touched > 0)
                await output.WriteLineAsync($"Read after context: {P(ar.HitRate)} ({ar.Hits} of {ar.Touched} files the agent then read or edited, over {ar.Calls} answer(s))");
            await output.WriteLineAsync("\nNo commit-scored answers yet: they are scored when HEAD moves on (harness sync) or a task completes with a commit.");
            return;
        }
        await output.WriteLineAsync($"Hit rate: {P(stats.HitRate)} per answer ({stats.Hits} of {stats.Changed} changed code files were in the context)");
        if (stats.Tests[1] > 0)
            await output.WriteLineAsync($"Test files: {stats.Tests[0]} of {stats.Tests[1]} changed were in the context (scored apart)");
        if (ar.Touched > 0)
            await output.WriteLineAsync($"Read after context: {P(ar.HitRate)} ({ar.Hits} of {ar.Touched} files the agent then read or edited, over {ar.Calls} answer(s); edits {ar.EditHits} of {ar.Edited})");
        if (stats.Changed > stats.Hits)
            await output.WriteLineAsync($"Callee misses: {stats.CalleeMisses} of {stats.Changed - stats.Hits} misses changed a function the returned code calls (what code.callees, HAR-0145, would add)");
        await output.WriteLineAsync("\nBy language:");
        foreach (var (language, pair) in stats.Language.OrderBy(p => p.Key, StringComparer.Ordinal))
            await output.WriteLineAsync($"  {language,-12} {P((double)pair[0] / Math.Max(1, pair[1])),4}  ({pair[0]} of {pair[1]})");
        await output.WriteLineAsync("\nBy provider, capability and language (hits / changed files in the answers it contributed to):");
        await output.WriteLineAsync($"  {"provider",-18} {"capability",-20} {"language",-11} {"rate",5} {"hits",9} {"tier0",6} {"median",7}");
        foreach (var r in stats.Rows ?? [])
            await output.WriteLineAsync($"  {r.Provider,-18} {Cap(r.Capability),-20} {r.Language,-11} {P(r.HitRate),5} {r.Hits,4}/{r.Changed,-4} {r.BestTier0,6} {r.MedianPos,7}");
        if (stats.Misses is { Count: > 0 })
        {
            await output.WriteLineAsync("\nChanged but never in the context (candidates for better gathering or a language-specific provider):");
            foreach (var m in stats.Misses)
                await output.WriteLineAsync($"  {m.Times,3}×  {m.Language,-11} {m.Path}{(m.CalledFrom is null ? "" : "  (called from " + m.CalledFrom + ")")}");
        }
    }
    public static async Task ReportAsync(TextWriter output, LocalReport report)
    {
        await output.WriteLineAsync($"{report.Root}  ({report.Id}, last {report.Days} days)");
        if (report.Findings is null or { Count: 0 })
            await output.WriteLineAsync("  no findings");
        foreach (var f in report.Findings ?? [])
        {
            var icon = f.Level switch { "problem" => "✕", "warn" => "▲", _ => "·" };
            await output.WriteLineAsync($"  {icon} {f.Level,-7} {f.Area,-10} {f.Message}");
            if (!string.IsNullOrEmpty(f.History))
                await output.WriteLineAsync($"    {"",-18}   {f.History}");
            if (!string.IsNullOrEmpty(f.Action))
                await output.WriteLineAsync($"    {"",-18} → {f.Action}");
        }
        if (report.Metrics.Since == default)
            return;
        var s = report.Context;
        var ar = s.AfterRead;
        var k = report.Knowledge;
        await output.WriteLineAsync($"  numbers: context hits {P(s.HitRate)} per answer (code {s.Hits}/{s.Changed}, tests {s.Tests[0]}/{s.Tests[1]}, over {s.Calls} answers) · read after context {P(ar.HitRate)} ({ar.Hits}/{ar.Touched}) · callee misses {s.CalleeMisses} of {s.Changed - s.Hits} · hook events {report.Metrics.Hooks.Events} · knowledge reads {report.KnowledgeReads}, writes {report.KnowledgeWrites} · proposals {k.Proposed} made, {k.Approved} approved, {k.Rejected} rejected, {k.Pending} pending");
    }
}
