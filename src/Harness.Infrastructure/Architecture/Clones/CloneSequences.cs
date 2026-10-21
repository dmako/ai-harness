using System.Globalization;
using System.Text;
using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Clones;

public sealed record SequenceOptions(int MinStatements = 2, int MinTokens = 14, int MinPlain = 21, int Places = 3, int LongStatements = 5, int LongTokens = 50, int MaxSeed = 400);

public static class CloneSequences
{
    private sealed record Position(int Function, int Unit);
    public static IReadOnlyList<CloneSequence> Find(IReadOnlyList<CloneFunction> functions, IReadOnlyList<CloneCopy> copies, IReadOnlyList<CloneTemplate> templates, SequenceOptions? options = null)
    {
        options ??= new();
        Dictionary<string, string> groups = new(StringComparer.Ordinal);
        for (var index = 0; index < copies.Count; index++)
            foreach (var function in copies[index].Functions)
                groups[function.Id] = "c" + index.ToString(CultureInfo.InvariantCulture);
        for (var index = 0; index < templates.Count; index++)
            foreach (var function in templates[index].Functions)
                groups.TryAdd(function.Id, "t" + index.ToString(CultureInfo.InvariantCulture));
        Dictionary<string, int> ids = new(StringComparer.Ordinal);
        var sequences = functions.Select(function => function.Statements.Select(unit =>
        {
            if (!ids.TryGetValue(unit.Key, out var id))
            { id = ids.Count; ids.Add(unit.Key, id); }
            return id;
        }).ToArray()).ToArray();
        Dictionary<(int First, int Second), List<Position>> seeds = [];
        for (var number = 0; number < functions.Count; number++)
            foreach (var unit in Enumerable.Range(0, Math.Max(0, functions[number].Statements.Count - 1)).Where(unit => !functions[number].Statements[unit].Idiom))
            {
                var key = (sequences[number][unit], sequences[number][unit + 1]);
                if (!seeds.TryGetValue(key, out var positions))
                { positions = []; seeds.Add(key, positions); }
                positions.Add(new(number, unit));
            }
        List<CloneSequence> found = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        void Grow(List<Position> positions, int length, int parent)
        {
            var next = positions.Where(position => position.Unit + length < sequences[position.Function].Length).GroupBy(position => sequences[position.Function][position.Unit + length]).Select(group => group.ToList()).ToArray();
            var reported = parent;
            if (!next.Any(group => group.Count == positions.Count))
                foreach (var sequence in Runs(functions, positions, length, groups, options))
                {
                    if (parent > 0 && sequence.Statements < 2 * parent || !seen.Add(sequence.Key))
                        continue;
                    found.Add(sequence);
                    reported = Math.Max(reported, sequence.Statements);
                }
            foreach (var group in next.Where(group => group.Count >= 2))
                Grow(group, length + 1, reported);
        }
        foreach (var seed in seeds.Where(pair => pair.Value.Count >= 2 && pair.Value.Count <= options.MaxSeed && LeftMaximal(functions, sequences, pair.Value)).OrderBy(pair => pair.Key.First).ThenBy(pair => pair.Key.Second))
            Grow(seed.Value, 2, 0);
        List<CloneSequence> output = [];
        List<HashSet<string>> sets = [];
        foreach (var sequence in found.OrderByDescending(sequence => sequence.Places.Count).ThenByDescending(sequence => sequence.Statements))
        {
            var idsOfFunctions = sequence.Places.Select(place => place.Function.Id).ToHashSet(StringComparer.Ordinal);
            if (output.Select((other, index) => (other, index)).Any(pair => sequence.Statements < 2 * pair.other.Statements && idsOfFunctions.IsSubsetOf(sets[pair.index])))
                continue;
            output.Add(sequence);
            sets.Add(idsOfFunctions);
        }
        return output.OrderByDescending(sequence => sequence.Tokens * sequence.Places.Count).ToArray();
    }
    private static bool LeftMaximal(IReadOnlyList<CloneFunction> functions, int[][] sequences, List<Position> positions)
    {
        for (var back = 1; back <= positions.Min(position => position.Unit) + 1; back++)
        {
            if (positions.Any(position => position.Unit - back < 0 || sequences[position.Function][position.Unit - back] != sequences[positions[0].Function][positions[0].Unit - back]))
                return true;
            if (!functions[positions[0].Function].Statements[positions[0].Unit - back].Idiom)
                return false;
        }
        return true;
    }
    private static List<CloneSequence> Runs(IReadOnlyList<CloneFunction> functions, List<Position> positions, int length, Dictionary<string, string> groups, SequenceOptions options)
    {
        var units = functions[positions[0].Function].Statements.Skip(positions[0].Unit).Take(length).ToList();
        while (units.Count > 0 && units[^1].Idiom)
            units.RemoveAt(units.Count - 1);
        length = units.Count;
        var statements = units.Count(unit => !unit.Idiom && unit.Work);
        var tokens = units.Where(unit => !unit.Idiom).Sum(unit => unit.Size);
        var data = units.Any(unit => !unit.Idiom && unit.Data);
        if (statements < options.MinStatements || tokens < options.MinTokens || !data && tokens < options.MinPlain || OneCall(units))
            return [];
        HashSet<string> taken = new(StringComparer.Ordinal);
        Dictionary<int, int> ends = [];
        List<Position> kept = [];
        foreach (var position in positions)
        {
            var function = functions[position.Function];
            var group = groups.GetValueOrDefault(function.Id, "");
            var where = function.Path + ":" + function.Statements[position.Unit].Line.ToString(CultureInfo.InvariantCulture);
            if (group.Length > 0 && taken.Contains(group) || taken.Contains(where) || ends.TryGetValue(position.Function, out var end) && position.Unit < end)
                continue;
            taken.Add(where);
            ends[position.Function] = position.Unit + length;
            if (group.Length > 0)
                taken.Add(group);
            kept.Add(position);
        }
        string OwnKey(Position position)
        {
            Dictionary<string, int> names = new(StringComparer.Ordinal);
            StringBuilder key = new();
            foreach (var name in functions[position.Function].Statements.Skip(position.Unit).Take(length).SelectMany(unit => unit.Own))
            {
                if (!names.TryGetValue(name, out var id))
                { id = names.Count; names.Add(name, id); }
                key.Append(id.ToString(CultureInfo.InvariantCulture)).Append(',');
            }
            return key.ToString();
        }
        List<CloneSequence> output = [];
        foreach (var part in kept.GroupBy(OwnKey, StringComparer.Ordinal))
        {
            var longRun = statements >= options.LongStatements && tokens >= options.LongTokens;
            if (part.Count() < options.Places && !(longRun && part.Count() >= 2))
                continue;
            var positionsOfPart = part.ToArray();
            Dictionary<(int Unit, int Token), HashSet<string>> values = [];
            foreach (var position in positionsOfPart)
                for (var number = 0; number < length; number++)
                {
                    var raw = functions[position.Function].Statements[position.Unit + number].Raw;
                    for (var token = 0; token < raw.Count; token++)
                        if (raw[token].Length > 0 && char.IsAsciiDigit(raw[token][0]))
                        {
                            var key = (number, token);
                            if (!values.TryGetValue(key, out var set))
                            { set = new(StringComparer.Ordinal); values.Add(key, set); }
                            set.Add(raw[token]);
                        }
                }
            List<ClonePlace> places = [];
            CloneFunction? holder = null;
            foreach (var position in positionsOfPart)
            {
                var function = functions[position.Function];
                List<string> differs = [];
                for (var number = 0; number < length; number++)
                {
                    var raw = function.Statements[position.Unit + number].Raw;
                    for (var token = 0; token < raw.Count; token++)
                        if (values.TryGetValue((number, token), out var set) && set.Count > 1 && !differs.Contains(raw[token], StringComparer.Ordinal))
                            differs.Add(raw[token]);
                }
                places.Add(new(function, function.Statements[position.Unit].Line, differs.Count is 0 ? null : differs.Take(4).ToArray()));
                if (holder is null && Holds(function, position.Unit, length))
                    holder = function;
            }
            output.Add(new(places, statements, tokens, units.Select(unit => Render(unit.Raw)).ToArray(), data, holder, string.Join('\n', units.Select(unit => unit.Key)) + "\n" + part.Key));
        }
        return output;
    }
    private static bool OneCall(List<CloneStatement> units)
    {
        var name = "";
        foreach (var unit in units)
        {
            if (unit.Header)
                return false;
            if (!unit.Work)
                continue;
            if (unit.Callee.Length is 0 || name.Length > 0 && unit.Callee != name)
                return false;
            name = unit.Callee;
        }
        return name.Length > 0;
    }
    private static bool Holds(CloneFunction function, int start, int length)
    {
        var last = function.Statements.Select((unit, index) => (unit, index)).Where(pair => !pair.unit.Idiom).Select(pair => pair.index).DefaultIfEmpty(-1).Max();
        return !function.Statements.Select((unit, index) => (unit, index)).Any(pair => (pair.index < start || pair.index >= start + length) && !pair.unit.Idiom && !(pair.index == last && pair.index >= start + length && pair.unit.Raw[0] is "return"));
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
                var tight = "([.!".Contains(previous[^1], StringComparison.Ordinal) && previous is not "!=" || unary || previous is "]" && index >= 2 && raw[index - 2] is "[" || ")].,;:".Contains(token[0], StringComparison.Ordinal) && token is not (":=" or "::") || token is "(" or "[" && Word(previous) || token is "++" or "--" or "?." || previous is "?.";
                if (!tight)
                    output.Append(' ');
            }
            output.Append(token);
        }
        return output.ToString();
    }
    private static bool Word(string text) => text.Length > 0 && (char.IsAsciiLetterOrDigit(text[^1]) || text[^1] is '_' or '>' or ')' or ']');
}
