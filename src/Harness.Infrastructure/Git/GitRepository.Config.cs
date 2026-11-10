using System.Collections.Concurrent;

namespace Harness.Infrastructure.Git;

public sealed partial record GitRepository
{
    /// <summary>A file's identity for a cached answer: missing, or its size and last write.</summary>
    private readonly record struct FileStamp(string Path, bool Exists, long Length, long Written)
    {
        public static FileStamp Of(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? new(path, true, file.Length, file.LastWriteTimeUtc.Ticks) : new(path, false, 0, 0);
        }

        public bool Unchanged => Of(Path) == this;
    }

    /// <summary>A setting as git read it, and what its answer depends on.</summary>
    private sealed record ConfigAnswer(string Value, FileStamp[] Files, string Environment);

    private static readonly ConcurrentDictionary<string, ConfigAnswer> HooksPaths = new(PathComparer);

    /// <summary>The environment variables that change which configuration git reads.</summary>
    private static readonly string[] ConfigVariables =
    [
        "GIT_CONFIG_GLOBAL",
        "GIT_CONFIG_SYSTEM",
        "GIT_CONFIG_NOSYSTEM",
        "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_COUNT",
        "HOME",
        "XDG_CONFIG_HOME",
        "USERPROFILE",
    ];

    /// <summary>
    /// <c>core.hooksPath</c> as <c>git config --get core.hooksPath</c> gives it, "" when unset or unreadable
    /// (TASK-0101). git answers once; the answer is kept while every configuration file git read (system, global,
    /// local, worktree and included ones), the files it would read if they appeared, HEAD (for onbranch includes)
    /// and the configuration environment stay as they were.
    /// </summary>
    public async Task<string> HooksPathAsync(CancellationToken token = default)
    {
        var environment = ConfigEnvironment();
        if (
            HooksPaths.TryGetValue(GitDir, out var known)
            && known.Environment == environment
            && known.Files.All(file => file.Unchanged)
        )
        {
            return known.Value;
        }

        // stamped before git reads them, so a change made meanwhile is seen on the next call
        var candidates = ConfigCandidates().Select(FileStamp.Of).ToArray();
        (int Code, string Output, string Error) result;
        try
        {
            result = await RunAsync(Root, ["config", "--list", "--show-origin", "-z"], token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return "";
        }

        if (result.Code is not 0)
        {
            return "";
        }

        var (value, origins) = ParseConfigList(result.Output, "core.hookspath");
        var read = origins
            .Select(path => Path.GetFullPath(path, Root))
            .Where(path => !candidates.Any(stamp => PathComparer.Equals(stamp.Path, path)))
            .Distinct(PathComparer)
            .Select(FileStamp.Of);
        HooksPaths[GitDir] = new(value, [.. candidates, .. read], environment);
        return value;
    }

    /// <summary>
    /// The last value of <paramref name="key"/> in <c>git config --list --show-origin -z</c> output (what
    /// <c>--get</c> answers), and the files the entries came from.
    /// </summary>
    internal static (string Value, IReadOnlyList<string> Files) ParseConfigList(string output, string key)
    {
        var value = "";
        List<string> files = [];
        var fields = output.Split('\0');
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            if (fields[i].StartsWith("file:", StringComparison.Ordinal))
            {
                files.Add(fields[i]["file:".Length..]);
            }

            var entry = fields[i + 1];
            var feed = entry.IndexOf('\n', StringComparison.Ordinal);
            var name = feed < 0 ? entry : entry[..feed];
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                value = feed < 0 ? "" : entry[(feed + 1)..];
            }
        }

        return (value, files);
    }

    /// <summary>The configuration files git reads for this repository, whether or not they exist yet.</summary>
    private IEnumerable<string> ConfigCandidates()
    {
        yield return Path.Combine(CommonDir, "config");
        yield return Path.Combine(GitDir, "config.worktree");
        yield return Path.Combine(GitDir, "HEAD");
        if (Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL") is { Length: > 0 } global)
        {
            yield return global;
        }
        else
        {
            var home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } set
                ? set
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
                ? config
                : Path.Combine(home, ".config");
            yield return Path.Combine(xdg, "git", "config");
            yield return Path.Combine(home, ".gitconfig");
        }

        if (Environment.GetEnvironmentVariable("GIT_CONFIG_SYSTEM") is { Length: > 0 } system)
        {
            yield return system;
        }
        else if (!OperatingSystem.IsWindows())
        {
            yield return "/etc/gitconfig";
        }
    }

    private static string ConfigEnvironment() =>
        string.Join(
            '\0',
            ConfigVariables
                .Select(Environment.GetEnvironmentVariable)
                .Concat(
                    Enumerable
                        .Range(
                            0,
                            int.TryParse(Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"), out var n) ? n : 0
                        )
                        .SelectMany(i =>
                            new[]
                            {
                                Environment.GetEnvironmentVariable($"GIT_CONFIG_KEY_{i}"),
                                Environment.GetEnvironmentVariable($"GIT_CONFIG_VALUE_{i}"),
                            }
                        )
                )
                .Select(value => value ?? "")
        );
}
