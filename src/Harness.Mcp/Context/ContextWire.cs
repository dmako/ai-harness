using Harness.Core.Retrieval;

namespace Harness.Mcp.Context;

internal static class ContextWire
{
    internal static Dictionary<string, object?> Code(ContextUnit unit, bool debug)
    {
        var c = unit.Ranked.Candidate;
        var value = new Dictionary<string, object?> { ["ref"] = unit.Reference, ["role"] = c.Role, ["evidence"] = c.Evidence, ["providers"] = Providers(c) };
        Add(value, "name", c.Name);
        Add(value, "start_line", c.StartLine);
        Add(value, "end_line", c.EndLine);
        Add(value, "depth", c.Depth);
        Add(value, "body", unit.Body);
        if (unit.Clipped)
            value["clipped"] = true;
        if (debug)
            value["why"] = unit.Ranked.Explain();
        return value;
    }
    internal static Dictionary<string, object?> Memory(ContextUnit unit, bool debug)
    {
        var c = unit.Ranked.Candidate;
        var value = new Dictionary<string, object?> { ["id"] = c.Id.StartsWith("memory:", StringComparison.Ordinal) ? c.Id[7..] : c.Id, ["type"] = c.Role };
        if (unit.Reference.StartsWith("see ", StringComparison.Ordinal))
            Add(value, "see", unit.Reference[4..]);
        else
            Add(value, "content", c.Text);
        Add(value, "path", c.Path);
        Add(value, "scope", c.MemoryScope);
        if (c.Overrides.Count > 0)
            value["overrides"] = c.Overrides;
        if (debug)
            value["why"] = unit.Ranked.Explain() + " (" + (c.Provenance.Count > 0 ? c.Provenance[0].Tool : "") + ")";
        return value;
    }
    internal static Dictionary<string, object?> Change(ContextUnit unit)
    {
        var c = unit.Ranked.Candidate;
        return new() { ["commit"] = c.Name, ["subject"] = c.Text, ["path"] = c.Path, ["when"] = (c.When?.ToRfc3339Nano() ?? "0001-01-01T00:00:00Z")[..10] };
    }
    internal static int UnitCost(ContextUnit unit, IReadOnlyList<ContextUnit> packed, bool debug)
    {
        var c = unit.Ranked.Candidate;
        if (c.Source is "memory")
            return ContextJson.Size(Memory(unit, debug)) + 1;
        if (c.Source is not "code")
            return ContextJson.Size(Change(unit)) + 1;
        var size = ContextJson.Size(Code(unit, debug)) + 1;
        var previous = packed.Where(item => item.Ranked.Candidate.Source is "code").Select(item => item.Ranked.Candidate.Path).ToArray();
        if (!previous.Contains(c.Path, StringComparer.Ordinal))
            size += ContextJson.Size(new { path = c.Path, units = (object?)null }) + 1 - 4;
        if (!previous.Any(path => Area(path) == Area(c.Path)))
            size += ContextJson.Size(Area(c.Path)) + 4;
        return size;
    }
    internal static void AddCode(Dictionary<string, object?> output, ContextUnit unit, bool debug)
    {
        var path = unit.Ranked.Candidate.Path;
        var files = (List<Dictionary<string, object?>>)output["code"]!;
        var file = files.Find(file => (string)file["path"]! == path);
        if (file is null)
        {
            file = new() { ["path"] = path, ["units"] = new List<object>() };
            files.Add(file);
            if (!output.TryGetValue("areas", out var areas))
            { areas = new Dictionary<string, int>(StringComparer.Ordinal); output["areas"] = areas; }
            var counts = (Dictionary<string, int>)areas!;
            var area = Area(path);
            counts[area] = counts.GetValueOrDefault(area) + 1;
        }
        ((List<object>)file["units"]!).Add(Code(unit, debug));
    }
    internal static object Debug(Gathered gathered, IReadOnlyList<Folded> folds, PackedContext packed, int calls, int candidates)
    {
        List<object> entries = [];
        foreach (var unit in packed.Units)
        {
            var fate = "included";
            var reason = "";
            if (unit.Ranked.Candidate.Source is "code")
            {
                fate = unit.Body.Length > 0 ? "inlined" : "reference";
                reason = unit.BodyNote;
                if (unit.Body.Length > 0)
                    reason = unit.Clipped ? "clipped to the maximum body size" : "";
            }
            entries.Add(DebugCandidate(unit.Ranked, fate, reason));
        }
        entries.AddRange(packed.Rejected.Select(item => DebugCandidate(item, "omitted", "did not fit the budget")));
        entries.AddRange(folds.Select(fold => DebugCandidate(new(fold.Candidate, 0, 0, []), "merged", fold.Reason + " → " + fold.Into)));
        return new
        {
            calls = gathered.Calls.Take(calls).Select(call =>
        {
            var value = new Dictionary<string, object?> { ["provider"] = call.Provider, ["capability"] = call.Capability, ["query"] = call.Query, ["items"] = call.Items, ["latency_ms"] = call.LatencyMs, ["bytes"] = call.Bytes };
            Add(value, "error", call.Error);
            return value;
        }).ToArray(),
            candidates = entries.Take(candidates).ToArray()
        };
    }
    private static Dictionary<string, object?> DebugCandidate(Ranked item, string fate, string reason)
    {
        var c = item.Candidate;
        var value = new Dictionary<string, object?> { ["id"] = c.Id, ["source"] = c.Source, ["role"] = c.Role, ["tier"] = item.Tier, ["score"] = Math.Truncate(item.Score * 1000 + 0.5) / 1000, ["size"] = c.Size, ["evidence"] = c.Evidence, ["providers"] = Providers(c), ["fate"] = fate };
        Add(value, "name", c.Name);
        Add(value, "reason", reason);
        Add(value, "why", item.Explain());
        return value;
    }
    internal static void Add(Dictionary<string, object?> value, string name, string text) { if (text.Length > 0) value[name] = text; }
    private static void Add(Dictionary<string, object?> value, string name, int number) { if (number is not 0) value[name] = number; }
    private static string Area(string path) { var slash = path.IndexOf('/', StringComparison.Ordinal); return slash < 0 ? path : path[..slash]; }
    private static string[]? Providers(Candidate candidate) { var providers = candidate.Provenance.Select(item => item.Provider).Distinct(StringComparer.Ordinal).ToArray(); return providers.Length > 0 ? providers : null; }
}
