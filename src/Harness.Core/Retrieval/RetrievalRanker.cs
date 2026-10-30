using System.Globalization;
using System.Text;

namespace Harness.Core.Retrieval;

public static class RetrievalRanker
{
    private static readonly HashSet<string> Documents = new(
        [".md", ".markdown", ".txt", ".rst", ".adoc", ".yaml", ".yml", ".json", ".csv"],
        StringComparer.OrdinalIgnoreCase
    );

    internal static double RoleWeight(string role) =>
        role switch
        {
            "definition" => 1,
            "context" or "constraint" or "decision" or "warning" => 0.9,
            // a question still open there ranks beside the rules: not to be settled unknowingly
            "convention" or "open_question" => 0.8,
            "caller" => 0.7,
            "flow" or "impact" => 0.6,
            "reference" or "match" or "fact" => 0.5,
            "change" => 0.4,
            "file" or "goal" => 0.3,
            _ => 0,
        };

    public static IReadOnlyList<Ranked> Rank(IEnumerable<Candidate> candidates) =>
        candidates
            .Select(Score)
            .OrderBy(ranked => ranked.Tier)
            .ThenByDescending(ranked => ranked.Score)
            .ThenBy(ranked => TieKey(ranked.Candidate), Utf8Comparer.Instance)
            .ToArray();

    public static Ranked Score(Candidate candidate)
    {
        var weight = RoleWeight(candidate.Role);
        if (weight is 0)
        {
            weight = 0.3;
        }

        var score = weight;
        var tier = 1;
        List<string> why = ["role " + candidate.Role + " " + Format(weight, "F2")];
        switch (candidate.Source)
        {
            case "code":
                ScoreCode(candidate, weight, ref score, ref tier, why);
                break;
            case "memory":
                var scope = candidate.Scores.GetValueOrDefault("scope");
                score *= scope;
                why.Add("scope ×" + Format(scope, "F2"));
                break;
            case "git":
                var recency = candidate.Scores.GetValueOrDefault("recency");
                score *= recency;
                why.Add("recency ×" + Format(recency, "F2"));
                break;
        }
        if (candidate.Sources > 1)
        {
            var factor = Math.Min(1 + 0.1 * (candidate.Sources - 1), 1.3);
            score *= factor;
            why.Add(candidate.Sources.ToString(CultureInfo.InvariantCulture) + " sources ×" + Format(factor, "F2"));
        }
        if (candidate.Evidence is "inferred")
        {
            score *= 0.8;
            why.Add("inferred ×0.80");
        }
        why.Add("= " + Format(score, "F3"));
        return new(candidate, tier, score, why);
    }

    private static void ScoreCode(Candidate candidate, double weight, ref double score, ref int tier, List<string> why)
    {
        if (candidate.Depth > 1)
        {
            score /= candidate.Depth;
            why.Add(string.Create(CultureInfo.InvariantCulture, $"depth {candidate.Depth} ÷{candidate.Depth}"));
        }
        if (candidate.Scores.TryGetValue("rank", out var rank))
        {
            var factor = 0.5 + 0.5 * rank;
            score *= factor;
            why.Add("position ×" + Format(factor, "F2"));
        }
        var named = candidate.Scores.GetValueOrDefault("name") is 1;
        if (candidate.Scores.GetValueOrDefault("phrase") is 1)
        {
            if (weight < 1 && candidate.Role is "file")
            {
                score = score / weight;
                why.Add("file named in the task's words: weighted as a definition");
            }
            score *= 1.5;
            why.Add("described in the task ×1.5");
        }
        else if (candidate.Scores.GetValueOrDefault("loose") is 1 && !named)
        {
            score *= 0.6;
            why.Add("only matched the task sentence ×0.6");
        }
        if (
            Documents.Contains(Extension(candidate.Path))
            && !named
            && candidate.Scores.GetValueOrDefault("phrase") is not 1
        )
        {
            score *= 0.5;
            why.Add("documentation or data, not code ×0.5");
        }
        if (candidate.Scores.GetValueOrDefault("test") is 1 && !named)
        {
            score *= 0.6;
            why.Add("test code, task not about tests ×0.6");
        }
        if (named && candidate.Role is "definition")
        {
            tier = 0;
            why.Add("named in the task (tier 0)");
        }
    }

    internal static string Extension(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[dot..] : "";
    }

    private static string Format(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static string TieKey(Candidate candidate)
    {
        if (candidate.Provenance.Count is 0)
        {
            return candidate.Id;
        }

        var source = candidate.Provenance[0];
        return candidate.Id
            + string.Create(CultureInfo.InvariantCulture, $"|{source.Provider}|{source.Capability}|{source.Rank}");
    }

    private sealed class Utf8Comparer : IComparer<string>
    {
        public static Utf8Comparer Instance { get; } = new();

        public int Compare(string? first, string? second) =>
            Encoding.UTF8.GetBytes(first ?? "").AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(second ?? ""));
    }
}
