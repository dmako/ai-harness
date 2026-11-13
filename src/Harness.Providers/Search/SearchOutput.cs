using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Providers.Mcp;

namespace Harness.Providers.Search;

/// <summary>One line of text search: the file as rg printed it, its 1-based line number and the line.</summary>
internal readonly record struct TextMatch(string Path, int Line, string Text);

/// <summary>The command lines of the built-in search and the parsing of what rg and fd print.</summary>
internal static class SearchOutput
{
    /// <summary>A snippet longer than this many UTF-8 bytes is cut (minified files have very long lines).</summary>
    internal const int SnippetBytes = 400;

    /// <summary>
    /// rg's JSON lines for a query, sorted by path so the first matches are always the same ones: the pattern is
    /// {query_regex} (the plain-text query with its metacharacters escaped); .gitignore and .ignore apply as rg reads
    /// them, and the user's rg configuration does not. Without a scope the search runs over '.', so rg never reads
    /// its standard input.
    /// </summary>
    internal static string[] TextArguments(string query, string scope) =>
        [
            "--json",
            "--no-config",
            "--sort",
            "path",
            "--path-separator",
            "/",
            "-e",
            McpMapping.QuoteMeta(query),
            "--",
            scope.Length > 0 ? scope : ".",
        ];

    /// <summary>fd's files whose name matches {query_regex} (smart case), within the scope when one is given.</summary>
    internal static string[] FdArguments(string query, string scope) =>
        [
            "--type",
            "f",
            "--color",
            "never",
            "--path-separator",
            "/",
            "--",
            McpMapping.QuoteMeta(query),
            .. scope.Length > 0 ? new[] { scope } : [],
        ];

    /// <summary>Without fd: every file rg would search, sorted, filtered by name here (<see cref="FileFilter"/>).</summary>
    internal static string[] RgFilesArguments(string scope) =>
        ["--files", "--no-config", "--sort", "path", "--path-separator", "/", "--", scope.Length > 0 ? scope : "."];

    /// <summary>
    /// The 'rg --files' filter that stands in for fd: {query_regex} over the file name, ignoring case unless the
    /// query has an upper-case letter (fd's smart case).
    /// </summary>
    internal static Func<string, bool> FileFilter(string query)
    {
        var options =
            RegexOptions.CultureInvariant
            | RegexOptions.NonBacktracking
            | (query.Any(char.IsUpper) ? RegexOptions.None : RegexOptions.IgnoreCase);
        Regex pattern;
        try
        {
            pattern = new(McpMapping.QuoteMeta(query), options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            var comparison = query.Any(char.IsUpper) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return path => FileName(path).Contains(query, comparison);
        }

        return path => pattern.IsMatch(FileName(path));
    }

    /// <summary>A match line of rg --json, or null for its begin, end, context and summary lines.</summary>
    internal static TextMatch? ParseTextLine(string line)
    {
        if (line.Length is 0 || !line.Contains("\"match\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (
                root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind is not JsonValueKind.String
                || type.GetString() is not "match"
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind is not JsonValueKind.Object
            )
            {
                return null;
            }

            var path = Data(data, "path");
            var number =
                data.TryGetProperty("line_number", out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
            return path.Length is 0 ? null : new TextMatch(path, number, Snippet(Data(data, "lines")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A line of fd or rg --files: a file path, or null for an empty line.</summary>
    internal static string? ParseFileLine(string line)
    {
        var path = line.TrimEnd('\r', '\n');
        return path.Length is 0 ? null : path;
    }

    /// <summary>The line without its line break and surrounding blanks, cut at <see cref="SnippetBytes"/>.</summary>
    internal static string Snippet(string text)
    {
        var trimmed = text.Trim();
        return Encoding.UTF8.GetByteCount(trimmed) > SnippetBytes
            ? McpMapping.Utf8Prefix(trimmed, SnippetBytes, "…")
            : trimmed;
    }

    /// <summary>rg's {"text": ...} or, for bytes that are not UTF-8, {"bytes": base64}.</summary>
    private static string Data(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.Object)
        {
            return "";
        }

        if (value.TryGetProperty("text", out var text) && text.ValueKind is JsonValueKind.String)
        {
            return text.GetString()!;
        }

        if (value.TryGetProperty("bytes", out var bytes) && bytes.ValueKind is JsonValueKind.String)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(bytes.GetString()!));
            }
            catch (FormatException)
            {
                return "";
            }
        }

        return "";
    }

    private static string FileName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}
