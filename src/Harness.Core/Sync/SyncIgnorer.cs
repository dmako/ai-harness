using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Core.Sync;

/// <summary>Harness path.Match-style ignores, in addition to Git's own ignore rules.</summary>
public sealed class SyncIgnorer
{
    private readonly string[] _patterns;
    private readonly Dictionary<string, Regex?> _matchers;

    public SyncIgnorer(IEnumerable<string>? configured = null)
    {
        _patterns = new[] { ".harness/", ".git/" }
            .Concat(
                (configured ?? []).Select(pattern => pattern.Trim().TrimStart('/')).Where(pattern => pattern.Length > 0)
            )
            .ToArray();
        _matchers = _patterns
            .Select(pattern => pattern.EndsWith('/') ? pattern[..^1] : pattern)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(pattern => pattern, Compile, StringComparer.Ordinal);
    }

    public bool Ignored(string path)
    {
        List<string> segments = [];
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment is "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else
            {
                segments.Add(segment);
            }
        }
        path = string.Join('/', segments);
        foreach (var pattern in _patterns)
        {
            if (pattern.EndsWith('/'))
            {
                var directory = pattern[..^1];
                if (path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal))
                {
                    return true;
                }

                if (
                    !directory.Contains('/', StringComparison.Ordinal)
                    && segments.Take(Math.Max(0, segments.Count - 1)).Any(segment => Matches(directory, segment))
                )
                {
                    return true;
                }
            }
            else if (
                Matches(
                    pattern,
                    pattern.Contains('/', StringComparison.Ordinal) ? path : segments.LastOrDefault() ?? ""
                )
            )
            {
                return true;
            }
        }
        return false;
    }

    public IReadOnlyList<string> FilterPaths(IEnumerable<string> paths) =>
        paths.Where(path => !Ignored(path)).ToArray();

    public IReadOnlyList<GitChange> Filter(IEnumerable<GitChange> changes) =>
        changes
            .Select(change =>
                change switch
                {
                    { Kind: "renamed" } when Ignored(change.Path) && !Ignored(change.OldPath) => new(
                        "deleted",
                        change.OldPath
                    ),
                    { Kind: "renamed" } when !Ignored(change.Path) && Ignored(change.OldPath) => new(
                        "added",
                        change.Path
                    ),
                    _ when Ignored(change.Path) => null,
                    _ => change,
                }
            )
            .OfType<GitChange>()
            .ToArray();

    private bool Matches(string pattern, string value) => _matchers[pattern]?.IsMatch(value) is true;

    private static Regex? Compile(string pattern)
    {
        var regex = new StringBuilder("\\A");
        var i = 0;
        while (i < pattern.Length)
        {
            switch (pattern[i++])
            {
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '\\':
                    if (i == pattern.Length)
                    {
                        return null;
                    }

                    regex.Append(Regex.Escape(pattern[i++].ToString()));
                    break;
                case '[':
                    var end = pattern.IndexOf(']', i);
                    if (end < 0)
                    {
                        return null;
                    }

                    regex.Append(pattern.AsSpan(i - 1, end - i + 2));
                    i = end + 1;
                    break;
                default:
                    regex.Append(Regex.Escape(pattern[i - 1].ToString()));
                    break;
            }
        }
        try
        {
            return new(
                regex.Append("\\z").ToString(),
                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)
            );
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
