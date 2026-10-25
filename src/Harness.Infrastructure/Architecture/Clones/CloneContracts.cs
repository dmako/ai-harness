using System.Text;
using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Clones;

public static class CloneContracts
{
    private static readonly HashSet<string> StopWords = CloneLexer.Words(
        "get set is has new to of for by on in the a and or with from test string int bool error err ok async"
    );
    private static readonly HashSet<string> Verbs = CloneLexer.Words(
        "add apply approve build call change check clean close compare complete copy create decode default delete detect draw encode ensure export fetch filter find format handle import init install join leave list load make mark merge move normalize open parse print promote put read record register reject remove rename render report reset resolve retire run save scan search send serve show sort start stop supersede swap sync take update uninstall validate verify view walk watch write"
    );

    public static IReadOnlyList<CloneContract> Find(
        IReadOnlyList<CloneFunction> functions,
        IReadOnlyList<CloneBranch> branches
    )
    {
        var operations = Families(functions);
        var words = operations
            .Select(operation =>
                operation.Variant.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            )
            .ToArray();
        Dictionary<string, string[]> keys = new(StringComparer.Ordinal);
        void Add(IEnumerable<string> input)
        {
            var set = input.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (set.Length >= 2)
            {
                keys[string.Join(' ', set)] = set;
            }
        }
        for (var index = 0; index < operations.Count; index++)
        {
            for (var other = index + 1; other < operations.Count; other++)
            {
                Add(words[index].Intersect(words[other], StringComparer.Ordinal));
            }

            foreach (var branch in branches.Where(branch => Supports(branch, words[index])))
            {
                Add(words[index].Intersect(branch.Words, StringComparer.Ordinal));
            }
        }
        var partners = keys
            .Values.SelectMany(set => set.Select(word => (word, set)))
            .GroupBy(pair => pair.word, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                    group
                        .SelectMany(pair => pair.set.Where(word => word != pair.word))
                        .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal
            );
        List<CloneContract> output = [];
        foreach (var set in keys.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
        {
            var applicable = operations
                .Select((operation, index) => (operation, index))
                .Where(pair => set.All(word => words[pair.index].Contains(word, StringComparer.Ordinal)))
                .Select(pair => new CloneOperation(
                    pair.operation.Stem,
                    pair.operation.Functions.Where(function =>
                            set.Contains(pair.operation.Variant[function.Id], StringComparer.Ordinal)
                        )
                        .ToArray(),
                    pair.operation.Variant.Where(item => set.Contains(item.Value, StringComparer.Ordinal))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                ))
                .ToArray();
            var dispatch = branches.Where(branch => Supports(branch, set)).ToArray();
            var evidence = branches
                .Where(branch =>
                    Supports(branch, set)
                    || applicable.Length > 0
                        && branch.Choices.Count <= 2
                        && branch.Words.Intersect(set, StringComparer.Ordinal).Count() is 1
                )
                .ToArray();
            var files = dispatch.Select(branch => branch.Path).Distinct(StringComparer.Ordinal).Count();
            var closed = set.All(word =>
                partners[word].Count(other => !set.Contains(other, StringComparer.Ordinal)) <= 1
            );
            if (
                files >= 2 && applicable.Length >= 1
                || files >= 1 && applicable.Length >= 2
                || applicable.Length >= 3 && closed
            )
            {
                output.Add(new(set, applicable, evidence.Length is 0 ? null : evidence));
            }
        }
        return output
            .Where(contract =>
                !output.Any(other =>
                    other.Variants.Count > contract.Variants.Count
                    && contract.Variants.All(word => other.Variants.Contains(word, StringComparer.Ordinal))
                    && other.Operations.Count >= contract.Operations.Count
                )
            )
            .OrderByDescending(contract => contract.Operations.Count * 2 + (contract.Branches?.Count ?? 0))
            .ToArray();
    }

    private static bool Supports(CloneBranch branch, string[] set) =>
        branch.Words.Intersect(set, StringComparer.Ordinal).Count() >= 2
        && branch.Choices.Count(choice => choice.Intersect(set, StringComparer.Ordinal).Any()) * 2
            >= branch.Choices.Count;

    private static List<CloneOperation> Families(IReadOnlyList<CloneFunction> functions)
    {
        Dictionary<string, List<(CloneFunction Function, string Word)>> byStem = new(StringComparer.Ordinal);
        foreach (var function in functions)
        {
            var words = NameWords(function.Name.Split('.')[^1]);
            if (words.Count < 2)
            {
                continue;
            }

            for (var index = 0; index < words.Count; index++)
            {
                if (Verbs.Contains(words[index]))
                {
                    continue;
                }

                var stem = string.Join(' ', words.Select((word, number) => number == index ? "*" : word));
                if (!byStem.TryGetValue(stem, out var members))
                {
                    members = [];
                    byStem.Add(stem, members);
                }
                members.Add((function, words[index]));
            }
        }
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<CloneOperation> output = [];
        foreach (var members in byStem.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
        {
            var distinct = members.Select(member => member.Word).Distinct(StringComparer.Ordinal).Count();
            if (distinct < 2 || members.Count != distinct)
            {
                continue;
            }

            var name = members[0].Function.Name.Split('.')[^1];
            var at = name.IndexOf(members[0].Word, StringComparison.OrdinalIgnoreCase);
            var stem = at < 0 ? name : name[..at] + "*" + name[(at + members[0].Word.Length)..];
            var kept = members.Where(member => !seen.Contains(member.Function.Id)).ToArray();
            if (kept.Length < 2)
            {
                continue;
            }

            foreach (var member in kept)
            {
                seen.Add(member.Function.Id);
            }

            output.Add(
                new(
                    stem,
                    kept.Select(member => member.Function).ToArray(),
                    kept.ToDictionary(member => member.Function.Id, member => member.Word, StringComparer.Ordinal)
                )
            );
        }
        return output;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Variant words are part of the lowercase wire contract."
    )]
    internal static IReadOnlyList<string> NameWords(string name)
    {
        List<string> output = [];
        StringBuilder current = new();
        void Flush()
        {
            if (current.Length > 1)
            {
                var word = current.ToString().ToLowerInvariant();
                if (!StopWords.Contains(word) && !output.Contains(word, StringComparer.Ordinal))
                {
                    output.Add(word);
                }
            }
            current.Clear();
        }
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (!char.IsLetter(character))
            {
                Flush();
                continue;
            }

            if (
                char.IsUpper(character)
                && index > 0
                && (
                    char.IsLower(name[index - 1])
                    || index + 1 < name.Length && char.IsLower(name[index + 1]) && char.IsUpper(name[index - 1])
                )
            )
            {
                Flush();
            }

            current.Append(character);
        }
        Flush();
        return output;
    }
}
