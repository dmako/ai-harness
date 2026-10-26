using System.Globalization;
using Harness.Core.Telemetry;

namespace Harness.Core.Reports;

public static class ReportAnalyzer
{
    public const int MinCalls = 5;
    public const int SlowWarnMs = 3000;
    public const int SlowProblemMs = 10000;
    public const int ResolvedCalls = 10;

    public static IReadOnlyList<ReportFinding>? Findings(
        LocalReport report,
        DateTimeOffset now,
        IReadOnlyList<string> toolNames
    )
    {
        List<ReportFinding> result = [];
        ContextFindings(result, report);
        CalleeFindings(result, report.Context);
        HookFindings(result, report);
        foreach (var provider in report.Metrics.Providers.Where(p => p.Calls >= MinCalls))
        {
            ProviderFindings(
                result,
                provider,
                report.Trends.FirstOrDefault(t =>
                    t.Provider == provider.Provider && t.Capability == provider.Capability
                )
            );
        }

        var total = report.Metrics.Tools.Sum(t => t.Calls);
        ToolFindings(result, report.Metrics.Tools);
        UnusedToolFindings(result, report, toolNames, total);
        BypassFindings(result, report.Metrics.Bypasses ?? []);
        KnowledgeFindings(result, report, now, total);
        return result.Count is 0
            ? null
            : result
                .OrderBy(f =>
                    f.Level switch
                    {
                        "problem" => 0,
                        "warn" => 1,
                        _ => 2,
                    }
                )
                .ToArray();
    }

    private static void Add(
        List<ReportFinding> result,
        string level,
        string area,
        string action,
        FormattableString message
    ) => result.Add(new(level, area, FormattableString.Invariant(message), action));

    /// <summary>Too few scored get_context answers or a low hit rate, and languages it rarely surfaces.</summary>
    private static void ContextFindings(List<ReportFinding> result, LocalReport report)
    {
        var st = report.Context;
        if (st.Calls < 5)
        {
            Add(
                result,
                "info",
                "context",
                "keep using get_context; answers are scored when HEAD moves on (harness sync)",
                $"only {st.Calls} get_context answer(s) scored in {report.Days} days: too few to judge relevance"
            );
        }
        else if (st.HitRate < .25)
        {
            Add(
                result,
                "warn",
                "context",
                "look at the never-surfaced files in harness context stats and improve gathering for them",
                $"get_context hit rate {Percent(st.HitRate)} per answer ({st.Hits} of {st.Changed} changed code files, tests apart) over {st.Calls} answers"
            );
        }

        foreach (var (language, pair) in st.Language.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (language is not "other" && pair[1] >= 10 && (double)pair[0] / pair[1] < .10)
            {
                Add(
                    result,
                    "warn",
                    "context",
                    "check which provider serves " + language + " (routing) or add a language-specific one",
                    $"{language} files are rarely in get_context: {pair[0]} of {pair[1]} changed ({Percent((double)pair[0] / pair[1])})"
                );
            }
        }
    }

    /// <summary>With 20 misses or more, whether enough of them were callees to justify code.callees.</summary>
    private static void CalleeFindings(List<ReportFinding> result, ContextStatistics st)
    {
        var misses = st.Changed - st.Hits;
        if (misses < 20)
        {
            return;
        }

        var share = (double)st.CalleeMisses / misses;
        if (share >= .15)
        {
            Add(
                result,
                "warn",
                "context",
                "build code.callees (HAR-0145): expanding context to callees would find these",
                $"{st.CalleeMisses} of {misses} misses ({Percent(share)}) changed a function the returned code calls"
            );
        }
        else
        {
            Add(
                result,
                "info",
                "context",
                "keep HAR-0145 (code.callees) waiting",
                $"callee misses are {Percent(share)} of misses ({st.CalleeMisses} of {misses}), below the {Percent(.15)} that would justify code.callees"
            );
        }
    }

    /// <summary>No client hooks, or (with hooks) a low share of read-after-context files the answer held.</summary>
    private static void HookFindings(List<ReportFinding> result, LocalReport report)
    {
        if (report.Metrics.Hooks.Events is 0)
        {
            Add(
                result,
                "info",
                "hooks",
                "harness hooks install --kind claude-code,codex (or run 'harness hooks')",
                $"no client hook events: read-after-context relevance and provider bypasses are not measured"
            );
        }
        else if (report.Context.AfterRead is { Touched: >= 10, HitRate: < .30 } ar)
        {
            Add(
                result,
                "warn",
                "context",
                "compare what agents read after get_context with what it returned (harness context stats)",
                $"read after context {Percent(ar.HitRate)}: of {ar.Touched} files agents read or edited after an answer, {ar.Hits} were in it"
            );
        }
    }

