using System.Globalization;
using System.Text;
using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Clones;

public sealed record SequenceOptions(
    int MinStatements = 2,
    int MinTokens = 14,
    int MinPlain = 21,
    int Places = 3,
    int LongStatements = 5,
    int LongTokens = 50,
    int MaxSeed = 400
);

public static class CloneSequences
{
    internal sealed record Position(int Function, int Unit);

    public static IReadOnlyList<CloneSequence> Find(
        IReadOnlyList<CloneFunction> functions,
        IReadOnlyList<CloneCopy> copies,
        IReadOnlyList<CloneTemplate> templates,
        SequenceOptions? options = null
    )
    {
        options ??= new();
        var sequences = StatementIds(functions);
        var growth = new Growth(functions, sequences, Groups(copies, templates), options);
        foreach (
            var seed in Seeds(functions, sequences)
                .Where(pair =>
                    pair.Value.Count >= 2
                    && pair.Value.Count <= options.MaxSeed
                    && LeftMaximal(functions, sequences, pair.Value)
                )
                .OrderBy(pair => pair.Key.First)
                .ThenBy(pair => pair.Key.Second)
        )
        {
            Grow(growth, seed.Value, 2, 0);
        }

        return Widest(growth.Found);
    }

    /// <summary>Each copied function's copy group ("c" + index), else its template group ("t" + index).</summary>
    private static Dictionary<string, string> Groups(
        IReadOnlyList<CloneCopy> copies,
        IReadOnlyList<CloneTemplate> templates
    )
    {
        Dictionary<string, string> groups = new(StringComparer.Ordinal);
        for (var index = 0; index < copies.Count; index++)
        {
            foreach (var function in copies[index].Functions)
            {
                groups[function.Id] = "c" + index.ToString(CultureInfo.InvariantCulture);
            }
        }

        for (var index = 0; index < templates.Count; index++)
        {
            foreach (var function in templates[index].Functions)
            {
                groups.TryAdd(function.Id, "t" + index.ToString(CultureInfo.InvariantCulture));
            }
        }
        return groups;
    }

    /// <summary>Each function's statements as ids, equal statements sharing one.</summary>
    private static int[][] StatementIds(IReadOnlyList<CloneFunction> functions)
    {
        Dictionary<string, int> ids = new(StringComparer.Ordinal);
        return functions
            .Select(function =>
                function
                    .Statements.Select(unit =>
                    {
                        if (!ids.TryGetValue(unit.Key, out var id))
                        {
                            id = ids.Count;
                            ids.Add(unit.Key, id);
                        }
                        return id;
                    })
                    .ToArray()
            )
            .ToArray();
    }

    /// <summary>Where each pair of adjacent statements starts, unless the first is an idiom.</summary>
    private static Dictionary<(int First, int Second), List<Position>> Seeds(
        IReadOnlyList<CloneFunction> functions,
        int[][] sequences
    )
    {
        Dictionary<(int First, int Second), List<Position>> seeds = [];
        for (var number = 0; number < functions.Count; number++)
        {
            foreach (
                var unit in Enumerable
                    .Range(0, Math.Max(0, functions[number].Statements.Count - 1))
                    .Where(unit => !functions[number].Statements[unit].Idiom)
            )
            {
                var key = (sequences[number][unit], sequences[number][unit + 1]);
                if (!seeds.TryGetValue(key, out var positions))
                {
                    positions = [];
                    seeds.Add(key, positions);
                }
                positions.Add(new(number, unit));
            }
        }
        return seeds;
    }

    /// <summary>What growing shared runs reads, and the sequences it found.</summary>
    private sealed record Growth(
        IReadOnlyList<CloneFunction> Functions,
        int[][] Sequences,
        Dictionary<string, string> CopyGroups,
        SequenceOptions Options
    )
    {
        internal HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
        internal List<CloneSequence> Found { get; } = [];
    }

    /// <summary>
    /// Reports the runs of length at positions once no single next statement continues them all, unless a
    /// parent run of at least half their length was reported; then grows each shared continuation.
    /// </summary>
    private static void Grow(Growth growth, List<Position> positions, int length, int parent)
    {
        var next = positions
            .Where(position => position.Unit + length < growth.Sequences[position.Function].Length)
            .GroupBy(position => growth.Sequences[position.Function][position.Unit + length])
            .Select(group => group.ToList())
            .ToArray();
        var reported = parent;
        if (!next.Any(group => group.Count == positions.Count))
        {
            foreach (var sequence in Runs(growth.Functions, positions, length, growth.CopyGroups, growth.Options))
            {
                if (parent > 0 && sequence.Statements < 2 * parent || !growth.Seen.Add(sequence.Key))
                {
                    continue;
                }

                growth.Found.Add(sequence);
                reported = Math.Max(reported, sequence.Statements);
            }
        }

        foreach (var group in next.Where(group => group.Count >= 2))
        {
            Grow(growth, group, length + 1, reported);
        }
    }

