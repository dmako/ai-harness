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
        var pairs = Candidates(
            input,
            index => first[index] && input[index].Tokens >= options.MinNear,
            function => function.Canonical,
            4,
            100
        );
        // The checks are independent and run side by side; groups are joined in the pairs' order (TASK-0107).
        var checks = new (double Similarity, bool Copied)[pairs.Length];
        Parallel.For(0, pairs.Length, at => checks[at] = Check(input[pairs[at].Left], input[pairs[at].Right], options));
        for (var at = 0; at < pairs.Length; at++)
        {
            if (checks[at].Copied)
            {
                groups.Join(pairs[at].Left, pairs[at].Right, checks[at].Similarity);
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
        var pairs = Candidates(input, _ => true, function => function.Shape, 6, Math.Max(100, input.Length / 4));
        // Checked side by side, joined in the pairs' order (TASK-0107); a copy is no template.
        var similarities = new double[pairs.Length];
        Parallel.For(
            0,
            pairs.Length,
            at =>
            {
                var (left, right) = (input[pairs[at].Left], input[pairs[at].Right]);
                similarities[at] =
                    Nested(left, right) || Overrides(left, right) || Check(left, right, options).Copied
                        ? -1
                        : Alike(left.Shape, right.Shape);
            }
        );
        for (var at = 0; at < pairs.Length; at++)
        {
            if (similarities[at] >= options.Shape)
            {
                groups.Join(pairs[at].Left, pairs[at].Right, similarities[at]);
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

    /// <summary>A thread's LCS table, kept up to <see cref="KeptTable"/> ints (4 MiB); larger ones are not kept.</summary>
    [ThreadStatic]
    private static int[]? s_lengths;

    private const int KeptTable = 1 << 20;

    internal static IReadOnlyList<Gap> Gaps(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.SequenceEqual(right, StringComparer.Ordinal))
        {
            return [];
        }

        // One flat table per thread, grown as needed and reused by every pair it checks (TASK-0107).
        var width = right.Count + 1;
        var size = (left.Count + 1) * width;
        var lengths = s_lengths;
        if (lengths is null || lengths.Length < size)
        {
            lengths = new int[size > KeptTable ? size : Math.Min(KeptTable, Math.Max(size, (lengths?.Length ?? 0) * 2))];
            if (lengths.Length <= KeptTable)
            {
                s_lengths = lengths;
            }
        }

        Array.Clear(lengths, left.Count * width, width);
        for (var i = left.Count - 1; i >= 0; i--)
        {
            var row = i * width;
            var below = row + width;
            lengths[row + right.Count] = 0;
            for (var j = right.Count - 1; j >= 0; j--)
            {
                lengths[row + j] =
                    left[i] == right[j]
                        ? lengths[below + j + 1] + 1
                        : Math.Max(lengths[below + j], lengths[row + j + 1]);
            }
        }
        int Length(int i, int j) => lengths[i * width + j];

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
            else if (Length(a + 1, b) >= Length(a, b + 1))
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

    /// <summary>
    /// Pairs of functions (left &lt; right) whose forms share enough windows of <paramref name="size"/> tokens: at
    /// least 40% of their distinct windows, with lengths within 4:5. Windows shared by more than
    /// <paramref name="common"/> functions say nothing and are skipped. Each distinct token is hashed once and the
    /// window hashes are rolled from the token hashes (TASK-0106); counting runs over sorted arrays.
    /// </summary>
    internal static (int Left, int Right)[] Candidates(
        CloneFunction[] input,
        Func<int, bool> use,
        Func<CloneFunction, IReadOnlyList<string>> form,
        int size,
        int common
    )
    {
        var sets = new ulong[]?[input.Length];
        var lengths = new int[input.Length];
        Dictionary<ulong, List<int>> places = [];
        Dictionary<string, ulong> tokens = new(StringComparer.Ordinal);
        for (var number = 0; number < input.Length; number++)
        {
            var words = form(input[number]);
            lengths[number] = words.Count;
            if (!use(number))
            {
                continue;
            }

            var windows = Windows(words, size, tokens);
            sets[number] = windows;
            foreach (var hash in windows)
            {
                if (!places.TryGetValue(hash, out var list))
                {
                    list = [];
                    places.Add(hash, list);
                }
                list.Add(number);
            }
        }

        // Each list holds its functions in increasing order, as they were added.
        var index = places.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        List<(int Left, int Right)> pairs = [];
        var shared = new int[input.Length];
        List<int> touched = [];
        for (var left = 0; left < input.Length; left++)
        {
            if (sets[left] is not { } hashes)
            {
                continue;
            }

            foreach (var hash in hashes)
            {
                var list = index[hash];
                if (list.Length > common)
                {
                    continue;
                }

                var from = Array.BinarySearch(list, left + 1);
                for (var at = from < 0 ? ~from : from; at < list.Length; at++)
                {
                    if (shared[list[at]]++ is 0)
                    {
                        touched.Add(list[at]);
                    }
                }
            }

            foreach (var right in touched)
            {
                var count = shared[right];
                shared[right] = 0;
                var a = lengths[left];
                var b = lengths[right];
                if (
                    5L * Math.Min(a, b) >= 4L * Math.Max(a, b)
                    && count >= 0.4 * (hashes.Length + sets[right]!.Length - count)
                )
                {
                    pairs.Add((left, right));
                }
            }
            touched.Clear();
        }
        return pairs.OrderBy(pair => pair.Left).ThenBy(pair => pair.Right).ToArray();
    }

    /// <summary>The distinct hashes of every run of <paramref name="size"/> tokens, rolled from the tokens' hashes.</summary>
    private static ulong[] Windows(IReadOnlyList<string> words, int size, Dictionary<string, ulong> tokens)
    {
        if (words.Count < size)
        {
            return [];
        }

        const ulong Base = 1099511628211UL;
        var hashes = new ulong[words.Count];
        for (var at = 0; at < hashes.Length; at++)
        {
            if (!tokens.TryGetValue(words[at], out var hash))
            {
                hash = TokenHash(words[at]);
                tokens.Add(words[at], hash);
            }
            hashes[at] = hash;
        }

        var top = 1UL;
        for (var step = 1; step < size; step++)
        {
            top = unchecked(top * Base);
        }

        HashSet<ulong> windows = new(words.Count - size + 1);
        var rolling = 0UL;
        for (var at = 0; at < hashes.Length; at++)
        {
            rolling = unchecked(rolling * Base + hashes[at]);
            if (at >= size)
            {
                rolling = unchecked(rolling - hashes[at - size] * top * Base);
            }

            if (at >= size - 1)
            {
                windows.Add(Mix(rolling));
            }
        }
        return [.. windows];
    }

    /// <summary>FNV-1a of the token's UTF-8 bytes; ASCII without encoding.</summary>
    private static ulong TokenHash(string token)
    {
        var hash = 14695981039346656037UL;
        foreach (var character in token)
        {
            if (character >= 0x80)
            {
                hash = 14695981039346656037UL;
                foreach (var value in Encoding.UTF8.GetBytes(token))
                {
                    hash = unchecked((hash ^ value) * 1099511628211UL);
                }
                return hash;
            }

            hash = unchecked((hash ^ character) * 1099511628211UL);
        }
        return hash;
    }

    /// <summary>SplitMix64's finalizer: spreads a rolled hash over all bits (one to one, so no new collision).</summary>
    private static ulong Mix(ulong value)
    {
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        return value ^ (value >> 31);
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