    /// <summary>Tools that fail often, and agents asking for deep workspace searches too often.</summary>
    private static void ToolFindings(List<ReportFinding> result, IReadOnlyList<ToolMetrics> tools)
    {
        var workspace = tools.Where(t => Workspace(t, "light") || Workspace(t, "deep")).Sum(t => t.Calls);
        var deep = tools.Where(t => Workspace(t, "deep")).Sum(t => t.Calls);
        foreach (var tool in tools.Where(t => t.Calls >= MinCalls && t.ErrorRate >= .10))
        {
            Add(
                result,
                "warn",
                "tools",
                "look at the tool's errors in harness metrics (bad arguments or a failing provider)",
                $"{tool.Tool} fails {Percent(tool.ErrorRate)} of {tool.Calls} calls"
            );
        }

        if (workspace >= MinCalls && (double)deep / workspace >= .5)
        {
            Add(
                result,
                "warn",
                "tools",
                "regenerate the agent instructions (harness instructions): deep is for when the user asks for compiler-exact cross-project answers",
                $"agents asked for depth deep in {deep} of {workspace} workspace calls: deep starts heavy providers in every member"
            );
        }
    }

    private static bool Workspace(ToolMetrics tool, string depth) =>
        (tool.Kind ?? "").Contains("[workspace " + depth + "]", StringComparison.Ordinal);

    /// <summary>With 20 tool calls or more, the tools (other than diagnostics) no agent called.</summary>
    private static void UnusedToolFindings(
        List<ReportFinding> result,
        LocalReport report,
        IReadOnlyList<string> toolNames,
        int total
    )
    {
        if (total < 20)
        {
            return;
        }

        var used = report.Metrics.Tools.Select(t => t.Tool).ToHashSet(StringComparer.Ordinal);
        var unused = toolNames
            .Where(n => !used.Contains(n) && n is not ("ping" or "diagnostics" or "state_get_test" or "state_put_test"))
            .ToArray();
        if (unused.Length > 0)
        {
            Add(
                result,
                "info",
                "tools",
                "check the agent instructions mention them, or drop tools nobody needs",
                $"unused for {report.Days} days while agents made {total} tool calls: {string.Join(", ", unused)}"
            );
        }
    }

    private static void BypassFindings(List<ReportFinding> result, IReadOnlyList<ProviderBypass> bypasses)
    {
        foreach (var bypass in bypasses)
        {
            Add(
                result,
                bypass.Calls >= 5 ? "warn" : "info",
                "bypass",
                "candidate capability: map "
                    + bypass.Tool
                    + " into harness or tell agents which harness tool covers it",
                $"{bypass.Calls} direct {bypass.Tool} call(s) to {bypass.Server} bypassed harness (provider {bypass.Provider})"
            );
        }
    }

    /// <summary>Busy agents that never read harness knowledge, and memory proposals waiting for review.</summary>
    private static void KnowledgeFindings(List<ReportFinding> result, LocalReport report, DateTimeOffset now, int total)
    {
        if (total >= 20 && report.KnowledgeReads is 0)
        {
            Add(
                result,
                "warn",
                "knowledge",
                "check that agent instructions ask for decisions_for before changing an area",
                $"agents made {total} tool calls but never read harness knowledge (decisions_for, goals_current, memory_search...)"
            );
        }

        if (report.Knowledge is not { Pending: > 0 } knowledge)
        {
            return;
        }

        var days = knowledge.OldestPending is { } oldest ? (int)(now - oldest).TotalDays : 0;
        var age = knowledge.OldestPending is null
            ? ""
            : ", oldest " + days.ToString(CultureInfo.InvariantCulture) + " days";
        Add(
            result,
            knowledge.OldestPending is not null && days > 7 ? "warn" : "info",
            "knowledge",
            "review them: harness proposals list, or the GUI's Proposals tab",
            $"{knowledge.Pending} memory proposal(s) waiting for review{age}"
        );
    }

    /// <summary>
    /// A provider problem to report: its level, what happened, the action, and from the trend when it
    /// last happened, how many calls followed, and which days count as bad.
    /// </summary>
    private sealed record ProviderProblem(
        string Level,
        string What,
        string Action,
        string? Last,
        int After,
        Func<ProviderDay, int> Bad
    );

