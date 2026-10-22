namespace Harness.Core.Instructions;

public sealed record TextEdit(string Text, string Action);

/// <summary>Edits only one balanced, uniquely marked block.</summary>
public sealed record MarkedBlock(string Begin, string End, string What)
{
    public (int Start, int End) Find(string source)
    {
        var start = source.IndexOf(Begin, StringComparison.Ordinal);
        var end = source.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 && end < 0)
        {
            return (-1, -1);
        }

        if (
            start < 0
            || end < start
            || source.IndexOf(Begin, start + Begin.Length, StringComparison.Ordinal) >= 0
            || source.IndexOf(End, end + End.Length, StringComparison.Ordinal) >= 0
        )
        {
            throw new InvalidDataException(What + " markers are unbalanced or duplicated; fix the file by hand");
        }

        return (source.LastIndexOf('\n', start) + 1, end + End.Length);
    }

    public TextEdit Put(string source, string block)
    {
        var (start, end) = Find(source);
        var newline = Newline(source);
        block = block.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
        if (start < 0)
        {
            return new(Separated(source, newline) + block + newline, "add");
        }

        return source[start..end] == block
            ? new(source, "unchanged")
            : new(source[..start] + block + source[end..], "update");
    }

    public TextEdit Cut(string source)
    {
        var (start, end) = Find(source);
        if (start < 0)
        {
            return new(source, "absent");
        }

        var newline = Newline(source);
        var before = source[..start];
        var after = source[end..];
        if (after.StartsWith(newline, StringComparison.Ordinal))
        {
            after = after[newline.Length..];
        }

        if (after.Length is 0)
        {
            before = before.TrimEnd('\r', '\n');
            if (before.Length > 0)
            {
                before += newline;
            }
        }
        return new(before + after, "remove");
    }

    public static string Newline(string source) => source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    public static string Separated(string source, string newline)
    {
        if (source.Length is 0)
        {
            return source;
        }

        if (!source.EndsWith(newline, StringComparison.Ordinal))
        {
            source += newline;
        }

        return source.EndsWith(newline + newline, StringComparison.Ordinal) ? source : source + newline;
    }
}
