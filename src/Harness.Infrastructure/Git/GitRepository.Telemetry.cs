using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Infrastructure.Git;

public sealed partial record GitRepository
{
    [GeneratedRegex(@"^@@ [^@]* @@ ?(.*)$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Hunk { get; }

    [GeneratedRegex(@"([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^()<>]*>)?\s*\(", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Callable { get; }

    private static readonly HashSet<string> NotNames = new(
        "func function def if for foreach while switch catch using return new lock fn".Split(' '),
        StringComparer.Ordinal
    );

    public async Task<IReadOnlyList<string>> ChangedBetweenAsync(string from, string to, CancellationToken token)
    {
        ValidateRevision(from);
        ValidateRevision(to);
        return SplitNul(
            await ReadGitAsync(["diff", "--name-only", "-z", "--no-renames", "--no-ext-diff", from, to, "--"], token)
                .ConfigureAwait(false)
        );
    }

    public Task<string> FileAtAsync(string commit, string path, CancellationToken token)
    {
        ValidateRevision(commit);
        return ReadGitAsync(["show", commit + ":" + path], token);
    }

    /// <summary>One <c>git cat-file --batch</c> for many <see cref="FileAtAsync"/>-like reads (TASK-0100).</summary>
    public GitBlobReader OpenBlobReader() => new(Root);

    /// <summary>
    /// The functions changed between two commits in each of <paramref name="paths"/>, from one
    /// <c>git diff -U0</c> over all of them (TASK-0100; more only when the paths exceed one command line): the names
    /// in its hunk headers, by harness's diff attributes. Renames are off, so each file's hunks are the ones a diff
    /// of that file alone gives. A path without hunks maps to no names.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ChangedFunctionsAsync(
        string from,
        string to,
        IReadOnlyCollection<string> paths,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(paths);
        ValidateRevision(from);
        ValidateRevision(to);
        var prefix = await DiffAttributesAsync(token).ConfigureAwait(false);
        Dictionary<string, IReadOnlyList<string>> result = new(StringComparer.Ordinal);
        foreach (var chunk in CommandLineChunks(paths))
        {
            var diff = await ReadGitAsync(
                    [
                        .. prefix,
                        "-c",
                        "core.quotePath=false",
                        "diff",
                        "-U0",
                        "--no-color",
                        "--no-ext-diff",
                        "--no-renames",
                        "--src-prefix=a/",
                        "--dst-prefix=b/",
                        from,
                        to,
                        "--",
                        .. chunk,
                    ],
                    token
                )
                .ConfigureAwait(false);
            foreach (var (path, hunks) in HunksByFile(diff))
            {
                result[path] = Names(hunks);
            }
        }

        foreach (var path in paths)
        {
            result.TryAdd(path, []);
        }

        return result;
    }

    /// <summary>Paths split so that each git command line stays well under Windows' 32,767-character limit.</summary>
    private static IEnumerable<List<string>> CommandLineChunks(IEnumerable<string> paths)
    {
        const int Limit = 24_000;
        List<string> chunk = [];
        var length = 0;
        foreach (var path in paths)
        {
            if (chunk.Count > 0 && length + path.Length + 3 > Limit)
            {
                yield return chunk;
                chunk = [];
                length = 0;
            }

            chunk.Add(path);
            length += path.Length + 3;
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// The hunk header lines of each file in a unified diff, by the file's path (its new path, or its old one when
    /// deleted). Lines are only taken as headers before a file's first hunk, so a removed line starting "-- " or an
    /// added one starting "++ " is never mistaken for one.
    /// </summary>
    internal static Dictionary<string, List<string>> HunksByFile(string diff)
    {
        Dictionary<string, List<string>> files = new(StringComparer.Ordinal);
        string? old = null;
        List<string>? hunks = null;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                old = null;
                hunks = null;
            }
            else if (hunks is null && line.StartsWith("--- ", StringComparison.Ordinal))
            {
                old = HeaderPath(line[4..], "a/");
            }
            else if (hunks is null && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if ((HeaderPath(line[4..], "b/") ?? old) is { } path)
                {
                    if (!files.TryGetValue(path, out hunks))
                    {
                        hunks = [];
                        files[path] = hunks;
                    }
                }
            }
            else if (hunks is not null && line.StartsWith("@@", StringComparison.Ordinal))
            {
                hunks.Add(line);
            }
        }

        return files;
    }

    /// <summary>A path of a ---/+++ line: unquoted, without its a/ or b/ prefix; null for /dev/null.</summary>
    private static string? HeaderPath(string text, string prefix)
    {
        // git ends a name holding whitespace with a tab, so that the line can be split
        var name = text.TrimEnd('\t', '\r');
        if (name is "/dev/null")
        {
            return null;
        }

        if (name.StartsWith('"'))
        {
            name = Unquote(name);
        }

        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    /// <summary>A name quoted as git quotes paths: C escapes and octal bytes of UTF-8.</summary>
    private static string Unquote(string quoted)
    {
        var bytes = new List<byte>(quoted.Length);
        var end = quoted.EndsWith('"') && quoted.Length > 1 ? quoted.Length - 1 : quoted.Length;
        var i = 1;
        while (i < end)
        {
            var c = quoted[i];
            if (c is not '\\' || i + 1 >= end)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                i++;
                continue;
            }

            var next = quoted[i + 1];
            if (next is >= '0' and <= '7' && i + 3 < end)
            {
                bytes.Add(Convert.ToByte(quoted.Substring(i + 1, 3), 8));
                i += 4;
                continue;
            }

            i += 2;
            bytes.Add(
                next switch
                {
                    'a' => 7,
                    'b' => 8,
                    't' => 9,
                    'n' => 10,
                    'v' => 11,
                    'f' => 12,
                    'r' => 13,
                    _ => (byte)next,
                }
            );
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>The callable names in hunk headers, once each, in order.</summary>
    private static string[] Names(IEnumerable<string> lines) =>
        lines
            .Select(line => Hunk.Match(line))
            .Where(m => m.Success)
            .Select(m =>
                Callable
                    .Matches(m.Groups[1].Value)
                    .FirstOrDefault(c => !NotNames.Contains(c.Groups[1].Value))
                    ?.Groups[1]
                    .Value
            )
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// <c>-c core.attributesFile=…</c> pointing at harness's diff attributes, which give git's hunk headers the
    /// function names of common languages; nothing when the file cannot be written.
    /// </summary>
    private async Task<string[]> DiffAttributesAsync(CancellationToken token)
    {
        var attributes = Path.Combine(GitDir, "harness-diff-attributes");
        string[] prefix = [];
        try
        {
            if (!File.Exists(attributes))
            {
                await File.WriteAllTextAsync(
                        attributes,
                        "*.cs diff=csharp\n*.go diff=golang\n*.java diff=java\n*.kt diff=kotlin\n*.py diff=python\n*.rs diff=rust\n*.php diff=php\n*.rb diff=ruby\n*.c diff=cpp\n*.h diff=cpp\n*.cpp diff=cpp\n*.hpp diff=cpp\n*.m diff=objc\n",
                        new UTF8Encoding(false),
                        token
                    )
                    .ConfigureAwait(false);
            }

            prefix = ["-c", "core.attributesFile=" + attributes];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* Git's repository/default hunk patterns remain available. */
        }

        return prefix;
    }
}