    private static void ProviderFindings(List<ReportFinding> result, ProviderMetrics p, ProviderTrend? trend)
    {
        var name =
            p.Provider
            + " "
            + (p.Capability.StartsWith("code.", StringComparison.Ordinal) ? p.Capability[5..] : p.Capability);
        if (FailureProblem(p, trend) is { } failure)
        {
            result.Add(Judge(name, failure, trend));
        }

        if (SlowProblem(p, trend) is { } slow)
        {
            result.Add(Judge(name, slow, trend));
        }

        if (p.Calls >= 10 && p.EmptyRate >= .80 && p.HitShare <= 0)
        {
            result.Add(
                new(
                    "info",
                    "providers",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{name} answers empty {Percent(p.EmptyRate)} of {p.Calls} calls and never surfaced a changed file"
                    ),
                    "consider routing this capability away from the provider"
                )
            );
        }
    }

    /// <summary>A problem from half the calls failing, a warning from a tenth.</summary>
    private static ProviderProblem? FailureProblem(ProviderMetrics p, ProviderTrend? trend)
    {
        var failure = string.Create(CultureInfo.InvariantCulture, $"failed {Percent(p.ErrorRate)} of {p.Calls} calls");
        var (last, after) = (trend?.LastError, trend?.CallsAfterError ?? 0);
        if (p.ErrorRate >= .50)
        {
            return new(
                "problem",
                failure,
                "run harness doctor and check the provider's own log; route the capability elsewhere until fixed",
                last,
                after,
                d => d.Errors
            );
        }

        return p.ErrorRate >= .10
            ? new("warn", failure, "run harness doctor and check the provider's errors", last, after, d => d.Errors)
            : null;
    }

    /// <summary>A problem from a p95 latency of SlowProblemMs, a warning from SlowWarnMs.</summary>
    private static ProviderProblem? SlowProblem(ProviderMetrics p, ProviderTrend? trend)
    {
        var slow = string.Create(
            CultureInfo.InvariantCulture,
            $"was slow: p95 {(double)p.P95MS / 1000:F1} s over {p.Calls} calls"
        );
        var (last, after) = (trend?.LastSlow, trend?.CallsAfterSlow ?? 0);
        if (p.P95MS >= SlowProblemMs)
        {
            return new(
                "problem",
                slow,
                "lower its timeout or route the capability elsewhere; slow calls hold up get_context",
                last,
                after,
                d => d.Slow
            );
        }

        return p.P95MS >= SlowWarnMs
            ? new("warn", slow, "check why the provider is slow (indexing, cold start)", last, after, d => d.Slow)
            : null;
    }

    /// <summary>
    /// The finding with the trend's history; resolved after ResolvedCalls clean calls, otherwise noted
    /// as too recent to tell or still happening.
    /// </summary>
    private static ReportFinding Judge(string name, ProviderProblem problem, ProviderTrend? trend)
    {
        var history = trend is null ? null : Histogram(trend.Days, problem.Bad);
        if (history?.Length is 0)
        {
            history = null;
        }

        var message = name + " " + problem.What;
        if (trend is null)
        {
            return new(problem.Level, "providers", message, problem.Action, history, trend);
        }

        var (last, after) = (problem.Last, problem.After);
        if (after >= ResolvedCalls)
        {
            message += string.Create(
                CultureInfo.InvariantCulture,
                $", but not in the {after} calls since {Day(last)}: resolved"
            );
            return new("info", "providers", message, "nothing to do while it stays clean", history, trend);
        }

        if (after > 0)
        {
            message += string.Create(
                CultureInfo.InvariantCulture,
                $"; only {after} call(s) since the last one ({Day(last)}): too few to tell whether it stopped"
            );
        }
        else if (!string.IsNullOrEmpty(last) && last == trend.LastCall)
        {
            message += "; still happening (last call " + Day(last) + ")";
        }

        return new(problem.Level, "providers", message, problem.Action, history, trend);
    }

    public static string Percent(double rate) => (rate * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    private static string Day(string? at) => at is null ? "" : at[..Math.Min(at.Length, 10)];

    public static string Histogram(IReadOnlyList<ProviderDay> days, Func<ProviderDay, int> bad)
    {
        if (days.Count is 0)
        {
            return "";
        }

        const string Bars = "▁▂▃▄▅▆▇█";
        var peak = Math.Max(1, days.Max(d => d.Calls));
        string Draw(Func<ProviderDay, int> count) =>
            new(days.Select(d => count(d) is 0 ? '·' : Bars[Math.Min(7, ((count(d) * 8) - 1) / peak)]).ToArray());
        return "calls "
            + Draw(d => d.Calls)
            + "  problem "
            + Draw(bad)
            + "  "
            + days[0].Day[5..]
            + "→"
            + days[^1].Day[5..];
    }
}
