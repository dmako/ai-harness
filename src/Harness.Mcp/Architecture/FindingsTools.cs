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

    public static async Task<object> CallAsync(
        string name,
        IDictionary<string, JsonElement>? arguments,
        IFindingSession session,
        string caller,
        CancellationToken token
    )
    {
        string Text(string key) => arguments?.TryGetValue(key, out var value) is true ? value.GetString() ?? "" : "";
        bool Flag(string key) =>
            arguments?.TryGetValue(key, out var value) is true && value.ValueKind is JsonValueKind.True;
        if (name is "architecture_propose")
        {
            var request = new FindingProposalRequest(
                Text("kind"),
                Text("finding"),
                Text("scope"),
                Text("title"),
                Text("reason"),
                arguments?.TryGetValue("evidence", out var evidence) is true
                    ? HarnessJson.Deserialize<string[]>(evidence)
                    : null
            );
            var result = await session.ProposeFindingAsync(request, caller, token).ConfigureAwait(false);
            return new Dictionary<string, object?>
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
        var read = await session.ReadFindingsAsync(Text("scope"), token).ConfigureAwait(false);
        List<object> groups = [];
        foreach (var group in read.Groups)
        {
            var key = Text("key");
            var kind = Text("kind");
            var area = Text("area");
            var selected = group
                .Findings.Where(f =>
                    (key.Length is 0 || f.Key == key)
                    && (kind.Length is 0 || f.Kind == kind)
                    && (area.Length is 0 || InArea(f, area))
                    && (Flag("gone") || f.Status is not "gone")
                )
                .ToArray();
            var hidden = Flag("hide_marked") ? selected.Count(f => f.Mark?.State is "by_design") : 0;
            if (Flag("hide_marked"))
            {
                selected = selected.Where(f => f.Mark?.State is not "by_design").ToArray();
            }

            var summary = selected
                .GroupBy(f => f.Kind, StringComparer.Ordinal)
                .Select(g => new JsonObjectMap
                {
                    ["kind"] = g.Key,
                    ["count"] = g.Count(),
                    ["worst"] = g.OrderBy(f => Rank(f.Level)).First().Level,
                })
                .OrderBy(g => Rank((string)g["worst"]!))
                .ToArray();
            Dictionary<string, object?> output = new() { ["where"] = group.Where, ["summary"] = summary };
            if (group.Snapshot is not null)
            {
                output["snapshot"] = Clean(group.Snapshot);
            }

            if (hidden > 0)
            {
                output["marked_hidden"] = hidden;
            }

            if (Flag("full") || key.Length > 0 || kind.Length > 0 || area.Length > 0)
            {
                if (selected.Length > 0)
                {
                    output["findings"] = selected.Select(Clean).ToArray();
                }
            }
            else if (selected.Length > 0)
            {
                output["brief"] = selected.Select(Brief).ToArray();
            }

            if (Flag("snippets"))
            {
                var room = 8000;
                var left = 0;
                List<object> code = [];
                foreach (var f in selected)
                {
                    foreach (var at in f.Evidence ?? [])
                    {
                        var span = f.Kind is "repeated-sequence"
                            ? f
                                .Why.Split('\n')
                                .Skip(1)
                                .Count(line =>
                                    line.Length > 0 && !line.StartsWith("They differ", StringComparison.Ordinal)
                                )
                            : 0;
                        var snippet = await session
                            .ReadFindingSnippetAsync(group, at, span, token)
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
            groups.Add(output);
        }
        Dictionary<string, object?> answer = new() { ["groups"] = groups, ["rule"] = Rule };
        if (read.Note.Length > 0)
        {
            answer["note"] = read.Note;
        }

        return answer;
    }

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

    // Go's omitempty omits empty optional strings as well as null pointers.
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
