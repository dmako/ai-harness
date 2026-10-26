using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Instructions;
using Harness.Core.Serialization;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Harness.Infrastructure.Clients;

public static class TomlConfigurationEditor
{
    public const string Marker = "# Managed by 'harness install'; 'harness uninstall' removes this block.";
    private const string LayoutError =
        "the harness entry uses a layout harness cannot edit safely (inline table or dotted value); edit it by hand";

    public static TomlTable Parse(string source)
    {
        var syntax = SyntaxParser.Parse(source);
        if (syntax.HasErrors)
        {
            throw new InvalidDataException("invalid TOML: " + syntax.Diagnostics);
        }

        return TomlSerializer.Deserialize(
                source,
                Harness.Infrastructure.Configuration.ConfigurationTomlContext.Default.TomlTable
            ) ?? new();
    }

    private static JsonObject Json(TomlTable table) => HarnessJson.SerializeToNode(table)!.AsObject();

    private static string Name(KeySyntax? key)
    {
        if (key is null)
        {
            throw new InvalidDataException(LayoutError);
        }

        static string Part(SyntaxNode? value) =>
            value switch
            {
                BareKeySyntax bare => bare.Key?.Text ?? throw new InvalidDataException(LayoutError),
                StringValueSyntax quoted => quoted.Value ?? throw new InvalidDataException(LayoutError),
                _ => throw new InvalidDataException(LayoutError),
            };
        return string.Join('.', new[] { Part(key.Key) }.Concat(key.DotKeys.Select(part => Part(part.Key))));
    }

    /// <summary>A [mcp_servers.harness] table or one of its subtables, with its position among all tables.</summary>
    private readonly record struct OwnedTable(TableSyntaxBase Table, int Index, string Key);

    public static TextEdit Edit(string source, ServerSpec? spec)
    {
        var before = Json(Parse(source));
        var entry = before["mcp_servers"]?["harness"] as JsonObject;
        if (spec is not null && Current(entry, spec))
        {
            return new(source, "unchanged");
        }

        var tables = SyntaxParser.Parse(source).Tables.ToArray();
        var owned = OwnedTables(tables);
        if (spec is null && owned.Length is 0)
        {
            return entry is null ? new(source, "absent") : throw new InvalidDataException(LayoutError);
        }

        var edit = spec is null
            ? new TextEdit(Remove(source, tables, owned), "remove")
            : Set(source, spec, owned, entry is not null);
        Verify(before, Json(Parse(edit.Text)), spec);
        return edit;
    }

    private static OwnedTable[] OwnedTables(TableSyntaxBase[] tables) =>
        tables
            .Select((table, index) => new OwnedTable(table, index, Name(table.Name)))
            .Where(t =>
                t.Key is "mcp_servers.harness" || t.Key.StartsWith("mcp_servers.harness.", StringComparison.Ordinal)
            )
            .ToArray();

    /// <summary>
    /// Updates the [mcp_servers.harness] table, or adds one when there is no entry in any other layout
    /// (subtables only, inline table or dotted keys).
    /// </summary>
    private static TextEdit Set(string source, ServerSpec spec, OwnedTable[] owned, bool entry)
    {
        var main = owned.FirstOrDefault(t => t.Key is "mcp_servers.harness").Table;
        if (main is not null)
        {
            return new(Update(source, main, spec), "update");
        }

        return owned.Length > 0 || entry
            ? throw new InvalidDataException(LayoutError)
            : new(Add(source, spec), "add");
    }

    private static bool Current(JsonObject? entry, ServerSpec spec) =>
        entry?["command"]?.GetValue<string>() == spec.Command
        && JsonNode.DeepEquals(entry["args"], HarnessJson.SerializeToNode(spec.Args));

    /// <summary>Cuts each owned table, its marker line and its trailing blank lines; keeps one final newline.</summary>
    private static string Remove(string source, TableSyntaxBase[] tables, OwnedTable[] owned)
    {
        var output = source;
        foreach (var item in owned.Reverse())
        {
            var (start, end) = RemovedRange(source, tables, item);
            output = output[..start] + output[end..];
        }
        if (source.EndsWith('\n'))
        {
            output = string.IsNullOrWhiteSpace(output) ? "" : output.TrimEnd('\n') + "\n";
        }

        return output;
    }

