namespace Harness.Core.Retrieval;

public static class RetrievalMerger
{
    public static Merged Merge(IEnumerable<Candidate> candidates)
    {
        List<Candidate> output = [];
        List<Folded> folded = [];
        Dictionary<string, int> ids = new(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (ids.TryGetValue(candidate.Id, out var index))
            {
                output[index] = Combine(output[index], candidate);
                folded.Add(new(candidate, candidate.Id, "same " + candidate.Source + " reported again"));
            }
            else
            {
                ids.Add(candidate.Id, output.Count);
                output.Add(candidate with { Scores = new(candidate.Scores, StringComparer.Ordinal), Provenance = candidate.Provenance.ToArray() });
            }
        }
        var definitions = output.Select((candidate, index) => (candidate, index))
            .Where(pair => pair.candidate.Source is "code" && pair.candidate.Role is "definition" && pair.candidate.StartLine > 0 && pair.candidate.EndLine > pair.candidate.StartLine)
            .GroupBy(pair => pair.candidate.Path, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        HashSet<int> removed = [];
        for (var i = 0; i < output.Count; i++)
        {
            var candidate = output[i];
            if (candidate.Source is not "code" || candidate.Role is "definition" || candidate.StartLine is 0 || candidate.EndLine > candidate.StartLine || !definitions.TryGetValue(candidate.Path, out var spans))
                continue;
            foreach (var (definition, index) in spans.Where(span => span.index != i && candidate.StartLine >= span.candidate.StartLine && candidate.StartLine <= span.candidate.EndLine))
            {
                var target = output[index];
                output[index] = target with { Provenance = target.Provenance.Concat(candidate.Provenance).Distinct().ToArray(), Evidence = candidate.Evidence is "exact" ? "exact" : target.Evidence };
                removed.Add(i);
                folded.Add(new(candidate, definition.Id, "line inside " + definition.Name));
                break;
            }
        }
        return new(output.Where((_, index) => !removed.Contains(index)).ToArray(), folded);
    }

    private static Candidate Combine(Candidate first, Candidate next)
    {
        foreach (var (name, score) in next.Scores.Where(pair => pair.Value > first.Scores.GetValueOrDefault(pair.Key)))
            first.Scores[name] = score;
        var result = first with
        {
            Provenance = first.Provenance.Concat(next.Provenance).Distinct().ToArray(),
            Evidence = next.Evidence is "exact" ? "exact" : first.Evidence,
            Name = first.Name.Length is 0 ? next.Name : first.Name,
            QualifiedName = first.QualifiedName.Length is 0 ? next.QualifiedName : first.QualifiedName,
            Freshness = next.Freshness is "live" ? "live" : first.Freshness,
        };
        if (result.Text.Length is 0 && next.Text.Length > 0)
            result = result with { Text = next.Text, Size = next.Size, SizeEstimated = next.SizeEstimated };
        if (result.EndLine is 0 && next.EndLine > 0)
            result = result with { EndLine = next.EndLine, Size = result.SizeEstimated || result.Size is 0 ? next.Size : result.Size, SizeEstimated = result.SizeEstimated || result.Size is 0 ? next.SizeEstimated : result.SizeEstimated };
        if (next.Depth > 0 && (result.Depth is 0 || next.Depth < result.Depth) && result.Role == next.Role)
            result = result with { Depth = next.Depth };
        if (RetrievalRanker.RoleWeight(next.Role) > RetrievalRanker.RoleWeight(result.Role))
            result = result with { Role = next.Role };
        return result;
    }
}
