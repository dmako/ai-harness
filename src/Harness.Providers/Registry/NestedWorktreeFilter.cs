namespace Harness.Providers.Registry;

/// <summary>Short-lived directory cache; checks ancestors only, never scans the working tree.</summary>
public sealed class NestedWorktreeFilter(string root, TimeProvider? timeProvider = null)
{
    private readonly string _inputRoot = Path.GetFullPath(root);
    private readonly string _root = ResolveRoot(root);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (bool Nested, DateTimeOffset At)> _cache = new(StringComparer.Ordinal);

    public bool Skip(string path)
    {
        var relative = path;
        if (Path.IsPathFullyQualified(path))
        {
            relative = Path.GetRelativePath(_inputRoot, path);
            if (
                Path.IsPathRooted(relative)
                || relative.Split(Path.DirectorySeparatorChar).Contains("..", StringComparer.Ordinal)
            )
            {
                relative = Path.GetRelativePath(_root, path);
            }
        }
        relative = relative.Replace(Path.DirectorySeparatorChar, '/');
        // Do not inspect ancestors outside this project, including provider-supplied traversal.
        if (Path.IsPathRooted(relative) || relative.Split('/').Contains("..", StringComparer.Ordinal))
        {
            return false;
        }
        var parts = relative.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            if (IsWorktree(string.Join('/', parts, 0, i)))
            {
                return true;
            }
        }
        return false;
    }

    private bool IsWorktree(string directory)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_cache.TryGetValue(directory, out var entry) && now - entry.At < TimeSpan.FromSeconds(30))
            {
                return entry.Nested;
            }
            var nested = false;
            try
            {
                var line = File.ReadAllText(Path.Combine(_root, directory, ".git")).Trim();
                nested =
                    line.StartsWith("gitdir:", StringComparison.Ordinal)
                    && line.Replace(Path.DirectorySeparatorChar, '/').Contains("/worktrees/", StringComparison.Ordinal);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Missing files and .git directories are ordinary, non-worktree ancestors.
            }
            _cache[directory] = (nested, now);
            return nested;
        }
    }

    private static string ResolveRoot(string root, int remainingLinks = 40)
    {
        var full = Path.GetFullPath(root);
        if (remainingLinks is 0)
        {
            return full;
        }
        var current = Path.GetPathRoot(full)!;
        try
        {
            foreach (
                var component in full[current.Length..]
                    .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            )
            {
                current = Path.Combine(current, component);
                var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (target is not null)
                {
                    current = ResolveRoot(target, remainingLinks - 1);
                }
            }
            return current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }
}
