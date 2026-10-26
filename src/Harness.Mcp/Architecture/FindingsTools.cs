using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Findings;
using Harness.Core.Serialization;

namespace Harness.Mcp.Architecture;

public static class FindingsTools
{
    public const string Rule =
        "Explain and suggest; never act on a finding until it is a task. Architectural tasks and by-design marks are the author's: propose them to the user, never create them. A finding marked by design is intended (its reason says why): do not raise it again; one back for review grew since it was marked.";

    /// <summary>The filters and output options of an architecture_findings call.</summary>
    private sealed record FindingsQuery(
        string Key,
        string Kind,
        string Area,
        bool Gone,
        bool HideMarked,
        bool Full,
        bool Snippets
    )
    {
        public static FindingsQuery From(IDictionary<string, JsonElement>? arguments) =>
            new(
                Text(arguments, "key"),
                Text(arguments, "kind"),
                Text(arguments, "area"),
                Flag(arguments, "gone"),
                Flag(arguments, "hide_marked"),
                Flag(arguments, "full"),
                Flag(arguments, "snippets")
            );

        /// <summary>Findings are listed in full when asked to, or when a key, kind or area narrows them.</summary>
        public bool InFull => Full || Key.Length > 0 || Kind.Length > 0 || Area.Length > 0;

        public bool Matches(Finding f) =>
            (Key.Length is 0 || f.Key == Key)
            && (Kind.Length is 0 || f.Kind == Kind)
            && (Area.Length is 0 || InArea(f, Area))
            && (Gone || f.Status is not "gone");

        private static bool Flag(IDictionary<string, JsonElement>? arguments, string key) =>
            arguments?.TryGetValue(key, out var value) is true && value.ValueKind is JsonValueKind.True;
    }

    public static async Task<object> CallAsync(
        string name,
        IDictionary<string, JsonElement>? arguments,
        IFindingSession session,
        string caller,
        CancellationToken token
    )
    {
        if (name is "architecture_propose")
        {
            return await ProposeAsync(arguments, session, caller, token).ConfigureAwait(false);
        }

        var read = await session.ReadFindingsAsync(Text(arguments, "scope"), token).ConfigureAwait(false);
        var query = FindingsQuery.From(arguments);
        List<object> groups = [];
        foreach (var group in read.Groups)
        {
            groups.Add(await GroupAsync(group, query, session, token).ConfigureAwait(false));
        }
        Dictionary<string, object?> answer = new() { ["groups"] = groups, ["rule"] = Rule };
        if (read.Note.Length > 0)
        {
            answer["note"] = read.Note;
        }

        return answer;
    }

    private static string Text(IDictionary<string, JsonElement>? arguments, string key) =>
        arguments?.TryGetValue(key, out var value) is true ? value.GetString() ?? "" : "";

    private static async Task<Dictionary<string, object?>> ProposeAsync(
        IDictionary<string, JsonElement>? arguments,
        IFindingSession session,
        string caller,
        CancellationToken token
    )
    {
        var request = new FindingProposalRequest(
            Text(arguments, "kind"),
            Text(arguments, "finding"),
            Text(arguments, "scope"),
            Text(arguments, "title"),
            Text(arguments, "reason"),
            arguments?.TryGetValue("evidence", out var evidence) is true
                ? HarnessJson.Deserialize<string[]>(evidence)
                : null
        );
        var result = await session.ProposeFindingAsync(request, caller, token).ConfigureAwait(false);
        return new()
        {
            ["proposal"] = Clean(result.Proposal),
            ["status"] = result.Existing ? "exists" : "pending",
            ["note"] = result.Existing
                ? "The same proposal already waits for the user ("
                    + result.Proposal.Id
                    + "); tell them it is in harness ui, Work › Inbox."
                : "Waiting for the user: tell them it is in harness ui, Work › Inbox. Do not act on it until they approve it.",
        };
    }

    /// <summary>One group's summary and its selected findings, in brief or in full, with their code if asked.</summary>
    private static async Task<Dictionary<string, object?>> GroupAsync(
        FindingGroup group,
        FindingsQuery query,
        IFindingSession session,
        CancellationToken token
    )
    {
        var selected = group.Findings.Where(query.Matches).ToArray();
        var hidden = 0;
        if (query.HideMarked)
        {
            hidden = selected.Count(ByDesign);
            selected = selected.Where(f => !ByDesign(f)).ToArray();
        }

        Dictionary<string, object?> output = new() { ["where"] = group.Where, ["summary"] = Summary(selected) };
        if (group.Snapshot is not null)
        {
            output["snapshot"] = Clean(group.Snapshot);
        }

        if (hidden > 0)
        {
            output["marked_hidden"] = hidden;
        }

        if (selected.Length > 0 && query.InFull)
        {
            output["findings"] = selected.Select(Clean).ToArray();
        }
        else if (selected.Length > 0)
        {
            output["brief"] = selected.Select(Brief).ToArray();
        }

        if (query.Snippets)
        {
            await AddSnippetsAsync(output, group, selected, session, token).ConfigureAwait(false);
        }

        return output;
    }

    private static bool ByDesign(Finding f) => f.Mark?.State is "by_design";