    /// <summary>
    /// From the table's line (or the marker line above it) to the next table, without the blank lines
    /// before that table.
    /// </summary>
    private static (int Start, int End) RemovedRange(string source, TableSyntaxBase[] tables, OwnedTable item)
    {
        var start = LineStart(source, item.Table.OpenBracket!.Span.Offset);
        var end =
            item.Index + 1 < tables.Length
                ? LineStart(source, tables[item.Index + 1].OpenBracket!.Span.Offset)
                : source.Length;
        var previous = start > 0 ? LineStart(source, start - 1) : 0;
        if (source[previous..start].Trim() == Marker)
        {
            start = previous;
        }

        if (end >= source.Length)
        {
            return (start, end);
        }

        while (
            end > item.Table.CloseBracket!.Span.Offset && source[LineStart(source, end - 1)..end].Trim().Length is 0
        )
        {
            end = LineStart(source, end - 1);
        }
        return (start, end);
    }

    /// <summary>Appends the marked harness table after a blank line.</summary>
    private static string Add(string source, ServerSpec spec)
    {
        var output = source;
        if (output.Length > 0 && !output.EndsWith('\n'))
        {
            output += "\n";
        }

        if (!string.IsNullOrWhiteSpace(output) && !output.EndsWith("\n\n", StringComparison.Ordinal))
        {
            output += "\n";
        }

        return output
            + Marker
            + "\n[mcp_servers.harness]\ncommand = "
            + Quote(spec.Command)
            + "\nargs = "
            + Args(spec.Args)
            + "\n";
    }

    /// <summary>
    /// Replaces the command and args values in place; a missing key is added after the table's header
    /// line.
    /// </summary>
    private static string Update(string source, TableSyntaxBase main, ServerSpec spec)
    {
        List<(int Start, int Length, string Text)> edits = [];
        List<string> missing = [];
        foreach (var (key, value) in new[] { ("command", Quote(spec.Command)), ("args", Args(spec.Args)) })
        {
            var item = main.Items.FirstOrDefault(i => Name(i.Key) == key);
            if (item is null)
            {
                missing.Add(key + " = " + value);
            }
            else
            {
                edits.Add((item.Value!.Span.Offset, item.Value.Span.Length, value));
            }
        }
        if (missing.Count > 0)
        {
            edits.Add(MissingKeys(source, main, missing));
        }

        var output = source;
        foreach (var edit in edits.OrderByDescending(e => e.Start))
        {
            output = output[..edit.Start] + edit.Text + output[(edit.Start + edit.Length)..];
        }

        return output;
    }

    private static (int Start, int Length, string Text) MissingKeys(
        string source,
        TableSyntaxBase main,
        List<string> missing
    )
    {
        var end = source.IndexOf('\n', main.CloseBracket!.Span.Offset);
        var newline = MarkedBlock.Newline(source);
        return end < 0
            ? (source.Length, 0, newline + string.Join(newline, missing) + newline)
            : (end + 1, 0, string.Join(newline, missing) + newline);
    }

    private static int LineStart(string source, int offset) =>
        offset <= 0 ? 0 : source.LastIndexOf('\n', offset - 1) + 1;

    private static string Args(IReadOnlyList<string> args) => "[" + string.Join(", ", args.Select(Quote)) + "]";

    private static string Quote(string text)
    {
        if (text.Any(c => c < 0x20 || c is '\x7f'))
        {
            throw new InvalidDataException("command or argument contains control characters");
        }

        return "\""
            + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            + "\"";
    }

    private static void Verify(JsonObject before, JsonObject after, ServerSpec? spec)
    {
        var previous = before["mcp_servers"]?["harness"] as JsonObject;
        var current = after["mcp_servers"]?["harness"] as JsonObject;
        if (
            spec is null
                ? current is not null
                : current?["command"]?.GetValue<string>() != spec.Command
                    || !JsonNode.DeepEquals(current?["args"], HarnessJson.SerializeToNode(spec.Args))
        )
        {
            throw new InvalidDataException("harness entry not as intended after edit; nothing written");
        }

        if (spec is not null && previous is not null)
        {
            if (
                previous
                    .Where(p => p.Key is not ("command" or "args"))
                    .Any(pair => !JsonNode.DeepEquals(pair.Value, current?[pair.Key]))
            )
            {
                throw new InvalidDataException("edit would change another harness key; nothing written");
            }
        }

        ClientConfiguration.Pop(before, ["mcp_servers"]);
        ClientConfiguration.Pop(after, ["mcp_servers"]);
        if (!JsonNode.DeepEquals(before, after))
        {
            throw new InvalidDataException("edit would change unrelated configuration; nothing written");
        }
    }
}
