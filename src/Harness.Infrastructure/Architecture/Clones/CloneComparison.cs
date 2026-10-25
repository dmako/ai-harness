using System.Text;
using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Clones;

public static class CloneComparison
{
    private static readonly HashSet<string> GoTypes = CloneLexer.Words(
        "any bool byte comparable complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 uint64 uintptr"
    );
    private static readonly HashSet<string> Logic = CloneLexer.Words("== != === !== < > <= >= && || !");

    public static IReadOnlyList<CloneCopy> Find(IReadOnlyList<CloneFunction> functions, CloneOptions? options = null)
    {
        options ??= new();
        var input = functions.Where(function => function.Tokens >= options.MinIdentical).ToArray();
        var groups = new CloneGroups(input.Length);
        Dictionary<string, int> byCanonical = new(StringComparer.Ordinal);
        var first = new bool[input.Length];
        for (var index = 0; index < input.Length; index++)
        {
            var key = string.Join('\0', input[index].Canonical);
            if (byCanonical.TryGetValue(key, out var previous))
            {
                if (!Overrides(input[previous], input[index]))
                {
                    groups.Join(previous, index, 1);
                }
            }
            else
            {
                byCanonical.Add(key, index);
                first[index] = true;
            }
        }
        foreach (
            var (left, right) in Candidates(
                input,
                index => first[index] && input[index].Tokens >= options.MinNear,
                function => function.Canonical,
                4,
                100
            )
        )
        {
            var (similarity, copied) = Check(input[left], input[right], options);
            if (copied)
            {
                groups.Join(left, right, similarity);
            }
        }
        return groups
            .Sets()
            .Select(indices => new CloneCopy(Sorted(indices.Select(index => input[index])), groups.Low(indices[0])))
            .OrderBy(copy => copy.Functions[0].Path, StringComparer.Ordinal)
            .ThenBy(copy => copy.Functions[0].Line)
            .ToArray();
    }

    public static (double Similarity, bool Copied) Check(
        CloneFunction left,
        CloneFunction right,
        CloneOptions? options = null
    )
    {
        options ??= new();
        if (Nested(left, right) || Overrides(left, right) || Template(left, right) || OtherLogic(left, right))
        {
            return (0, false);
        }

        var similarity = Alike(left.Canonical, right.Canonical);
        var least = similarity >= 1 ? options.MinIdentical : options.MinNear;
        return (similarity, similarity >= options.Near && Math.Min(left.Tokens, right.Tokens) >= least);
    }

