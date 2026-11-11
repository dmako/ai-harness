using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Scm;

/// <summary>A line's numbers on both sides of a diff: an added line has only a new number, a removed one only an old.</summary>
public sealed record DiffLine(int? OldLine, int? NewLine);

/// <summary>Reads unified diff hunks: where a line sits, and splitting a whole-request diff into files.</summary>
public static partial class UnifiedDiff
{
    /// <summary>
    /// The line <paramref name="line" /> of the side <paramref name="type" /> names inside the hunks of one file's diff,
    /// with its number on the other side when it is an unchanged context line; null when no hunk shows it.
    /// </summary>
    public static DiffLine? Locate(string diff, int line, DiffLineType type)
    {
        ArgumentNullException.ThrowIfNull(diff);
        int oldLine = 0,
            newLine = 0;
        var inHunk = false;
        foreach (var raw in diff.Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            var header = HunkHeader.Match(text);
            if (header.Success)
            {
                oldLine = int.Parse(header.Groups["old"].Value, System.Globalization.CultureInfo.InvariantCulture);
                newLine = int.Parse(header.Groups["new"].Value, System.Globalization.CultureInfo.InvariantCulture);
                inHunk = true;
                continue;
            }

            if (!inHunk || text.StartsWith("\\", StringComparison.Ordinal))
            {
                continue;
            }

            var marker = text.Length > 0 ? text[0] : ' ';
            var side = type is DiffLineType.New ? newLine : oldLine;
            var found = marker switch
            {
                '+' when type is DiffLineType.New && newLine == line => new DiffLine(null, newLine),
                '-' when type is DiffLineType.Old && oldLine == line => new DiffLine(oldLine, null),
                '+' or '-' => null,
                _ when side == line => new DiffLine(oldLine, newLine),
                _ => null,
            };
            if (found is not null)
            {
                return found;
            }

            (oldLine, newLine) = marker switch
            {
                '+' => (oldLine, newLine + 1),
                '-' => (oldLine + 1, newLine),
                _ => (oldLine + 1, newLine + 1),
            };
        }

        return null;
    }

    /// <summary>Splits a whole <c>git diff</c> into its files (paths from the <c>diff --git</c>, ---/+++ and rename lines).</summary>
    public static List<ScmFileDiff> SplitFiles(string patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        List<ScmFileDiff> files = [];
        string? oldPath = null,
            newPath = null;
        bool added = false,
            deleted = false,
            renamed = false,
            binary = false;
        var body = new StringBuilder();
        var inBody = false;

        void Flush()
        {
            if (newPath is not null || oldPath is not null)
            {
                files.Add(
                    new ScmFileDiff(
                        deleted || renamed ? oldPath : oldPath ?? newPath,
                        newPath ?? oldPath!,
                        added,
                        deleted,
                        renamed,
                        binary,
                        body.ToString(),
                        false
                    )
                );
            }

            (oldPath, newPath, added, deleted, renamed, binary, inBody) = (
                null,
                null,
                false,
                false,
                false,
                false,
                false
            );
            body.Clear();
        }

        foreach (var raw in patch.Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            var start = GitHeader.Match(text);
            if (start.Success)
            {
                Flush();
                (oldPath, newPath) = (start.Groups["old"].Value, start.Groups["new"].Value);
                continue;
            }

            if (inBody)
            {
                body.Append(text).Append('\n');
                continue;
            }

            switch (text)
            {
                case var _ when text.StartsWith("@@", StringComparison.Ordinal):
                    inBody = true;
                    body.Append(text).Append('\n');
                    break;
                case var _ when text.StartsWith("new file mode", StringComparison.Ordinal):
                    added = true;
                    break;
                case var _ when text.StartsWith("deleted file mode", StringComparison.Ordinal):
                    deleted = true;
                    break;
                case var _ when text.StartsWith("rename from ", StringComparison.Ordinal):
                    (renamed, oldPath) = (true, text["rename from ".Length..]);
                    break;
                case var _ when text.StartsWith("rename to ", StringComparison.Ordinal):
                    (renamed, newPath) = (true, text["rename to ".Length..]);
                    break;
                case var _ when text.StartsWith("Binary files", StringComparison.Ordinal):
                    binary = true;
                    break;
                default:
                    break;
            }
        }

        Flush();
        return files;
    }

    [GeneratedRegex(@"^@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader { get; }

    [GeneratedRegex(@"^diff --git a/(?<old>.+?) b/(?<new>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHeader { get; }
}

/// <summary>Keeps a merge request's diff within a byte budget without dropping files silently.</summary>
public static class ScmDiffBudget
{
    /// <summary>
    /// Every file stays listed; a file whose hunks do not fit keeps what fits (cut at a line end) and is marked truncated,
    /// and once the budget is spent the remaining files keep only their paths, marked truncated and counted as omitted.
    /// </summary>
    public static ScmMergeRequestDiff Apply(IEnumerable<ScmFileDiff> files, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(files);
        var remaining = Math.Max(0, maxBytes);
        var omitted = 0;
        var truncated = false;
        List<ScmFileDiff> kept = [];
        foreach (var file in files)
        {
            var size = Encoding.UTF8.GetByteCount(file.Diff);
            if (size <= remaining)
            {
                remaining -= size;
                kept.Add(file);
                truncated |= file.Truncated;
                continue;
            }

            truncated = true;
            if (remaining <= 0 || file.Diff.Length is 0)
            {
                omitted++;
                kept.Add(file with { Diff = "", Truncated = true });
                continue;
            }

            var cut = Cut(file.Diff, remaining);
            remaining = 0;
            if (cut.Length is 0)
            {
                omitted++;
            }

            kept.Add(file with { Diff = cut, Truncated = true });
        }

        return new ScmMergeRequestDiff(kept, truncated, omitted);
    }

    private static string Cut(string text, int maxBytes)
    {
        var builder = new StringBuilder();
        var used = 0;
        foreach (var line in text.Split('\n'))
        {
            var size = Encoding.UTF8.GetByteCount(line) + 1;
            if (used + size > maxBytes)
            {
                break;
            }

            builder.Append(line).Append('\n');
            used += size;
        }

        return builder.ToString();
    }
}
