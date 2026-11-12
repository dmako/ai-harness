using System.Text;

namespace Harness.Infrastructure.Scm;

/// <summary>One variable of a git config file, in file order.</summary>
/// <param name="Section">The section, upper case (REMOTE, URL).</param>
/// <param name="Subsection">The quoted subsection as written (a remote's name, a URL base), or empty.</param>
/// <param name="Key">The variable name, upper case (URL, PUSHURL, INSTEADOF).</param>
public sealed record GitConfigEntry(string Section, string Subsection, string Key, string Value);

/// <summary>
/// Reads git config files directly (no git process): sections with quoted subsections, the deprecated
/// <c>[section.sub]</c> form, comments, quoted values with escapes and line continuations. Includes are not followed.
/// </summary>
public static class GitConfigFile
{
    public static IReadOnlyList<GitConfigEntry> Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<GitConfigEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<GitConfigEntry> entries = [];
        string section = "",
            subsection = "";
        foreach (var logical in LogicalLines(text))
        {
            var line = logical.Trim();
            if (line.Length is 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] is '[')
            {
                (section, subsection) = Header(line);
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            var key = (equals < 0 ? line : line[..equals]).Trim();
            if (
                section.Length is 0
                || key.Length is 0
                || !key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-')
            )
            {
                continue;
            }

            entries.Add(
                new GitConfigEntry(
                    section,
                    subsection,
                    key.ToUpperInvariant(),
                    equals < 0 ? "true" : Value(line[(equals + 1)..])
                )
            );
        }

        return entries;
    }

    /// <summary>The file's lines with continuations (a line ending in an odd number of backslashes) joined.</summary>
    private static List<string> LogicalLines(string text)
    {
        List<string> lines = [];
        var builder = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var slashes = raw.Length - raw.TrimEnd('\\').Length;
            if (slashes % 2 is 1)
            {
                builder.Append(raw.AsSpan(0, raw.Length - 1));
                continue;
            }

            lines.Add(builder.Append(raw).ToString());
            builder.Clear();
        }

        if (builder.Length > 0)
        {
            lines.Add(builder.ToString());
        }

        return lines;
    }

    private static (string Section, string Subsection) Header(string line)
    {
        var end = line.LastIndexOf("]", StringComparison.Ordinal);
        var inner = end > 0 ? line[1..end].Trim() : line[1..].Trim();
        var quote = inner.IndexOf('"', StringComparison.Ordinal);
        if (quote > 0)
        {
            return (inner[..quote].Trim().ToUpperInvariant(), Value(inner[quote..]));
        }

        var dot = inner.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? (inner[..dot].ToUpperInvariant(), inner[(dot + 1)..]) : (inner.ToUpperInvariant(), "");
    }

    /// <summary>A value with quotes removed, escapes resolved and an unquoted comment cut off.</summary>
    private static string Value(string raw)
    {
        var builder = new StringBuilder();
        var quoted = false;
        var escaped = false;
        foreach (var character in raw)
        {
            if (escaped)
            {
                builder.Append(
                    character switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'b' => '\b',
                        _ => character,
                    }
                );
                escaped = false;
                continue;
            }

            switch (character)
            {
                case '\\':
                    escaped = true;
                    break;
                case '"':
                    quoted = !quoted;
                    break;
                case '#' or ';' when !quoted:
                    return builder.ToString().Trim();
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString().Trim();
    }
}