    public static IReadOnlyList<CloneTemplate> Templates(
        IReadOnlyList<CloneFunction> functions,
        CloneOptions? options = null
    )
    {
        options ??= new();
        var input = functions.Where(function => function.Tokens >= options.MinNear).ToArray();
        var groups = new CloneGroups(input.Length);
        foreach (
            var (left, right) in Candidates(
                input,
                _ => true,
                function => function.Shape,
                6,
                Math.Max(100, input.Length / 4)
            )
        )
        {
            if (
                Nested(input[left], input[right])
                || Overrides(input[left], input[right])
                || Check(input[left], input[right], options).Copied
            )
            {
                continue;
            }

            var similarity = Alike(input[left].Shape, input[right].Shape);
            if (similarity >= options.Shape)
            {
                groups.Join(left, right, similarity);
            }
        }
        List<CloneTemplate> output = [];
        foreach (var indices in groups.Sets())
        {
            if (indices.Count < options.Repeated && indices.Any(index => input[index].Tokens < options.LongPair))
            {
                continue;
            }

            var members = Sorted(indices.Select(index => input[index]));
            var uses = members
                .SelectMany(Names)
                .GroupBy(name => name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            // Same-named nested functions of one file share an id (app.js#label twice): the first speaks for both.
            var differs = members
                .DistinctBy(function => function.Id, StringComparer.Ordinal)
                .ToDictionary(
                    function => function.Id,
                    function =>
                    {
                        var own = Names(function)
                            .Where(name => 2 * uses[name] <= members.Length)
                            .Order(StringComparer.Ordinal)
                            .Take(4)
                            .ToArray();
                        return (IReadOnlyList<string>?)(own.Length is 0 ? null : own);
                    },
                    StringComparer.Ordinal
                );
            output.Add(new(members, differs, groups.Low(indices[0]), members.Min(function => function.Tokens)));
        }
        return output
            .OrderBy(template => template.Functions[0].Path, StringComparer.Ordinal)
            .ThenBy(template => template.Functions[0].Line)
            .ToArray();
    }

    public static bool Template(CloneFunction left, CloneFunction right)
    {
        var shortBody = Math.Min(left.Tokens, right.Tokens) < 40;
        HashSet<string> plain = new(StringComparer.Ordinal);
        foreach (var gap in Gaps(left.Canonical, right.Canonical))
        {
            var (leftStructure, leftPlain) = Kinds(left.Canonical, gap.LeftStart, gap.LeftEnd);
            var (rightStructure, rightPlain) = Kinds(right.Canonical, gap.RightStart, gap.RightEnd);
            if (leftStructure && rightStructure || shortBody && (leftStructure || rightStructure))
            {
                return true;
            }

            if ((leftStructure || leftPlain) && (rightStructure || rightPlain))
            {
                plain.Add(
                    string.Join(' ', left.Canonical.Skip(gap.LeftStart).Take(gap.LeftEnd - gap.LeftStart))
                        + "|"
                        + string.Join(' ', right.Canonical.Skip(gap.RightStart).Take(gap.RightEnd - gap.RightStart))
                );
            }
        }
        if (
            Gaps(left.Signature, right.Signature)
                .Any(gap => gap.LeftStart < gap.LeftEnd && gap.RightStart < gap.RightEnd)
        )
        {
            return true;
        }

        return plain.Count >= 2 || plain.Count is 1 && shortBody;
    }

    private static bool OtherLogic(CloneFunction left, CloneFunction right)
    {
        foreach (var gap in Gaps(left.Canonical, right.Canonical))
        {
            var a = left.Canonical.Skip(gap.LeftStart).Take(gap.LeftEnd - gap.LeftStart).ToArray();
            var b = right.Canonical.Skip(gap.RightStart).Take(gap.RightEnd - gap.RightStart).ToArray();
            if (
                a.Length > 0 && b.Length > 0 && (a.Any(Logic.Contains) || b.Any(Logic.Contains))
                || a.Length is 0 && b is ["!"]
                || b.Length is 0 && a is ["!"]
            )
            {
                return true;
            }
        }
        return false;
    }

    internal sealed record Gap(int LeftStart, int LeftEnd, int RightStart, int RightEnd);

    internal static IReadOnlyList<Gap> Gaps(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.SequenceEqual(right, StringComparer.Ordinal))
        {
            return [];
        }

        var lengths = Enumerable.Range(0, left.Count + 1).Select(_ => new int[right.Count + 1]).ToArray();
        for (var i = left.Count - 1; i >= 0; i--)
        {
            for (var j = right.Count - 1; j >= 0; j--)
            {
                lengths[i][j] =
                    left[i] == right[j] ? lengths[i + 1][j + 1] + 1 : Math.Max(lengths[i + 1][j], lengths[i][j + 1]);
            }
        }

        List<Gap> gaps = [];
        var a = 0;
        var b = 0;
        var startA = 0;
        var startB = 0;
        void Flush()
        {
            if (startA < a || startB < b)
            {
                gaps.Add(new(startA, a, startB, b));
            }
        }
        while (a < left.Count && b < right.Count)
        {
            if (left[a] == right[b])
            {
                Flush();
                a++;
                b++;
                startA = a;
                startB = b;
            }
            else if (lengths[a + 1][b] >= lengths[a][b + 1])
            {
                a++;
            }
            else
            {
                b++;
            }
        }
        a = left.Count;
        b = right.Count;
        Flush();
        return gaps;
    }

    private static (bool Structure, bool Plain) Kinds(IReadOnlyList<string> tokens, int start, int end)
    {
        var structure = false;
        var plain = false;
        for (var index = start; index < end; index++)
        {
            if (IsName(tokens[index]))
            {
                if (Structural(tokens, index))
                {
                    structure = true;
                }
                else
                {
                    plain = true;
                }
            }
        }

        return (structure, plain);
    }

    internal static bool IsName(string token) =>
        token.Length > 0
        && CloneLexer.IdentifierStart(token[0])
        && !CloneLexer.Keywords.Contains(token)
        && !CloneLexer.GoKeywords.Contains(token)
        && !Local(token);

    private static bool Structural(IReadOnlyList<string> tokens, int index)
    {
        var previous = index > 0 ? tokens[index - 1] : "";
        var next = index + 1 < tokens.Count ? tokens[index + 1] : "";
        return previous is "." or "?." or "new" or "is" or "as" or "not"
            || next is "(" or "<" or "{"
            || tokens[index].Contains('/', StringComparison.Ordinal)
            || GoTypes.Contains(tokens[index])
            || Local(next)
            || Local(previous);
    }

    internal static HashSet<string> Names(CloneFunction function) =>
        function.Canonical.Concat(function.Signature).Where(IsName).ToHashSet(StringComparer.Ordinal);