    /// <summary>Per kind: how many findings, and the worst level among them; worst kinds first.</summary>
    private static JsonObjectMap[] Summary(Finding[] selected) =>
        selected
            .GroupBy(f => f.Kind, StringComparer.Ordinal)
            .Select(g => new JsonObjectMap
            {
                ["kind"] = g.Key,
                ["count"] = g.Count(),
                ["worst"] = g.OrderBy(f => Rank(f.Level)).First().Level,
            })
            .OrderBy(g => Rank((string)g["worst"]!))
            .ToArray();

    /// <summary>
    /// The code at each finding's evidence, in order, while it fits 8000 bytes; counts the snippets left
    /// out.
    /// </summary>
    private static async Task AddSnippetsAsync(
        Dictionary<string, object?> output,
        FindingGroup group,
        Finding[] selected,
        IFindingSession session,
        CancellationToken token
    )
    {
        var room = 8000;
        var left = 0;
        List<object> code = [];
        foreach (var f in selected)
        {
            foreach (var at in f.Evidence ?? [])
            {
                var snippet = await session
                    .ReadFindingSnippetAsync(group, at, SnippetSpan(f), token)
                    .ConfigureAwait(false);
                if (snippet is null)
                {
                    continue;
                }

                var body = string.Join('\n', snippet.Lines ?? []);
                var size = Encoding.UTF8.GetByteCount(body) + Encoding.UTF8.GetByteCount(at) + 40;
                if (size > room)
                {
                    left++;
                    continue;
                }
                room -= size;
                code.Add(
                    new JsonObjectMap
                    {
                        ["key"] = f.Key,
                        ["at"] = at,
                        ["start"] = snippet.Start,
                        ["code"] = body,
                        ["focus"] = snippet.Focus,
                    }
                );
            }
        }

        if (code.Count > 0)
        {
            output["code"] = code;
        }

        if (left > 0)
        {
            output["code_left_out"] = left;
        }
    }

    /// <summary>A repeated sequence's statement count (the lines of its why after the first); 0 otherwise.</summary>
    private static int SnippetSpan(Finding f) =>
        f.Kind is "repeated-sequence"
            ? f
                .Why.Split('\n')
                .Skip(1)
                .Count(line => line.Length > 0 && !line.StartsWith("They differ", StringComparison.Ordinal))
            : 0;

    private static int Rank(string level) =>
        level switch
        {
            "problem" => 0,
            "warn" => 1,
            "info" => 2,
            _ => 0,
        };

    private static object Brief(Finding f)
    {
        Dictionary<string, object?> result = new()
        {
            ["key"] = f.Key,
            ["level"] = f.Level,
            ["basis"] = f.Basis,
            ["title"] = f.Title,
        };
        if (f.Confidence is not 0)
        {
            result["confidence"] = f.Confidence;
        }

        if (!string.IsNullOrEmpty(f.Status))
        {
            result["status"] = f.Status;
        }

        if (f.Evidence is { Count: > 0 } evidence)
        {
            result["at"] = evidence[0];
            if (evidence.Count > 1)
            {
                result["more"] = evidence.Count - 1;
            }
        }
        if (f.Mark is { } mark)
        {
            result["mark"] = mark.State + (mark.TaskId.Length > 0 ? " " + mark.TaskId : "");
        }

        return result;
    }

    public static bool InArea(Finding f, string area)
    {
        area = area.TrimEnd('/');
        bool Under(string p) => p == area || p.StartsWith(area + "/", StringComparison.Ordinal);
        var project = (f.Project ?? "").TrimEnd('/');
        var folder =
            Path.GetExtension(project).EndsWith("proj", StringComparison.Ordinal)
            || Path.GetExtension(project) is ".json"
                ? project[..Math.Max(0, project.LastIndexOf('/'))]
                : project;
        if (Under(project) || Under(folder))
        {
            return true;
        }

        return (f.Evidence ?? []).Any(e =>
        {
            if (e.StartsWith(area + ": ", StringComparison.Ordinal))
            {
                return true;
            }

            var colon = e.IndexOf(": ", StringComparison.Ordinal);
            if (colon >= 0)
            {
                e = e[(colon + 2)..];
            }

            var file =
                e.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(':')[0] ?? "";
            return Under(file);
        });
    }

    // Omit empty optional strings as well as null values.
    private static JsonNode Clean<T>(T value)
    {
        var node = HarnessJson.SerializeToNode(value)!;
        void Visit(JsonNode? item)
        {
            if (item is JsonObject obj)
            {
                foreach (var (key, child) in obj.ToArray())
                {
                    if (
                        child is JsonValue v
                        && v.TryGetValue<string>(out var text)
                        && text.Length is 0
                        && key
                            is "reason"
                                or "review_reason"
                                or "memory_id"
                                or "task_id"
                                or "task_project"
                                or "proposed_by"
                                or "commit"
                                or "finding_key"
                                or "body"
                                or "decided_by"
                                or "decided_at"
                                or "note"
                                or "result"
                                or "fix"
                                or "project"
                    )
                    {
                        obj.Remove(key);
                    }
                    else
                    {
                        Visit(child);
                    }
                }
            }
            else if (item is JsonArray array)
            {
                foreach (var child in array)
                {
                    Visit(child);
                }
            }
        }
        Visit(node);
        return node;
    }
}
