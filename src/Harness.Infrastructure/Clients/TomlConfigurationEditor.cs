using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Instructions;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Harness.Infrastructure.Clients;

public static class TomlConfigurationEditor
{
    public const string Marker = "# Managed by 'harness install'; 'harness uninstall' removes this block.";
    private const string LayoutError = "the harness entry uses a layout harness cannot edit safely (inline table or dotted value); edit it by hand";
    public static TomlTable Parse(string source)
    {
        var syntax = SyntaxParser.Parse(source);
        if (syntax.HasErrors)
            throw new InvalidDataException("invalid TOML: " + syntax.Diagnostics);
        return TomlSerializer.Deserialize<TomlTable>(source) ?? new();
    }
    private static JsonObject Json(TomlTable table) => JsonSerializer.SerializeToNode(table)!.AsObject();
    private static string Name(KeySyntax? key)
    {
        if (key is null)
            throw new InvalidDataException(LayoutError);
        static string Part(SyntaxNode? value) => value switch { BareKeySyntax bare => bare.Key?.Text ?? throw new InvalidDataException(LayoutError), StringValueSyntax quoted => quoted.Value ?? throw new InvalidDataException(LayoutError), _ => throw new InvalidDataException(LayoutError) };
        return string.Join('.', new[] { Part(key.Key) }.Concat(key.DotKeys.Select(part => Part(part.Key))));
    }
    public static TextEdit Edit(string source, ServerSpec? spec)
    {
        var before = Json(Parse(source));
        var entry = before["mcp_servers"]?["harness"] as JsonObject;
        if (spec is not null && entry?["command"]?.GetValue<string>() == spec.Command && JsonNode.DeepEquals(entry["args"], JsonSerializer.SerializeToNode(spec.Args)))
            return new(source, "unchanged");
        var document = SyntaxParser.Parse(source);
        var tables = document.Tables.ToArray();
        var owned = tables.Select((table, index) => (Table: table, Index: index, Name: Name(table.Name))).Where(t => t.Name is "mcp_servers.harness" || t.Name.StartsWith("mcp_servers.harness.", StringComparison.Ordinal)).ToArray();
        var main = owned.FirstOrDefault(t => t.Name is "mcp_servers.harness").Table;
        string output;
        string action;
        if (spec is null)
        {
            if (owned.Length is 0)
                return entry is null ? new(source, "absent") : throw new InvalidDataException(LayoutError);
            output = source;
            foreach (var item in owned.Reverse())
            {
                var start = LineStart(source, item.Table.OpenBracket!.Span.Offset);
                var end = item.Index + 1 < tables.Length ? LineStart(source, tables[item.Index + 1].OpenBracket!.Span.Offset) : source.Length;
                var previous = start > 0 ? LineStart(source, start - 1) : 0;
                if (source[previous..start].Trim() == Marker)
                    start = previous;
                if (end < source.Length)
                    while (end > item.Table.CloseBracket!.Span.Offset && source[LineStart(source, end - 1)..end].Trim().Length is 0)
                        end = LineStart(source, end - 1);
                output = output[..start] + output[end..];
            }
            if (source.EndsWith('\n'))
                output = string.IsNullOrWhiteSpace(output) ? "" : output.TrimEnd('\n') + "\n";
            action = "remove";
        }
        else if (main is null)
        {
            if (owned.Length > 0 || entry is not null)
                throw new InvalidDataException(LayoutError);
            output = source;
            if (output.Length > 0 && !output.EndsWith('\n'))
                output += "\n";
            if (!string.IsNullOrWhiteSpace(output) && !output.EndsWith("\n\n", StringComparison.Ordinal))
                output += "\n";
            output += Marker + "\n[mcp_servers.harness]\ncommand = " + Quote(spec.Command) + "\nargs = " + Args(spec.Args) + "\n";
            action = "add";
        }
        else
        {
            List<(int Start, int Length, string Text)> edits = [];
            List<string> missing = [];
            foreach (var (key, value) in new[] { ("command", Quote(spec.Command)), ("args", Args(spec.Args)) })
            {
                var item = main.Items.FirstOrDefault(i => Name(i.Key) == key);
                if (item is null)
                    missing.Add(key + " = " + value);
                else
                    edits.Add((item.Value!.Span.Offset, item.Value.Span.Length, value));
            }
            if (missing.Count > 0)
            {
                var end = source.IndexOf('\n', main.CloseBracket!.Span.Offset);
                var newline = MarkedBlock.Newline(source);
                edits.Add(end < 0 ? (source.Length, 0, newline + string.Join(newline, missing) + newline) : (end + 1, 0, string.Join(newline, missing) + newline));
            }
            output = source;
            foreach (var edit in edits.OrderByDescending(e => e.Start))
                output = output[..edit.Start] + edit.Text + output[(edit.Start + edit.Length)..];
            action = "update";
        }
        Verify(before, Json(Parse(output)), spec);
        return new(output, action);
    }
    private static int LineStart(string source, int offset) => offset <= 0 ? 0 : source.LastIndexOf('\n', offset - 1) + 1;
    private static string Args(IReadOnlyList<string> args) => "[" + string.Join(", ", args.Select(Quote)) + "]";
    private static string Quote(string text)
    {
        if (text.Any(c => c < 0x20 || c is '\x7f'))
            throw new InvalidDataException("command or argument contains control characters");
        return "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
    private static void Verify(JsonObject before, JsonObject after, ServerSpec? spec)
    {
        var previous = before["mcp_servers"]?["harness"] as JsonObject;
        var current = after["mcp_servers"]?["harness"] as JsonObject;
        if (spec is null ? current is not null : current?["command"]?.GetValue<string>() != spec.Command || !JsonNode.DeepEquals(current?["args"], JsonSerializer.SerializeToNode(spec.Args)))
            throw new InvalidDataException("harness entry not as intended after edit; nothing written");
        if (spec is not null && previous is not null)
            if (previous.Where(p => p.Key is not ("command" or "args")).Any(pair => !JsonNode.DeepEquals(pair.Value, current?[pair.Key])))
                throw new InvalidDataException("edit would change another harness key; nothing written");
        ClientConfiguration.Pop(before, ["mcp_servers"]);
        ClientConfiguration.Pop(after, ["mcp_servers"]);
        if (!JsonNode.DeepEquals(before, after))
            throw new InvalidDataException("edit would change unrelated configuration; nothing written");
    }
}
