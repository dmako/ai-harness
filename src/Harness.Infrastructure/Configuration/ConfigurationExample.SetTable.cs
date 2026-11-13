using System.Text.Json.Nodes;
using Harness.Core.Serialization;
using Tomlyn;
using Tomlyn.Parsing;

namespace Harness.Infrastructure.Configuration;

public static partial class ConfigurationExample
{
    /// <summary>
    /// Sets keys of one table in a config.toml line by line, keeping everything else as written (comments, other
    /// tables, the table's other keys and its commented examples): a key the table has is rewritten on its line, a
    /// missing one is added after the table's last key, and a missing table is added at the end. A key that already
    /// has the value is left alone, so setting the same keys twice changes nothing. Added lists the keys set
    /// ("table.key"); a file that is not valid TOML is left alone with a Problem.
    /// </summary>
    public static ConfigurationUpdate SetTable(
        string text,
        string table,
        IReadOnlyList<(string Key, string Value)> keys
    )
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keys);
        if (SyntaxParser.Parse(text).HasErrors)
        {
            return new(text, [], [], "not valid TOML: fix it first");
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] raw = text.Length is 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        // a file that ends with a line break keeps it; a new file gets one
        var ending = raw.Length is 0 || raw[^1].Length is 0;
        var lines = Classify(raw.Length > 0 && raw[^1].Length is 0 ? raw[..^1] : raw);
        var output = lines.Select(line => line.Source).ToList();
        List<string> set = [];
        var start = lines.FindIndex(line => line.Kind is Kind.Header && line.Table == table);
        if (start < 0)
        {
            if (output.Count > 0 && output[^1].Trim().Length > 0)
            {
                output.Add("");
            }

            output.Add("[" + table + "]");
            foreach (var (key, value) in keys)
            {
                output.Add($"{key} = {value}");
                set.Add(table + "." + key);
            }
        }
        else
        {
            var stop = start + 1;
            while (stop < lines.Count && !(lines[stop].Kind is Kind.Header || IsTableComment(lines[stop])))
            {
                stop++;
            }

            // edits from the bottom up keep the indexes above them valid
            List<(int At, int Length, string[] Text)> edits = [];
            var last = start;
            for (var index = start + 1; index < stop; index++)
            {
                if (lines[index].Kind is Kind.Key or Kind.Continuation)
                {
                    last = index;
                }
            }

            List<string> appended = [];
            foreach (var (key, value) in keys)
            {
                var at = -1;
                for (var index = start + 1; index < stop && at < 0; index++)
                {
                    if (lines[index].Kind is Kind.Key && lines[index].Key == key)
                    {
                        at = index;
                    }
                }

                if (at < 0)
                {
                    appended.Add($"{key} = {value}");
                    set.Add(table + "." + key);
                    continue;
                }

                var length = 1;
                while (at + length < lines.Count && lines[at + length].Kind is Kind.Continuation)
                {
                    length++;
                }

                var current = string.Join("\n", lines.Skip(at).Take(length).Select(line => line.Source));
                if (SameValue(current, $"{key} = {value}"))
                {
                    continue;
                }

                var indent = lines[at].Source[..(lines[at].Source.Length - lines[at].Source.TrimStart().Length)];
                edits.Add((at, length, [$"{indent}{key} = {value}"]));
                set.Add(table + "." + key);
            }

            if (appended.Count > 0)
            {
                edits.Add((last + 1, 0, [.. appended]));
            }

            foreach (var (at, length, replacement) in edits.OrderByDescending(edit => edit.At))
            {
                output.RemoveRange(at, length);
                output.InsertRange(at, replacement);
            }
        }

        if (set.Count is 0)
        {
            return new(text, [], []);
        }

        var updated = string.Join(newline, output) + (ending ? newline : "");
        return SyntaxParser.Parse(updated).HasErrors
            ? new(text, [], [], "could not be changed safely; it is left as it was")
            : new(updated, set, []);
    }

    /// <summary>A commented table header ends the table above it as far as an edit goes: keys go above it.</summary>
    private static bool IsTableComment(Line line) => line.Kind is Kind.CommentedHeader;

    /// <summary>Whether two "key = value" snippets give the key the same value.</summary>
    private static bool SameValue(string current, string wanted)
    {
        try
        {
            return JsonNode.DeepEquals(Node(current), Node(wanted));
        }
        catch (TomlException)
        {
            return false;
        }
    }

    private static JsonNode? Node(string snippet) =>
        HarnessJson.SerializeToNode(TomlSerializer.Deserialize(snippet, ConfigurationTomlContext.Default.TomlTable));

    /// <summary>The lines of a change, as 'harness doctor --fix' and Health show it before it is written.</summary>
    public static IReadOnlyList<string> Changes(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var old = Split(before);
        var updated = Split(after);
        var prefix = 0;
        while (prefix < old.Length && prefix < updated.Length && old[prefix] == updated[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (
            suffix < old.Length - prefix
            && suffix < updated.Length - prefix
            && old[^(suffix + 1)] == updated[^(suffix + 1)]
        )
        {
            suffix++;
        }

        var text = new List<string>();
        text.AddRange(old[prefix..^suffix].Select(line => "- " + line));
        text.AddRange(updated[prefix..^suffix].Select(line => "+ " + line));
        return text;

        static string[] Split(string value)
        {
            var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            return lines.Length > 0 && lines[^1].Length is 0 ? lines[..^1] : lines;
        }
    }
}
