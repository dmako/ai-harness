using System.Collections.Concurrent;
using System.Globalization;
using Harness.Infrastructure.Git;
using Harness.Scm;

namespace Harness.Infrastructure.Scm;

/// <summary>
/// Finds a repository's SCM from its git remotes without running git (a git process costs ~150 ms on Windows):
/// remotes come from the repository's own config file (in the common directory, so worktrees share them),
/// rewritten by <c>url.&lt;base&gt;.insteadOf</c> rules from that file and the user's global git config, and are
/// cached per repository until one of those files changes. The machine's SCMs are read from the store on each call,
/// so a changed scm.toml is seen at once.
/// </summary>
public sealed class ScmProjectResolver(IScmConfiguration configuration, string? home = null)
{
    private static readonly ConcurrentDictionary<string, (string Stamp, IReadOnlyList<ScmRemote> Remotes)> Cache = new(
        StringComparer.Ordinal
    );

    private readonly string _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public async Task<ScmResolution> ResolveAsync(
        string directory,
        ScmProjectOverride? overrides,
        CancellationToken cancellationToken
    )
    {
        GitRepository? repository;
        try
        {
            repository = await GitRepository.DiscoverAsync(directory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            repository = null;
        }

        return Resolve(repository, overrides);
    }

    /// <summary>
    /// The repository's resolution, with the SCM chosen for it on this machine (by its common git directory, so its
    /// worktrees share the choice); a null repository has no remotes and no choice.
    /// </summary>
    public ScmResolution Resolve(GitRepository? repository, ScmProjectOverride? overrides)
    {
        IReadOnlyList<ScmEndpoint> endpoints;
        string? choice;
        try
        {
            endpoints = configuration.List().Select(entry => entry.Endpoint).ToList();
            choice = repository is null ? null : configuration.ChoiceFor(RepositoryKey(repository));
        }
        catch (ScmConfigurationException exception)
        {
            return new ScmResolution(ScmResolutionStatus.Invalid, null, null, [], false, exception.Message);
        }

        var remotes = repository is null ? [] : Remotes(repository.CommonDir);
        return ScmProjectMatcher.Resolve(
            endpoints,
            remotes,
            (overrides ?? new ScmProjectOverride()) with
            {
                Choice = choice ?? "",
            }
        );
    }

    /// <summary>The key a repository's SCM choice is stored under: its common git directory.</summary>
    public static string RepositoryKey(GitRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return Path.GetFullPath(repository.CommonDir);
    }

    /// <summary>The remotes of the repository whose common git directory is given, in config order.</summary>
    public IReadOnlyList<ScmRemote> Remotes(string commonDirectory)
    {
        var local = Path.Combine(commonDirectory, "config");
        var files = new[] { local }.Concat(GlobalFiles()).ToArray();
        var stamp = string.Join('|', files.Select(Stamp));
        var key = Path.GetFullPath(commonDirectory);
        if (Cache.TryGetValue(key, out var cached) && cached.Stamp == stamp)
        {
            return cached.Remotes;
        }

        var localEntries = GitConfigFile.Read(local);
        var rewrites = files
            .Skip(1)
            .SelectMany(GitConfigFile.Read)
            .Concat(localEntries)
            .Where(entry => entry is { Section: "URL", Key: "INSTEADOF" } && entry.Value.Length > 0)
            .Select(entry => (Prefix: entry.Value, Base: entry.Subsection))
            .ToList();
        List<ScmRemote> remotes = [];
        foreach (
            var name in localEntries
                .Where(entry => entry.Section is "REMOTE")
                .Select(entry => entry.Subsection)
                .Distinct()
        )
        {
            var own = localEntries.Where(entry => entry.Section is "REMOTE" && entry.Subsection == name).ToList();
            var address = own.FirstOrDefault(entry => entry.Key is "URL")?.Value;
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            remotes.Add(
                new ScmRemote(
                    name,
                    Rewrite(address, rewrites),
                    own.Where(entry => entry.Key is "PUSHURL").Select(entry => Rewrite(entry.Value, rewrites)).ToList()
                )
            );
        }

        Cache[key] = (stamp, remotes);
        return remotes;
    }

    /// <summary>A URL with the longest matching insteadOf prefix replaced by its base, as git does.</summary>
    public static string Rewrite(string address, IReadOnlyList<(string Prefix, string Base)> rewrites)
    {
        var best = rewrites
            .Where(rule => address.StartsWith(rule.Prefix, StringComparison.Ordinal))
            .OrderByDescending(rule => rule.Prefix.Length)
            .FirstOrDefault();
        return best.Prefix is null ? address : best.Base + address[best.Prefix.Length..];
    }

    private IEnumerable<string> GlobalFiles()
    {
        // as git: GIT_CONFIG_GLOBAL replaces both global files
        if (Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL") is { Length: > 0 } global)
        {
            yield return global;
            yield break;
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        yield return string.IsNullOrEmpty(xdg)
            ? Path.Combine(_home, ".config", "git", "config")
            : Path.Combine(xdg, "git", "config");
        yield return Path.Combine(_home, ".gitconfig");
    }

    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return path
            + "="
            + (
                file.Exists
                    ? file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                        + ":"
                        + file.Length.ToString(CultureInfo.InvariantCulture)
                    : "-"
            );
    }
}