    /// <summary>
    /// The sequences, widest first, without one less than twice as long as a kept one whose functions it
    /// is inside; ordered by size.
    /// </summary>
    private static CloneSequence[] Widest(List<CloneSequence> found)
    {
        List<CloneSequence> output = [];
        List<HashSet<string>> sets = [];
        foreach (
            var sequence in found
                .OrderByDescending(sequence => sequence.Places.Count)
                .ThenByDescending(sequence => sequence.Statements)
        )
        {
            var idsOfFunctions = sequence.Places.Select(place => place.Function.Id).ToHashSet(StringComparer.Ordinal);
            if (
                output
                    .Select((other, index) => (other, index))
                    .Any(pair =>
                        sequence.Statements < 2 * pair.other.Statements && idsOfFunctions.IsSubsetOf(sets[pair.index])
                    )
            )
            {
                continue;
            }

            output.Add(sequence);
            sets.Add(idsOfFunctions);
        }
        return output.OrderByDescending(sequence => sequence.Tokens * sequence.Places.Count).ToArray();
    }

    private static bool LeftMaximal(IReadOnlyList<CloneFunction> functions, int[][] sequences, List<Position> positions)
    {
        for (var back = 1; back <= positions.Min(position => position.Unit) + 1; back++)
        {
            if (
                positions.Any(position =>
                    position.Unit - back < 0
                    || sequences[position.Function][position.Unit - back]
                        != sequences[positions[0].Function][positions[0].Unit - back]
                )
            )
            {
                return true;
            }

            if (!functions[positions[0].Function].Statements[positions[0].Unit - back].Idiom)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A run's statements (trailing idioms dropped) and what its non-idiom statements weigh.</summary>
    private sealed record Run(List<CloneStatement> Units, int Statements, int Tokens, bool Data)
    {
        public int Length => Units.Count;

        public static Run Of(CloneFunction function, int start, int length)
        {
            var units = function.Statements.Skip(start).Take(length).ToList();
            while (units.Count > 0 && units[^1].Idiom)
            {
                units.RemoveAt(units.Count - 1);
            }

            return new(
                units,
                units.Count(unit => !unit.Idiom && unit.Work),
                units.Where(unit => !unit.Idiom).Sum(unit => unit.Size),
                units.Any(unit => !unit.Idiom && unit.Data)
            );
        }

        /// <summary>
        /// Large enough (plain runs need more tokens than runs with data), and not one repeated call.
        /// </summary>
        public bool Reportable(SequenceOptions options) =>
            Statements >= options.MinStatements
            && Tokens >= options.MinTokens
            && (Data || Tokens >= options.MinPlain)
            && !OneCall(Units);

        public bool Long(SequenceOptions options) =>
            Statements >= options.LongStatements && Tokens >= options.LongTokens;

        private static bool OneCall(List<CloneStatement> units)
        {
            var name = "";
            foreach (var unit in units)
            {
                if (unit.Header)
                {
                    return false;
                }

                if (!unit.Work)
                {
                    continue;
                }

                if (unit.Callee.Length is 0 || name.Length > 0 && unit.Callee != name)
                {
                    return false;
                }

                name = unit.Callee;
            }
            return name.Length > 0;
        }
    }

    private static List<CloneSequence> Runs(
        IReadOnlyList<CloneFunction> functions,
        List<Position> positions,
        int length,
        Dictionary<string, string> groups,
        SequenceOptions options
    )
    {
        var run = Run.Of(functions[positions[0].Function], positions[0].Unit, length);
        if (!run.Reportable(options))
        {
            return [];
        }

        List<CloneSequence> output = [];
        foreach (
            var part in DistinctPositions(functions, positions, run.Length, groups)
                .GroupBy(position => OwnKey(functions, position, run.Length), StringComparer.Ordinal)
        )
        {
            var count = part.Count();
            if (count < options.Places && !(run.Long(options) && count >= 2))
            {
                continue;
            }

            output.Add(Sequence(functions, part.ToArray(), run, part.Key));
        }
        return output;
    }

    /// <summary>
    /// One position per copy group and per source line, without runs that overlap an earlier one in the
    /// same function.
    /// </summary>
    private static List<Position> DistinctPositions(
        IReadOnlyList<CloneFunction> functions,
        List<Position> positions,
        int length,
        Dictionary<string, string> groups
    )
    {
        HashSet<string> taken = new(StringComparer.Ordinal);
        Dictionary<int, int> ends = [];
        List<Position> kept = [];
        foreach (var position in positions)
        {
            var function = functions[position.Function];
            var group = groups.GetValueOrDefault(function.Id, "");
            var where =
                function.Path + ":" + function.Statements[position.Unit].Line.ToString(CultureInfo.InvariantCulture);
            if (
                group.Length > 0 && taken.Contains(group)
                || taken.Contains(where)
                || ends.TryGetValue(position.Function, out var end) && position.Unit < end
            )
            {
                continue;
            }

            taken.Add(where);
            ends[position.Function] = position.Unit + length;
            if (group.Length > 0)
            {
                taken.Add(group);
            }

            kept.Add(position);
        }
        return kept;
    }

    /// <summary>The pattern of the run's own names: each name numbered by its first appearance.</summary>
    private static string OwnKey(IReadOnlyList<CloneFunction> functions, Position position, int length)
    {
        Dictionary<string, int> names = new(StringComparer.Ordinal);
        StringBuilder key = new();
        foreach (
            var name in functions[position.Function]
                .Statements.Skip(position.Unit)
                .Take(length)
                .SelectMany(unit => unit.Own)
        )
        {
            if (!names.TryGetValue(name, out var id))
            {
                id = names.Count;
                names.Add(name, id);
            }
            key.Append(id.ToString(CultureInfo.InvariantCulture)).Append(',');
        }
        return key.ToString();
    }

    /// <summary>
    /// The run at each position, with the numbers that differ between them and the function it fills.
    /// </summary>
    private static CloneSequence Sequence(
        IReadOnlyList<CloneFunction> functions,
        Position[] positions,
        Run run,
        string ownKey
    )
    {
        var values = NumberValues(functions, positions, run.Length);
        List<ClonePlace> places = [];
        CloneFunction? holder = null;
        foreach (var position in positions)
        {
            var function = functions[position.Function];
            var differs = Differs(function, position.Unit, run.Length, values);
            places.Add(
                new(
                    function,
                    function.Statements[position.Unit].Line,
                    differs.Count is 0 ? null : differs.Take(4).ToArray()
                )
            );
            if (holder is null && Holds(function, position.Unit, run.Length))
            {
                holder = function;
            }
        }
        return new(
            places,
            run.Statements,
            run.Tokens,
            run.Units.Select(unit => Render(unit.Raw)).ToArray(),
            run.Data,
            holder,
            string.Join('\n', run.Units.Select(unit => unit.Key)) + "\n" + ownKey
        );
    }

    /// <summary>
    /// For each (statement, token) slot that holds a number in some position, the numbers seen there.
    /// </summary>
    private static Dictionary<(int Unit, int Token), HashSet<string>> NumberValues(
        IReadOnlyList<CloneFunction> functions,
        Position[] positions,
        int length
    )
    {
        Dictionary<(int Unit, int Token), HashSet<string>> values = [];
        foreach (var position in positions)
        {
            for (var number = 0; number < length; number++)
            {
                var raw = functions[position.Function].Statements[position.Unit + number].Raw;
                for (var token = 0; token < raw.Count; token++)
                {
                    if (raw[token].Length is 0 || !char.IsAsciiDigit(raw[token][0]))
                    {
                        continue;
                    }

                    if (!values.TryGetValue((number, token), out var set))
                    {
                        set = new(StringComparer.Ordinal);
                        values.Add((number, token), set);
                    }
                    set.Add(raw[token]);
                }
            }
        }
        return values;
    }

    /// <summary>This position's tokens in slots where the positions hold different numbers, each once.</summary>
    private static List<string> Differs(
        CloneFunction function,
        int start,
        int length,
        Dictionary<(int Unit, int Token), HashSet<string>> values
    )
    {
        List<string> differs = [];
        for (var number = 0; number < length; number++)
        {
            var raw = function.Statements[start + number].Raw;
            for (var token = 0; token < raw.Count; token++)
            {
                if (
                    values.TryGetValue((number, token), out var set)
                    && set.Count > 1
                    && !differs.Contains(raw[token], StringComparer.Ordinal)
                )
                {
                    differs.Add(raw[token]);
                }
            }
        }
        return differs;
    }


    private static bool Holds(CloneFunction function, int start, int length)
    {
        var last = function
            .Statements.Select((unit, index) => (unit, index))
            .Where(pair => !pair.unit.Idiom)
            .Select(pair => pair.index)
            .DefaultIfEmpty(-1)
            .Max();
        return !function
            .Statements.Select((unit, index) => (unit, index))
            .Any(pair =>
                (pair.index < start || pair.index >= start + length)
                && !pair.unit.Idiom
                && !(pair.index == last && pair.index >= start + length && pair.unit.Raw[0] is "return")
            );
    }

    internal static string Render(IReadOnlyList<string> raw)
    {
        StringBuilder output = new();
        for (var index = 0; index < raw.Count; index++)
        {
            var token = raw[index];
            if (index > 0)
            {
                var previous = raw[index - 1];
                var unary = previous is "*" or "&" or "<-" or "-" && (index < 2 || !Word(raw[index - 2]));
                var tight =
                    "([.!".Contains(previous[^1], StringComparison.Ordinal) && previous is not "!="
                    || unary
                    || previous is "]" && index >= 2 && raw[index - 2] is "["
                    || ")].,;:".Contains(token[0], StringComparison.Ordinal) && token is not (":=" or "::")
                    || token is "(" or "[" && Word(previous)
                    || token is "++" or "--" or "?."
                    || previous is "?.";
                if (!tight)
                {
                    output.Append(' ');
                }
            }
            output.Append(token);
        }
        return output.ToString();
    }

    private static bool Word(string text) =>
        text.Length > 0 && (char.IsAsciiLetterOrDigit(text[^1]) || text[^1] is '_' or '>' or ')' or ']');
}
