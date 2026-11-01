using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Harness.Core.Memory;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Harness.Infrastructure.Memory;

/// <summary>
/// The project's shared knowledge, .harness/knowledge.toml: tracked by Git, one [[memory]] table per memory the team
/// shares. Written deterministically (entries sorted by id, fixed key order, LF) so concurrent changes merge cleanly;
/// read leniently, a bad entry reported with its line and skipped while the others load.
/// </summary>
public static partial class KnowledgeFile
{
    public const string FileName = "knowledge.toml";
    public const int Version = 1;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] Keys = ["id", "type", "scope", "content", "source_ref", "shared_by", "shared_at"];

    [GeneratedRegex(@"\Amem_[A-Za-z0-9_-]+\z")]
    private static partial Regex IdPattern { get; }

    /// <summary>The file in a project's state directory (.harness).</summary>
    public static string In(string stateDirectory) => Path.Combine(stateDirectory, FileName);

    /// <summary>The file as read; no file is empty shared knowledge with an empty hash.</summary>
    public static SharedKnowledge Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return SharedKnowledge.Empty;
        }

        return Parse(Utf8.GetString(bytes)) with
        {
            Hash = Hash(bytes),
        };
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Writes the memories, replacing the file at once.</summary>
    public static void Write(string path, IEnumerable<SharedMemory> memories)
    {
        var text = Format(memories);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, Utf8);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>The file's text: the same memories always give the same bytes.</summary>
    public static string Format(IEnumerable<SharedMemory> memories)
    {
        ArgumentNullException.ThrowIfNull(memories);
        var sorted = memories.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        if (sorted.GroupBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new ArgumentException($"memory {twice.Key} is shared twice");
        }

        var text = new StringBuilder()
            .Append(
                "# Project knowledge shared through Git. harness reads it after every Git change and merges it with\n"
            )
            .Append("# each teammate's own knowledge; share or withdraw a memory with harness ui or 'harness memory\n")
            .Append("# share/withdraw'. One [[memory]] per shared memory, sorted by id.\n")
            .Append(CultureInfo.InvariantCulture, $"version = {Version}\n");
        foreach (var memory in sorted)
        {
            text.Append("\n[[memory]]\n");
            Line(text, "id", memory.Id);
            Line(text, "type", memory.Type);
            Line(text, "scope", memory.Scope.ToString());
            Line(text, "content", memory.Content);
            Line(text, "source_ref", memory.SourceRef);
            Line(text, "shared_by", memory.SharedBy);
            Line(text, "shared_at", memory.SharedAt);
        }

        return text.ToString();
    }

    private static void Line(StringBuilder text, string key, string value)
    {
        if (value.Length is not 0)
        {
            text.Append(key).Append(" = ").Append(Quote(value)).Append('\n');
        }
    }

    /// <summary>A TOML basic string.</summary>
    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in value)
        {
            quoted.Append(
                c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\b' => "\\b",
                    '\t' => "\\t",
                    '\n' => "\\n",
                    '\f' => "\\f",
                    '\r' => "\\r",
                    < ' ' or '\u007f' => "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture),
                    _ => c.ToString(),
                }
            );
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>The memories in a file's text, and what was wrong with the rest (the hash is left empty).</summary>
    public static SharedKnowledge Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<SharedKnowledgeProblem> problems = [];
        var document = SyntaxParser.Parse(text);
        int LineOf(SyntaxNode node) => text.AsSpan(0, Math.Clamp(node.Span.Offset, 0, text.Length)).Count('\n') + 1;
        if (document.HasErrors)
        {
            return new([], [new(0, "not valid TOML: " + document.Diagnostics.ToString().Trim())], "");
        }

        foreach (var item in document.KeyValues)
        {
            var key = KeyName(item.Key);
            if (key is "version")
            {
                if (item.Value is not IntegerValueSyntax { Value: var version } || version < 1)
                {
                    problems.Add(new(LineOf(item), "version must be a positive whole number"));
                }
                else if (version > Version)
                {
                    return new(
                        [],
                        [
                            new(
                                LineOf(item),
                                $"written by a newer harness (version {version}; this one reads {Version})"
                            ),
                        ],
                        ""
                    );
                }
            }
            else
            {
                problems.Add(new(LineOf(item), $"unknown key {key}"));
            }
        }

        List<SharedMemory> memories = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var table in document.Tables)
        {
            var line = LineOf(table);
            if (table is not TableArraySyntax || KeyName(table.Name) is not "memory")
            {
                problems.Add(new(line, $"unknown table {KeyName(table.Name)}: only [[memory]] entries belong here"));
                continue;
            }

            if (Entry(table, line, problems) is not { } memory)
            {
                continue;
            }

            if (!ids.Add(memory.Id))
            {
                problems.Add(new(line, $"memory {memory.Id} is listed twice; this entry is skipped"));
                continue;
            }

            memories.Add(memory);
        }

        return new(memories, problems, "");
    }

    /// <summary>One [[memory]] entry, or null (with its problem) when it cannot be used.</summary>
    private static SharedMemory? Entry(TableSyntaxBase table, int line, List<SharedKnowledgeProblem> problems)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (var item in table.Items)
        {
            var key = KeyName(item.Key);
            if (!Keys.Contains(key, StringComparer.Ordinal))
            {
                problems.Add(new(line, $"unknown key {key} in a [[memory]] entry"));
                continue;
            }

            if (item.Value is not StringValueSyntax { Value: { } value })
            {
                problems.Add(new(line, $"{key} must be a string; the entry is skipped"));
                return null;
            }

            values[key] = value;
        }

        string Value(string key) => values.GetValueOrDefault(key, "").Trim();
        var id = Value("id");
        void Skip(string why)
        {
            problems.Add(new(line, $"{(id.Length is 0 ? "an entry" : "memory " + id)} {why}; it is skipped"));
        }

        if (!IdPattern.IsMatch(id))
        {
            Skip(id.Length is 0 ? "has no id" : "has an id that is not a memory id (mem_…)");
            return null;
        }

        string type;
        try
        {
            type = MemoryText.ParseType(Value("type"));
        }
        catch (ArgumentException)
        {
            Skip($"has an unknown type \"{Value("type")}\"");
            return null;
        }

        var content = Value("content");
        if (content.Length is 0 || Encoding.UTF8.GetByteCount(content) > MemoryText.MaxContentBytes)
        {
            Skip(content.Length is 0 ? "has no content" : $"has content over {MemoryText.MaxContentBytes} bytes");
            return null;
        }

        MemoryScope scope;
        try
        {
            scope = MemoryScope.Parse(Value("scope").Length is 0 ? "project" : Value("scope"));
        }
        catch (ArgumentException)
        {
            Skip($"has an invalid scope \"{Value("scope")}\"");
            return null;
        }

        if (scope.Wider)
        {
            Skip($"has scope {scope}: only project, path, subsystem and branch knowledge is shared here");
            return null;
        }

        return new(id, type, content, scope, Value("source_ref"), Value("shared_by"), Value("shared_at"));
    }

    private static string KeyName(KeySyntax? key)
    {
        static string Part(SyntaxNode? value) =>
            value switch
            {
                BareKeySyntax bare => bare.Key?.Text ?? "",
                StringValueSyntax quoted => quoted.Value ?? "",
                _ => "",
            };
        return key is null
            ? ""
            : string.Join('.', new[] { Part(key.Key) }.Concat(key.DotKeys.Select(part => Part(part.Key))));
    }
}
