using System.Text;
using System.Text.RegularExpressions;
using Tomlyn.Parsing;

namespace Harness.Infrastructure.Configuration;

/// <summary>
/// What bringing a config.toml up to date changed: the options added as commented examples and the options (or
/// whole tables) removed because this harness no longer has them. Problem says why a file was left alone.
/// </summary>
public sealed record ConfigurationUpdate(
    string Text,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    string Problem = ""
)
{
    public bool Changed => Added.Count + Removed.Count > 0;
}

public static partial class ConfigurationExample
{
    [GeneratedRegex(@"\A\[\[?\s*(?<name>[^\]#]+?)\s*\]\]?\s*(?:#.*)?\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HeaderLine { get; }

    [GeneratedRegex(@"\A(?<key>[A-Za-z0-9_-]+)\s*=\s*\S", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CommentedKey { get; }

    [GeneratedRegex(@"\A(?:(?<key>[A-Za-z0-9_-]+)|[^=#]+?)\s*=", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex KeyLine { get; }

    /// <summary>
    /// Brings a config.toml up to date line by line, keeping its layout: an option missing from a table is added as
    /// a commented example under it (a missing table at the end), an option or table this harness no longer has is
    /// removed, commented or not. Everything else stays as written. A file that is not valid TOML is left alone.
    /// </summary>
    public static ConfigurationUpdate Update(string text) => Update(text, Dropped);

    /// <summary>As <see cref="Update(string)"/>, with these options and tables ("[table]") counted as dropped.</summary>
    internal static ConfigurationUpdate Update(string text, IReadOnlyCollection<string> dropped)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(dropped);
        if (SyntaxParser.Parse(text).HasErrors)
        {
            return new(text, [], [], "not valid TOML: fix it, and 'harness init' brings it up to date");
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var raw = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var ending = raw[^1].Length is 0 && raw.Length > 1;
        var lines = Classify(ending ? raw[..^1] : raw);
        List<string> removed = [];
        Remove(lines, dropped, removed);
        List<string> added = [];
        var after = Add(lines, added);
        if (added.Count + removed.Count is 0)
        {
            return new(text, [], []);
        }

        var output = new StringBuilder();
        for (var index = -1; index < lines.Count; index++)
        {
            if (index >= 0 && !lines[index].Removed)
            {
                output.Append(lines[index].Source).Append(newline);
            }

            foreach (var line in after.GetValueOrDefault(index, []))
            {
                output.Append(line).Append(newline);
            }
        }

        var updated = output.ToString();
        if (!ending)
        {
            updated = updated[..^newline.Length];
        }

        return SyntaxParser.Parse(updated).HasErrors
            ? new(text, [], [], "could not be brought up to date safely; it is left as it was")
            : new(updated, added, removed);
    }

    private enum Kind
    {
        Other,
        Header,
        CommentedHeader,
        Key,
        CommentedKey,
        Continuation,
    }

    private sealed class Line(string text, Kind kind, string table, string key)
    {
        public string Source { get; } = text;
        public Kind Kind { get; } = kind;
        public string Table { get; } = table;
        public string Key { get; } = key;
        public bool Removed { get; set; }
    }

    private static List<Line> Classify(string[] raw)
    {
        List<Line> lines = [];
        var table = "";
        var index = 0;
        while (index < raw.Length)
        {
            var text = raw[index++];
            var trimmed = text.Trim();
            if (trimmed.StartsWith('#', StringComparison.Ordinal))
            {
                var body = trimmed[1..].Trim();
                if (HeaderLine.Match(body) is { Success: true } commented)
                {
                    table = TableName(commented.Groups["name"].Value);
                    lines.Add(new(text, Kind.CommentedHeader, table, ""));
                }
                else if (CommentedKey.Match(body) is { Success: true } key)
                {
                    lines.Add(new(text, Kind.CommentedKey, table, key.Groups["key"].Value));
                }
                else
                {
                    lines.Add(new(text, Kind.Other, table, ""));
                }

                continue;
            }

            if (HeaderLine.Match(trimmed) is { Success: true } header)
            {
                table = TableName(header.Groups["name"].Value);
                lines.Add(new(text, Kind.Header, table, ""));
                continue;
            }

            if (KeyLine.Match(trimmed) is not { Success: true } assignment)
            {
                lines.Add(new(text, Kind.Other, table, ""));
                continue;
            }

            // a quoted or dotted key is kept as written: only bare keys are checked
            lines.Add(new(text, Kind.Key, table, assignment.Groups["key"].Value));
            var value = new ValueScanner();
            value.Read(trimmed[(trimmed.IndexOf('=', StringComparison.Ordinal) + 1)..]);
            while (value.Open && index < raw.Length)
            {
                value.Read(raw[index]);
                lines.Add(new(raw[index++], Kind.Continuation, table, ""));
            }
        }

        return lines;
    }

    /// <summary>
    /// Removes the commented examples of options and tables this harness does not have, and the settings of the
    /// ones it dropped. Any other setting it does not know (a typo) stays, so loading the file names it.
    /// </summary>
    private static void Remove(List<Line> lines, IReadOnlyCollection<string> dropped, List<string> removed)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            switch (line.Kind)
            {
                case Kind.CommentedHeader when !Known(line.Table):
                case Kind.Header when !Known(line.Table) && dropped.Contains("[" + line.Table + "]"):
                    removed.Add("[" + line.Table + "]");
                    line.Removed = true;
                    for (var next = index + 1; next < lines.Count && !IsHeader(lines[next]); next++)
                    {
                        lines[next].Removed |= lines[next].Kind is not Kind.Other;
                    }

                    break;
                case Kind.CommentedKey when Known(line.Table) && !Accepts(line.Table, line.Key):
                case Kind.Key
                    when line.Key.Length > 0
                        && Known(line.Table)
                        && !Accepts(line.Table, line.Key)
                        && dropped.Contains(Qualified(line.Table, line.Key)):
                    removed.Add(Qualified(line.Table, line.Key));
                    line.Removed = true;
                    for (var next = index + 1; next < lines.Count && lines[next].Kind is Kind.Continuation; next++)
                    {
                        lines[next].Removed = true;
                    }

                    break;
            }
        }
    }

    private static Dictionary<int, List<string>> Add(List<Line> lines, List<string> added)
    {
        Dictionary<int, List<string>> after = [];
        foreach (var table in Tables)
        {
            var start = lines.FindIndex(line => !line.Removed && IsHeader(line) && line.Table == table.Name);
            if (start < 0)
            {
                var last = lines.FindLastIndex(line => !line.Removed);
                List<string> block = last >= 0 && lines[last].Source.Trim().Length > 0 ? [""] : [];
                block.AddRange(Lines(table, table.Keys));
                if (!after.TryGetValue(lines.Count - 1, out var end))
                {
                    after[lines.Count - 1] = end = [];
                }

                end.AddRange(block);
                added.Add("[" + table.Name + "]");
                continue;
            }

            var stop = start + 1;
            var place = start;
            HashSet<string> present = new(StringComparer.Ordinal);
            for (; stop < lines.Count && !(IsHeader(lines[stop]) && !lines[stop].Removed); stop++)
            {
                var line = lines[stop];
                if (line.Removed)
                {
                    continue;
                }

                if (line.Kind is Kind.Key or Kind.CommentedKey or Kind.Continuation)
                {
                    place = stop;
                    if (line.Key.Length > 0)
                    {
                        present.Add(line.Key);
                    }
                }
            }

            var missing = table.Keys.Where(key => !present.Contains(key.Name)).ToArray();
            if (missing.Length is 0)
            {
                continue;
            }

            if (!after.TryGetValue(place, out var into))
            {
                after[place] = into = [];
            }

            into.AddRange(Lines(table, missing).Skip(1));
            added.AddRange(missing.Select(key => Qualified(table.Name, key.Name)));
        }

        return after;
    }

    private static bool IsHeader(Line line) => line.Kind is Kind.Header or Kind.CommentedHeader;

    private static string Qualified(string table, string key) => table.Length is 0 ? key : table + "." + key;

    private static bool Known(string table) =>
        Tables.Any(example => example.Name == table) || Schema.Any(entry => Matches(entry.Pattern, table));

    private static bool Accepts(string table, string key) =>
        Tables.Any(example => example.Name == table && example.Keys.Any(option => option.Name == key))
        || Schema.Any(entry => Matches(entry.Pattern, table) && (entry.Keys is null || entry.Keys.Contains(key)));

    private static bool Matches(string pattern, string table)
    {
        var expected = Segments(pattern);
        var actual = Segments(table);
        return expected.Count == actual.Count
            && expected.Zip(actual).All(pair => pair.First == "*" || pair.First == Unquoted(pair.Second));
    }

    private static string Unquoted(string segment) =>
        segment.Length >= 2 && segment[0] is '"' or '\'' && segment[^1] == segment[0] ? segment[1..^1] : segment;

    /// <summary>A table name without the spaces TOML allows around its dots.</summary>
    private static string TableName(string name) => string.Join('.', Segments(name).Select(part => part.Trim()));

    private static List<string> Segments(string name)
    {
        List<string> segments = [];
        if (name.Length is 0)
        {
            return segments;
        }

        var current = new StringBuilder();
        var quote = '\0';
        foreach (var character in name)
        {
            if (quote is '\0' && character is '.')
            {
                segments.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            if (quote is '\0' && character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == quote)
            {
                quote = '\0';
            }

            current.Append(character);
        }

        segments.Add(current.ToString().Trim());
        return segments;
    }

    /// <summary>Follows a TOML value across lines: open brackets, braces and multi-line strings.</summary>
    private sealed class ValueScanner
    {
        private int _depth;
        private string _string = "";

        public bool Open => _depth > 0 || _string is "\"\"\"" or "'''";

        public void Read(string text)
        {
            var index = 0;
            while (index < text.Length)
            {
                index += Step(text, index);
            }

            // a single-line string cannot continue on the next line
            if (_string is "\"" or "'")
            {
                _string = "";
            }
        }

        private int Step(string text, int index)
        {
            var rest = text.AsSpan(index);
            if (_string.Length > 0)
            {
                if (_string is "\"" or "\"\"\"" && text[index] is '\\')
                {
                    return 2;
                }

                if (rest.StartsWith(_string, StringComparison.Ordinal))
                {
                    var length = _string.Length;
                    _string = "";
                    return length;
                }

                return 1;
            }

            if (rest.StartsWith("\"\"\"", StringComparison.Ordinal) || rest.StartsWith("'''", StringComparison.Ordinal))
            {
                _string = rest[..3].ToString();
                return 3;
            }

            switch (text[index])
            {
                case '"' or '\'':
                    _string = text[index].ToString();
                    break;
                case '#':
                    return text.Length - index;
                case '[' or '{':
                    _depth++;
                    break;
                case ']' or '}':
                    _depth--;
                    break;
            }

            return 1;
        }
    }
}
