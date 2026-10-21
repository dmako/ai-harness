using Harness.Mcp.Workspaces;
using Harness.Core.Retrieval;

namespace Harness.Mcp.Context;

public sealed record ContextRequest(string Task, int Budget = 0, int Tokens = 0, bool Debug = false);

public static class ContextRenderer
{
    public static int BudgetBytes(ContextRequest request) => (request.Tokens > 0 ? ContextBudget.FromTokens(request.Tokens).Bytes : request.Budget) switch
    {
        <= 0 => ContextBudget.DefaultBytes,
        < 1000 => 1000,
        var bytes => bytes,
    };

    public static Task<ContextAnswer> RenderAsync(ContextRequest request, Gathered gathered, ContextProject? project = null, CancellationToken cancellationToken = default) => RenderAsync(request, gathered, project, null, cancellationToken);

    public static async Task<ContextAnswer> RenderAsync(ContextRequest request, Gathered gathered, ContextProject? project, ContextWorkspace? workspace, CancellationToken cancellationToken = default)
    {
        var full = BudgetBytes(request);
        var notes = (project?.Notes ?? []).Concat(gathered.Notes).Concat(workspace?.Notes ?? []).ToList();
        var specified = request.Tokens > 0 ? ContextBudget.FromTokens(request.Tokens).Bytes : request.Budget;
        if (specified is > 0 and < 1000)
            notes.Add("budget raised to the minimum, 1000 bytes");
        var stale = project?.Stale ?? "";
        var candidates = gathered.Candidates;
        if (project?.CrossBranch is true)
        {
            var kept = candidates.Where(candidate => candidate.Source is not "code" || candidate.Freshness is not "indexed").ToArray();
            if (kept.Length < candidates.Count)
                stale += "; indexed code results were withheld until then";
            candidates = kept;
        }
        if (project?.IsDirectory is { } directory)
            candidates = candidates.Where(candidate => candidate.Source is not "code" || candidate.Role is not "file" || !directory(candidate.Path)).ToArray();
        var merged = RetrievalMerger.Merge(candidates);
        var errors = gathered.Errors.ToList();
        var diagnosticsTruncated = false;
        var empty = Base(request.Task, gathered.Symbols, errors, stale, workspace?.Projects);
        while (ContextJson.Size(empty) + 100 > full && errors.Count > 0)
        {
            diagnosticsTruncated = true;
            errors.RemoveAt(errors.Count - 1);
            empty = Base(request.Task, gathered.Symbols, errors, stale, workspace?.Projects);
        }
        if (ContextJson.Size(empty) + 100 > full)
            throw new ArgumentException("task metadata exceeds the context budget; raise the budget or shorten the task");
        var envelope = EnvelopeSize(empty, notes);
        while (envelope + 200 > full && (errors.Count > 0 || notes.Count > 0))
        {
            diagnosticsTruncated = true;
            if (errors.Count > 0)
                errors.RemoveAt(errors.Count - 1);
            else
                notes.RemoveAt(notes.Count - 1);
            empty = Base(request.Task, gathered.Symbols, errors, stale, workspace?.Projects);
            envelope = EnvelopeSize(empty, notes);
        }
        var budget = new ContextBudget(Math.Max(full - envelope, 1)) { Cost = (unit, packed) => ContextWire.UnitCost(unit, packed, request.Debug) };
        var packed = await ContextPacker.PackAsync(RetrievalRanker.Rank(merged.Candidates), budget, project?.ReadBody, cancellationToken).ConfigureAwait(false);
        var calls = request.Debug ? gathered.Calls.Count : 0;
        var debugEntries = packed.Units.Count + packed.Rejected.Count + merged.Folded.Count;
        var debugTruncated = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = Build();
            var json = Measured(output);
            if (System.Text.Encoding.UTF8.GetByteCount(json) <= full)
                return new(json);
            // The original adds debug after measuring. Keep useful context and bound diagnostics first.
            if (request.Debug && (debugEntries > 0 || calls > 0))
            {
                debugTruncated = true;
                if (debugEntries > 0)
                    debugEntries -= Math.Max(1, debugEntries / 4);
                else
                    calls -= Math.Max(1, calls / 4);
                continue;
            }
            if (errors.Count > 0 || notes.Count > 0)
            {
                diagnosticsTruncated = true;
                if (errors.Count > 0)
                    errors.RemoveAt(errors.Count - 1);
                else
                    notes.RemoveAt(notes.Count - 1);
                continue;
            }
            if (packed.Units.Count is 0)
                throw new ArgumentException("task metadata exceeds the context budget; raise the budget or shorten the task");
            var rejected = packed.Rejected.Prepend(packed.Units[^1].Ranked).ToArray();
            packed = packed with { Units = packed.Units.Take(packed.Units.Count - 1).ToArray(), Omitted = packed.Omitted + 1, Rejected = rejected };
        }

