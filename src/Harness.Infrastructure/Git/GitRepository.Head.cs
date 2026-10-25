using System.Text;

namespace Harness.Infrastructure.Git;

public sealed partial record GitRepository
{
    private GitHead ReadHeadFiles(CancellationToken token)
    {
        var content = ReadRefFile(Path.Combine(GitDir, "HEAD"));
        if (!content.StartsWith("ref:", StringComparison.Ordinal))
        {
            if (!IsObjectId(content))
            {
                throw new InvalidDataException("HEAD is neither a ref nor an object id.");
            }
            return new("", content, true, false);
        }
        var reference = content[4..].Trim();
        var branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference[11..] : reference;
        var commit = ResolveRef(reference, token);
        return new(branch, commit ?? "", false, commit is null);
    }

    private string? ResolveRef(string reference, CancellationToken token)
    {
        for (var depth = 0; depth < 10; depth++)
        {
            token.ThrowIfCancellationRequested();
            ValidateRef(reference);
            var directories =
                Linked
                && !reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                && !reference.StartsWith("refs/tags/", StringComparison.Ordinal)
                    ? new[] { GitDir, CommonDir }
                    : [CommonDir];
            string? content = null;
            foreach (var directory in directories)
            {
                try
                {
                    content = ReadRefFile(Path.Combine(directory, reference.Replace('/', Path.DirectorySeparatorChar)));
                    break;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                { /* Try the shared refs. */
                }
            }
            if (content is null)
            {
                return PackedRef(reference, token);
            }
            if (content.StartsWith("ref:", StringComparison.Ordinal))
            {
                reference = content[4..].Trim();
                continue;
            }
            if (!IsObjectId(content))
            {
                throw new InvalidDataException("Git ref contains an invalid object id.");
            }
            return content;
        }
        throw new InvalidDataException("Too many levels of symbolic Git refs.");
    }

    private string? PackedRef(string reference, CancellationToken token)
    {
        var path = Path.Combine(CommonDir, "packed-refs");
        try
        {
            using var reader = new StreamReader(path, new UTF8Encoding(false, true));
            while (reader.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested();
                if (line.Length > 1 << 20)
                {
                    throw new InvalidDataException("Git packed ref exceeds the line limit.");
                }
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                if (space > 0 && line[(space + 1)..] == reference && IsObjectId(line[..space]))
                {
                    return line[..space];
                }
            }
            return null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string ReadRefFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 4096)
        {
            throw new InvalidDataException("Git ref exceeds the size limit.");
        }
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd().Trim();
    }

    private static bool IsObjectId(string value) =>
        value.Length is 40 or 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateRef(string reference)
    {
        if (
            reference.Length is 0
            || Path.IsPathRooted(reference)
            || reference.Contains('\\', StringComparison.Ordinal)
            || reference.Contains(':', StringComparison.Ordinal)
            || reference.Split('/').Any(p => p is "" or "." or "..")
        )
        {
            throw new InvalidDataException("Invalid Git ref path.");
        }
    }
}
