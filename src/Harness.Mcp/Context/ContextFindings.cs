using Harness.Core.Findings;
using Harness.Mcp.Architecture;

namespace Harness.Mcp.Context;

internal static class ContextFindings
{
    internal static void Add(Dictionary<string, object?> output, IReadOnlyList<FindingContextGroup> groups, int budget)
    {
        var files = ((List<Dictionary<string, object?>>)output["code"]!)
            .Select(file => (string)file["path"]!)
            .ToHashSet(StringComparer.Ordinal);
        var touching = groups
            .SelectMany(g => g.Findings.Select(f => Touching(f, g, files)))
            .OfType<Dictionary<string, object?>>()
            .OrderBy(f =>
                f["level"] switch
                {
                    "problem" => 0,
                    "warn" => 1,
                    _ => 2,
                }
            )
            .ToArray();
        if (touching.Length is 0)
        {
            return;
        }

        List<object> kept = [];
        output["findings"] = kept;
        output["findings_rule"] = FindingsTools.Rule;
        foreach (var finding in touching)
        {
            kept.Add(finding);
            if (ContextJson.Size(output) + 20 > budget)
            {
                kept.RemoveAt(kept.Count - 1);
                break;
            }
        }
        if (kept.Count is 0)
        {
            output.Remove("findings");
            output.Remove("findings_rule");
        }
        if (kept.Count < touching.Length)
        {
            output["omitted"] = (output.GetValueOrDefault("omitted") as int? ?? 0) + touching.Length - kept.Count;
        }
    }

    private static Dictionary<string, object?>? Touching(Finding f, FindingContextGroup group, HashSet<string> files)
    {
        var see = (f.Evidence ?? []).Select(e => FileOf(e, group.Member)).FirstOrDefault(files.Contains) ?? "";
        if (see.Length is 0 && group.Member.Length is 0)
        {
            var area = (f.Project ?? "").TrimEnd('/');
            if (
                Path.GetExtension(area).EndsWith("proj", StringComparison.Ordinal) || Path.GetExtension(area) is ".json"
            )
            {
                area = Path.GetDirectoryName(area)?.Replace('\\', '/') ?? "";
            }

            if (area.Length > 0)
            {
                see =
                    files
                        .Where(file => file.StartsWith(area + "/", StringComparison.Ordinal))
                        .Order(StringComparer.Ordinal)
                        .FirstOrDefault()
                    ?? "";
            }
        }
        if (see.Length is 0)
        {
            return null;
        }

        Dictionary<string, object?> result = new()
        {
            ["key"] = f.Key,
            ["kind"] = f.Kind,
            ["level"] = f.Level,
            ["basis"] = f.Basis,
            ["source"] = (f.Source.Provider + " " + f.Source.Tool).Trim(),
            ["title"] = f.Title,
            ["see"] = see,
        };
        ContextWire.Add(result, "where", group.Where);
        ContextWire.Add(result, "status", f.Status ?? "");
        if (f.Confidence is not 0)
        {
            result["confidence"] = f.Confidence;
        }

        if (f.Rule is { } rule)
        {
            result["rule"] = rule.Line + " (" + rule.Memory + ")";
        }

        if (f.Mark is { } mark)
        {
            ContextWire.Add(
                result,
                "mark",
                mark.State switch
                {
                    "by_design" => "by design: " + mark.Reason,
                    "review" => "back for review (" + mark.ReviewReason + "); by design: " + mark.Reason,
                    "task" => "task " + mark.TaskId,
                    _ => "",
                }
            );
        }

        return result;
    }

    private static string FileOf(string evidence, string member)
    {
        if (member.Length > 0)
        {
            if (!evidence.StartsWith(member + ": ", StringComparison.Ordinal))
            {
                return "";
            }

            evidence = evidence[(member.Length + 2)..];
        }
        var file =
            evidence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(':')[0] ?? "";
        return Path.GetExtension(file).Length > 0 ? file : "";
    }
}