        Dictionary<string, object?> Build()
        {
            var output = Base(request.Task, gathered.Symbols, errors, stale, workspace?.Projects);
            output["budget_bytes"] = full;
            foreach (var unit in packed.Units)
            {
                if (unit.Ranked.Candidate.Source is "code")
                    ContextWire.AddCode(output, unit, request.Debug);
                else
                {
                    var key = unit.Ranked.Candidate.Source is "memory" ? "memory" : "changes";
                    if (!output.TryGetValue(key, out var entries))
                    { entries = new List<object>(); output[key] = entries; }
                    ((List<object>)entries!).Add(key is "memory" ? ContextWire.Memory(unit, request.Debug) : ContextWire.Change(unit));
                }
            }
            var messages = notes.ToList();
            if (packed.Omitted > 0)
            { output["omitted"] = packed.Omitted; messages.Add(ContextPacker.OmissionNote(packed.Omitted, full)); }
            if (debugTruncated)
                messages.Add("debug details truncated to fit the budget");
            if (diagnosticsTruncated)
                messages.Add("additional diagnostics omitted to fit the budget");
            ContextWire.Add(output, "note", string.Join("; ", messages));
            if (request.Debug)
                output["debug"] = ContextWire.Debug(gathered, merged.Folded, packed, calls, debugEntries);
            ContextFindings.Add(output, project?.Findings ?? [], full);
            return output;
        }
    }

    private static Dictionary<string, object?> Base(string task, IReadOnlyList<string> symbols, List<string> errors, string stale, IReadOnlyList<object>? workspace)
    {
        var output = new Dictionary<string, object?> { ["task"] = task, ["code"] = new List<Dictionary<string, object?>>(), ["used_bytes"] = 0, ["budget_bytes"] = 0 };
        if (workspace is { Count: > 0 })
            output["workspace"] = workspace;
        if (symbols.Count > 0)
            output["symbols"] = symbols;
        if (errors.Count > 0)
            output["errors"] = errors;
        ContextWire.Add(output, "stale", stale);
        return output;
    }
    private static int EnvelopeSize(Dictionary<string, object?> original, IReadOnlyList<string> notes)
    {
        var output = new Dictionary<string, object?>(original)
        {
            ["used_bytes"] = 999999,
            ["budget_bytes"] = 999999,
            ["omitted"] = 9999,
            ["memory"] = new[] { new { id = "", type = "" } },
            ["changes"] = new[] { new { commit = "", subject = "", path = "", when = "" } },
            ["areas"] = new Dictionary<string, int> { [""] = 0 },
            ["note"] = string.Join("; ", notes.Append(new string('x', 140)))
        };
        return ContextJson.Size(output) - ContextJson.Size(new { id = "", type = "" }) - ContextJson.Size(new { commit = "", subject = "", path = "", when = "" }) - ContextJson.Size("");
    }
    private static string Measured(Dictionary<string, object?> output)
    {
        for (var i = 0; i < 4; i++)
            output["used_bytes"] = ContextJson.Size(output);
        return ContextJson.Serialize(output);
    }
}
