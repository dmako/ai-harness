using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Infrastructure.Git;

public sealed partial record GitRepository
{
    private static readonly Regex Hunk = new(@"^@@ [^@]* @@ ?(.*)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Callable = new(@"([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^()<>]*>)?\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> NotNames = new("func function def if for foreach while switch catch using return new lock fn".Split(' '), StringComparer.Ordinal);
    public async Task<IReadOnlyList<string>> ChangedBetweenAsync(string from, string to, CancellationToken token)
    {
        ValidateRevision(from);
        ValidateRevision(to);
        return SplitNul(await ReadGitAsync(["diff", "--name-only", "-z", "--no-renames", "--no-ext-diff", from, to, "--"], token).ConfigureAwait(false));
    }
    public Task<string> FileAtAsync(string commit, string path, CancellationToken token)
    {
        ValidateRevision(commit);
        return ReadGitAsync(["show", commit + ":" + path], token);
    }
    public async Task<IReadOnlyList<string>> ChangedFunctionsAsync(string from, string to, string path, CancellationToken token)
    {
        ValidateRevision(from);
        ValidateRevision(to);
        var attributes = Path.Combine(GitDir, "harness-diff-attributes");
        string[] prefix = [];
        try
        {
            if (!File.Exists(attributes))
            {
                await File.WriteAllTextAsync(attributes, "*.cs diff=csharp\n*.go diff=golang\n*.java diff=java\n*.kt diff=kotlin\n*.py diff=python\n*.rs diff=rust\n*.php diff=php\n*.rb diff=ruby\n*.c diff=cpp\n*.h diff=cpp\n*.cpp diff=cpp\n*.hpp diff=cpp\n*.m diff=objc\n", new UTF8Encoding(false), token).ConfigureAwait(false);
            }

            prefix = ["-c", "core.attributesFile=" + attributes];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Git's repository/default hunk patterns remain available. */ }
        var diff = await ReadGitAsync([.. prefix, "diff", "-U0", "--no-color", "--no-ext-diff", from, to, "--", path], token).ConfigureAwait(false);
        return diff.Split('\n').Select(line => Hunk.Match(line)).Where(m => m.Success).Select(m => Callable.Matches(m.Groups[1].Value).FirstOrDefault(c => !NotNames.Contains(c.Groups[1].Value))?.Groups[1].Value).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
    }
}