    internal static bool Local(string token) =>
        token.Length >= 2 && token[0] is 'L' && token.Skip(1).All(char.IsAsciiDigit);

    internal static bool Nested(CloneFunction left, CloneFunction right) =>
        left.Path == right.Path
        && (left.Line <= right.Line && right.End <= left.End || right.Line <= left.Line && left.End <= right.End);

    private static bool Overrides(CloneFunction left, CloneFunction right) =>
        left.Override && right.Override && left.Name == right.Name;

    internal static double Alike(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.SequenceEqual(right, StringComparer.Ordinal))
        {
            return 1;
        }

        var previous = new int[right.Count + 1];
        var current = new int[right.Count + 1];
        foreach (var token in left)
        {
            for (var index = 1; index <= right.Count; index++)
            {
                current[index] =
                    token == right[index - 1] ? previous[index - 1] + 1 : Math.Max(previous[index], current[index - 1]);
            }

            (previous, current) = (current, previous);
        }
        return 2.0 * previous[right.Count] / (left.Count + right.Count);
    }

    private static (int Left, int Right)[] Candidates(
        CloneFunction[] input,
        Func<int, bool> use,
        Func<CloneFunction, IReadOnlyList<string>> form,
        int size,
        int common
    )
    {
        var sets = new HashSet<ulong>?[input.Length];
        Dictionary<ulong, List<int>> index = [];
        for (var number = 0; number < input.Length; number++)
        {
            if (!use(number))
            {
                continue;
            }

            var tokens = form(input[number]);
            HashSet<ulong> hashes = [];
            for (var start = 0; start + size <= tokens.Count; start++)
            {
                var hash = 14695981039346656037UL;
                for (var offset = 0; offset < size; offset++)
                {
                    foreach (var value in Encoding.UTF8.GetBytes(tokens[start + offset]))
                    {
                        hash = unchecked((hash ^ value) * 1099511628211UL);
                    }

                    hash = unchecked(hash * 1099511628211UL);
                }
                hashes.Add(hash);
            }
            sets[number] = hashes;
            foreach (var hash in hashes)
            {
                if (!index.TryGetValue(hash, out var places))
                {
                    places = [];
                    index.Add(hash, places);
                }
                places.Add(number);
            }
        }
        List<(int Left, int Right)> pairs = [];
        for (var left = 0; left < input.Length; left++)
        {
            if (sets[left] is not { } hashes)
            {
                continue;
            }

            Dictionary<int, int> shared = [];
            foreach (var hash in hashes.Where(hash => index[hash].Count <= common))
            {
                foreach (var right in index[hash].Where(right => right > left))
                {
                    shared[right] = shared.GetValueOrDefault(right) + 1;
                }
            }

            foreach (var (right, count) in shared)
            {
                var a = form(input[left]).Count;
                var b = form(input[right]).Count;
                if (
                    5L * Math.Min(a, b) >= 4L * Math.Max(a, b)
                    && count >= 0.4 * (hashes.Count + sets[right]!.Count - count)
                )
                {
                    pairs.Add((left, right));
                }
            }
        }
        return pairs.OrderBy(pair => pair.Left).ThenBy(pair => pair.Right).ToArray();
    }

    internal static CloneFunction[] Sorted(IEnumerable<CloneFunction> functions) =>
        functions
            .OrderBy(function => function.Path, StringComparer.Ordinal)
            .ThenBy(function => function.Line)
            .ToArray();
}

internal sealed class CloneGroups(int count)
{
    private readonly int[] _parents = Enumerable.Range(0, count).ToArray();
    private readonly Dictionary<int, double> _low = [];

    private int Root(int index)
    {
        while (_parents[index] != index)
        {
            _parents[index] = _parents[_parents[index]];
            index = _parents[index];
        }
        return index;
    }

    internal void Join(int left, int right, double similarity)
    {
        var a = Root(left);
        var b = Root(right);
        similarity = Math.Min(
            similarity,
            Math.Min(_low.GetValueOrDefault(a, similarity), _low.GetValueOrDefault(b, similarity))
        );
        if (a != b)
        {
            _parents[b] = a;
            _low.Remove(b);
        }
        _low[a] = similarity;
    }

    internal double Low(int index) => _low[Root(index)];

    internal IReadOnlyList<IReadOnlyList<int>> Sets() =>
        Enumerable
            .Range(0, _parents.Length)
            .Where(index => _low.ContainsKey(Root(index)))
            .GroupBy(Root)
            .Where(group => group.Count() >= 2)
            .Select(group => (IReadOnlyList<int>)group.ToArray())
            .ToArray();
}
