using System.Globalization;
using System.Text;
using Harness.Core.Architecture;
using Harness.Core.Providers;
using Harness.Core.Retrieval;

namespace Harness.Infrastructure.Architecture.Clones;

public sealed class RepeatedCodeReader(AnalysisRepository repository)
{
    private readonly CloneExtractor _extractor = new(repository);
    public CloneExtractor Extractor => _extractor;
    public IReadOnlyList<ArchitectureFinding> Read(IReadOnlyList<ArchitectureFinding>? provided = null)
    {
        var functions = _extractor.Extract();
        var copies = CloneComparison.Find(functions);
        var own = copies.Select(CopyFinding).ToList();
        var groups = own.SelectMany((finding, index) => finding.Evidence!.Select(evidence => (Id: PlaceId(evidence), Group: index + 1))).GroupBy(pair => pair.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last().Group, StringComparer.Ordinal);
        foreach (var finding in Checked(functions, provided ?? []))
        {
            if (finding.Kind is "duplicate" && finding.Evidence is { Count: 2 } evidence && groups.TryGetValue(PlaceId(evidence[0]), out var left) && groups.GetValueOrDefault(PlaceId(evidence[1])) == left)
            {
                continue;
            }

            own.Add(finding);
        }
        var templates = CloneComparison.Templates(functions);
        own.AddRange(templates.Select(TemplateFinding));
        own.AddRange(CloneSequences.Find(functions, copies, templates).Select(SequenceFinding));
        own.AddRange(CloneContracts.Find(functions, _extractor.Branches()).Select(ContractFinding));
        return own;
    }
    private static ArchitectureFinding CopyFinding(CloneCopy copy)
    {
        var names = Distinct(copy.Functions);
        var title = (names.Length, copy.Functions.Count) switch
        {
            (1, 2) => names[0] + " is written twice",
            (1, _) => names[0] + $" is written {copy.Functions.Count} times",
            (_, 2) => names[0] + " repeats " + names[1],
            _ => List(names) + $": one body written {copy.Functions.Count} times",
        };
        var (fix, small) = CopyFix(copy.Functions[0]);
        return new()
        {
            Kind = "duplicate",
            Level = "info",
            Key = ArchitectureAnalyzer.Key("duplicate", [.. copy.Functions.Select(function => function.Id).Order(StringComparer.Ordinal)]),
            Measure = copy.Functions.Count,
            Title = title,
            Fix = fix,
            Project = Directory(copy.Functions[0].Path),
            Evidence = Places(copy.Functions),
            Source = ArchitectureAnalyzer.Harness("internal/clones"),
            Rank = Rank(copy.Functions, copy.Similarity, small),
            Why = copy.Similarity >= 1 ? "Their bodies are the same code (names of their own parameters and locals aside): a fix to one is needed in the others." : $"Their bodies are {(int)(copy.Similarity * 100)}% alike with their names kept: a change to one likely needs the others.",
            Basis = copy.Similarity >= 1 ? "fact" : "heuristic",
            Confidence = copy.Similarity >= 1 ? 0 : copy.Similarity
        };
    }
    private static (string Fix, bool Small) CopyFix(CloneFunction function)
    {
        var standard = CloneExtractor.Standard(function);
        return (standard.Length > 0, function.Data, function.Tokens <= 20, function.Wraps) switch
        {
            (true, _, _, _) => ("It is " + standard + " from Go's standard library: call that and delete the copies.", true),
            (_, true, true, _) => ("Small, but it holds a decision the copies must agree on (a length, a format, a constant): keep one where it belongs and call it.", false),
            (_, _, true, not null) => ("It only wraps " + function.Wraps + ": call that where it is used, or keep the copies; a shared wrapper this small costs more than it saves. Share it only if the copies must behave the same (they can drift: check they still do).", true),
            (_, _, true, _) => ("A line or two: keep the copies or inline them; share it only if they must behave the same.", true),
            _ => ("Keep one and call it from the other places, or move it where they can all use it.", false),
        };
    }
    private static ArchitectureFinding TemplateFinding(CloneTemplate template)
    {
        var names = Distinct(template.Functions);
        var title = names.Length is 1 ? names[0] + $" follows one template {template.Functions.Count} times" : List(names) + $": one template followed {template.Functions.Count} times";
        return new() { Kind = "template", Level = "info", Key = ArchitectureAnalyzer.Key("template", [.. template.Functions.Select(function => function.Id).Order(StringComparer.Ordinal)]), Measure = template.Functions.Count, Title = title, Rank = Rank(template.Functions, template.Similarity, template.Tokens < 60), Why = $"They take the same steps (their shapes {(int)(template.Similarity * 100)}% alike) on other names, so they are not copies: a change to the steps is made in each.", Fix = template.Tokens >= 60 ? "A shared base or a generic helper could hold the steps once, each variant passing what differs (its repository, entity or call)." : "A short pattern: nothing worth sharing while it stays this size; keep the variants in step when the steps change.", Project = Directory(template.Functions[0].Path), Evidence = Places(template.Functions, template.Differs), Basis = "heuristic", Confidence = template.Similarity, Source = ArchitectureAnalyzer.Harness("internal/clones") };
    }
    private static ArchitectureFinding SequenceFinding(CloneSequence sequence)
    {
        var names = sequence.Places.Select(place => place.Function.Name.Split('.')[^1]).Distinct(StringComparer.Ordinal).ToArray();
        var text = sequence.Text.Count > 8 ? sequence.Text.Take(8).Append($"… {sequence.Text.Count - 8} more") : sequence.Text;
        var why = $"The same {Plural(sequence.Statements, "statement")} written out in {sequence.Places.Count} places ({List(names)}), each a change to make in all of them:\n" + string.Join('\n', text);
        var fix = "Extract them into one function the places call, passing what differs.";
        if (sequence.Holder is { } holder)
        {
            fix = $"{holder.Name} ({holder.Path}:{holder.Line}) already does this: call it from the other places.";
        }
        else if (sequence.Tokens <= 20 && !sequence.Data)
        {
            fix = "A short run: extract it only if the places must stay the same; otherwise keep them.";
        }

        var hash = 2166136261U;
        foreach (var value in Encoding.UTF8.GetBytes(sequence.Key))
        {
            hash = unchecked((hash ^ value) * 16777619U);
        }

        var differs = sequence.Places.Any(place => place.Differs?.Count > 0);
        return new() { Kind = "repeated-sequence", Level = "info", Key = ArchitectureAnalyzer.Key("repeated-sequence", [.. sequence.Places.Select(place => place.Function.Id).Order(StringComparer.Ordinal), hash.ToString("x", CultureInfo.InvariantCulture)]), Measure = sequence.Places.Count, Title = $"{Plural(sequence.Statements, "statement")} repeated in {sequence.Places.Count} places ({List(names)})", Why = why + (differs ? "\nThey differ in numbers only." : ""), Fix = fix, Project = Directory(sequence.Places[0].Function.Path), Evidence = sequence.Places.Select(place => $"{place.Function.Path}:{place.Line} ({place.Function.Name})" + (place.Differs is { Count: > 0 } values ? ": " + string.Join(", ", values) : "")).ToArray(), Source = ArchitectureAnalyzer.Harness("internal/clones"), Rank = (double)sequence.Tokens * sequence.Places.Count * sequence.Places.Select(place => Directory(place.Function.Path)).Distinct(StringComparer.Ordinal).Count() / (sequence.Tokens <= 20 && !sequence.Data ? 10 : 1), Basis = differs ? "heuristic" : "fact", Confidence = differs ? 0.9 : 0 };
    }
    private static ArchitectureFinding ContractFinding(CloneContract contract)
    {
        var files = (contract.Branches ?? []).Select(branch => branch.Path).Distinct(StringComparer.Ordinal).Count();
        var confidence = Math.Min(0.9, 0.3 + 0.1 * Math.Min(contract.Operations.Count, 4) + 0.1 * Math.Min(files, 3));
        var variants = List(contract.Variants);
        var why = $"{contract.Operations.Count} operations ({string.Join(", ", contract.Operations.Select(operation => operation.Stem))}) are written once for each of {variants}, their names differing only by it" + (files > 0 ? $", and code in {files} file(s) chooses among them by name" : "") + ": a new variant means writing each operation again and finding every place that chooses.";
        var evidence = contract.Operations.SelectMany(operation => operation.Functions.Select(function => $"{function.Path}:{function.Line} ({function.Name}): {operation.Variant[function.Id]}")).Concat((contract.Branches ?? []).Select(branch => $"{branch.Path}:{branch.Line}: chooses by {string.Join(", ", branch.Words)}")).ToArray();
        return new() { Kind = "interface-candidate", Level = "info", Key = ArchitectureAnalyzer.Key("interface-candidate", [.. contract.Variants]), Measure = contract.Operations.Count, Title = $"{variants}: one contract written per variant ({Plural(contract.Operations.Count, "operation")})", Why = why, Fix = "An interface with the operations as its methods and one implementation per variant; callers take the implementation from a list or map by variant instead of branching on it. A heuristic from names: check the variants are alternatives of one thing, not different things handled alike.", Project = Directory(contract.Operations[0].Functions[0].Path), Evidence = evidence, Basis = "heuristic", Confidence = confidence, Source = ArchitectureAnalyzer.Harness("internal/clones"), Rank = confidence * 100 + contract.Operations.Count + (contract.Branches?.Count ?? 0) / 10.0 };
    }
    private static List<ArchitectureFinding> Checked(IReadOnlyList<CloneFunction> functions, IReadOnlyList<ArchitectureFinding> provided)
    {
        List<ArchitectureFinding> output = [];
        foreach (var finding in provided)
        {
            if (finding.Kind is not "duplicate" || finding.Evidence is not { Count: 2 } evidence)
            { output.Add(finding); continue; }
            var left = ParsePlace(evidence[0]);
            var right = ParsePlace(evidence[1]);
            if (!CloneExtractor.Reads(left.Path) || !CloneExtractor.Reads(right.Path))
            { output.Add(finding); continue; }
            CloneFunction? Locate(EvidenceSite site) => functions.Where(function => function.Path == site.Path && (function.Name == site.Name || function.Name.Split('.')[^1] == site.Name)).MinBy(function => Math.Abs((long)function.Line - site.Line));
            var a = Locate(left);
            var b = Locate(right);
            if (a is null || b is null)
            {
                continue;
            }

            var (similarity, copied) = CloneComparison.Check(a, b);
            if (!copied)
            {
                continue;
            }

            output.Add(finding with { Confidence = similarity, Basis = similarity >= 1 ? "fact" : finding.Basis, Why = $"{finding.Source.Provider} pairs them; with their names kept their bodies are {(int)(similarity * 100)}% alike: a change to one likely needs the other." });
        }
        return output;
    }
    private static EvidenceSite ParsePlace(string evidence)
    {
        var space = evidence.IndexOf(" (", StringComparison.Ordinal);
        var where = space < 0 ? evidence : evidence[..space];
        var name = space < 0 ? "" : evidence[(space + 2)..].Split(')')[0].Split('.')[^1];
        var parts = where.Split(':');
        int.TryParse(parts.ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var line);
        return new(parts[0], line, name);
    }
    private static string PlaceId(string evidence) { var site = ParsePlace(evidence); return site.Name is { Length: > 0 } ? site.Path + "#" + site.Name : ""; }
    private static double Rank(IReadOnlyList<CloneFunction> functions, double similarity, bool small) => (double)functions.Min(function => function.Tokens) * functions.Count * functions.Select(function => Directory(function.Path)).Distinct(StringComparer.Ordinal).Count() * similarity / (small ? 10 : 1);
    private static string[] Distinct(IReadOnlyList<CloneFunction> functions) => functions.Select(function => function.Name.Split('.')[^1]).Distinct(StringComparer.Ordinal).ToArray();
    private static string[] Places(IReadOnlyList<CloneFunction> functions, IReadOnlyDictionary<string, IReadOnlyList<string>?>? differs = null) => functions.Select(function => $"{function.Path}:{function.Line} ({function.Name})" + (differs?.GetValueOrDefault(function.Id) is { Count: > 0 } names ? ": " + string.Join(", ", names) : "")).ToArray();
    private static string Directory(string file) => Path.GetDirectoryName(file)?.Replace('\\', '/') is { Length: > 0 } dir ? dir : ".";
    private static string Plural(int count, string word) => count.ToString(CultureInfo.InvariantCulture) + " " + word + (count is 1 ? "" : "s");
    private static string List(IReadOnlyList<string> names) => names.Count switch { 0 => "", 1 => names[0], > 3 => string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more", _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1] };

    public static async Task<ProviderArchitectureRead> ProviderDuplicatesAsync(IProviderRegistry? registry, CancellationToken token)
    {
        if (registry is null)
        {
            return new([], false, []);
        }

        List<ArchitectureFinding> found = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        var answered = false;
        foreach (var result in await registry.CallAsync(new(Capabilities.ArchDuplicates, Limit: 100), cancellationToken: token).ConfigureAwait(false))
        {
            if (result.Error is not null)
            {
                continue;
            }

            answered = true;
            foreach (var item in result.Items)
            {
                var related = item.Related;
                if (related?.Path is not { Length: > 0 } path || item.Path is not { Length: > 0 } own || TaskParsing.IsTestFile(own) && TaskParsing.IsTestFile(path) || ChangeCoupling.Generated(own) || ChangeCoupling.Generated(path))
                {
                    continue;
                }

                var ids = new[] { own + "#" + item.Name, path + "#" + related.Name }.Order(StringComparer.Ordinal).ToArray();
                var key = ArchitectureAnalyzer.Key("duplicate", ids);
                if (!seen.Add(key))
                {
                    continue;
                }

                static string At(string file, string? name, int line) => file + (line > 0 ? ":" + line.ToString(CultureInfo.InvariantCulture) : "") + " (" + name + ")";
                found.Add(new() { Kind = "duplicate", Level = "info", Key = key, Measure = 1, Title = item.Name == related.Name ? item.Name + " is written twice" : item.Name + " repeats " + related.Name, Why = $"{item.Provider} finds their code {(int)(item.Confidence * 100 + 0.5)}% alike: a change to one likely needs the other.", Fix = "Keep one and call it from the other place, or move it where both can use it.", Project = Directory(own), Evidence = [At(own, item.Name, item.StartLine), At(path, related.Name, related.StartLine)], Basis = "heuristic", Confidence = item.Confidence, Source = new(item.Provider, item.Tool.Length is 0 ? null : item.Tool) });
            }
        }
        return new(found, answered, []);
    }
}
