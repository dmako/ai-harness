namespace Harness.Scm;

/// <summary>A remote matched to a configured SCM, with the API's identifier of the project.</summary>
/// <param name="Project">The project's full path: group/sub/repo (GitLab) or owner/repo (Forgejo, GitHub).</param>
public sealed record ScmMatch(ScmEndpoint Endpoint, string Project, ScmRemoteUrl Remote);

public enum ScmMatchStatus
{
    Matched,
    NoMatch,
    Ambiguous,
}

/// <summary>What matching one remote URL found; <see cref="Reason" /> says why when nothing matched.</summary>
public sealed record ScmMatchResult(
    ScmMatchStatus Status,
    ScmMatch? Match,
    IReadOnlyList<ScmEndpoint> Candidates,
    string Reason
);

/// <summary>
/// Maps a git remote URL to a configured SCM. An SCM matches when its URL's host equals the remote's
/// (case-insensitive), or one of its <see cref="ScmAlias" />es serves it; for http(s) remotes the port must match
/// too, while ssh and git remotes may use any port and user. The SCM's base path (an instance under
/// https://host/gitlab), or a URL alias's own path, must prefix an http(s) remote's path; an ssh remote may include
/// it or not. Of the matching SCMs the one with the longest base path wins; equally long ones are ambiguous, an alias
/// that two SCMs share included. The rest of the path is the project: at least namespace/repo on GitLab, exactly
/// owner/repo on Forgejo and GitHub.
/// </summary>
public static class ScmMatcher
{
    /// <param name="address">The remote's URL as git config holds it.</param>
    public static ScmMatchResult Match(IEnumerable<ScmEndpoint> endpoints, string address)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var configured = endpoints.ToList();
        if (!ScmRemoteUrl.TryParse(address, out var remote))
        {
            return new ScmMatchResult(
                ScmMatchStatus.NoMatch,
                null,
                [],
                $"'{ScmRemoteUrls.Safe(address)}' is not a URL an SCM serves (http, https, ssh, git or scp-like user@host:path)."
            );
        }

        if (configured.Count == 0)
        {
            return new ScmMatchResult(ScmMatchStatus.NoMatch, null, [], "No SCM is configured.");
        }

        var candidates = configured
            .Select(endpoint => (endpoint, score: Score(endpoint, remote, out var project), project))
            .Where(candidate => candidate.score >= 0)
            .ToList();
        if (candidates.Count == 0)
        {
            return new ScmMatchResult(
                ScmMatchStatus.NoMatch,
                null,
                [],
                $"No configured SCM serves {remote.Host}{(remote.IsHttp && remote.Port is not null ? ":" + remote.Port : "")} "
                    + $"with a project path like '{string.Join('/', remote.Path)}' (configured: "
                    + string.Join(", ", configured.Select(Describe))
                    + ")."
            );
        }

        var best = candidates.Max(candidate => candidate.score);
        var winners = candidates.Where(candidate => candidate.score == best).ToList();
        if (winners.Count > 1)
        {
            return new ScmMatchResult(
                ScmMatchStatus.Ambiguous,
                null,
                winners.Select(winner => winner.endpoint).ToList(),
                $"{remote} matches several SCMs equally: "
                    + string.Join(", ", winners.Select(winner => winner.endpoint.Name))
                    + "; choose one for this project with 'harness scm use <name>' (or in harness ui under Agents › Tools & providers), "
                    + "or set [scm] name in its .harness/config.toml."
            );
        }

        var (endpoint, _, project) = winners[0];
        return new ScmMatchResult(ScmMatchStatus.Matched, new ScmMatch(endpoint, project, remote), [endpoint], "");
    }

    /// <summary>An SCM as a no-match reason names it: its name and URL, and its aliases if it has any.</summary>
    private static string Describe(ScmEndpoint endpoint) =>
        $"{endpoint.Name} {endpoint.BaseUrl}"
        + (endpoint.Aliases.Count is 0 ? "" : $" (aliases {string.Join(", ", endpoint.Aliases)})");

    /// <summary>
    /// The length of the base path that matched, through the SCM's URL or the best of its aliases, or -1 when the
    /// endpoint does not serve the remote.
    /// </summary>
    private static int Score(ScmEndpoint endpoint, ScmRemoteUrl remote, out string project)
    {
        project = "";
        var best = -1;
        var url = endpoint.BaseUrl;
        IEnumerable<Base> bases =
        [
            new(url.Scheme, url.Host, url.Port, endpoint.BasePath),
            .. endpoint.Aliases.Select(alias =>
                alias.IsHost
                    ? new Base(null, alias.Host, null, endpoint.BasePath)
                    : new Base(alias.Scheme, alias.Host, alias.Port, alias.Path)
            ),
        ];
        foreach (var candidate in bases)
        {
            var score = Score(endpoint.Kind, candidate, remote, out var found);
            if (score > best)
            {
                (best, project) = (score, found);
            }
        }

        return best;
    }

    /// <summary>
    /// Whether one address of an SCM serves the remote. The host must be the remote's. An http(s) remote needs a web
    /// base on its port and its base path as a prefix; an ssh or git remote may use any port and user (an ssh://
    /// alias's explicit port aside) and may leave a web base's path out, while an ssh:// alias's path is required. A
    /// host name alone serves every protocol and port.
    /// </summary>
    private static int Score(ScmKind kind, Base candidate, ScmRemoteUrl remote, out string project)
    {
        project = "";
        if (!string.Equals(candidate.Host, remote.Host, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        var webBase = candidate.Scheme is "http" or "https";
        var sshBase = candidate.Scheme is "ssh";
        var reachable = (remote.IsHttp, candidate.Scheme) switch
        {
            (_, null) => true,
            (true, _) => webBase && candidate.EffectivePort == remote.EffectivePort,
            (false, "ssh") => candidate.Port is null || candidate.Port == remote.EffectivePort,
            _ => true,
        };
        if (!reachable)
        {
            return -1;
        }

        var basePath = candidate.Path;
        var prefixed =
            remote.Path.Count > basePath.Count
            && basePath
                .Select((segment, index) => (segment, index))
                .All(pair => string.Equals(pair.segment, remote.Path[pair.index], StringComparison.OrdinalIgnoreCase));
        var skip = prefixed ? basePath.Count : 0;
        if (!prefixed && basePath.Count > 0 && (remote.IsHttp || sshBase))
        {
            return -1;
        }

        var rest = remote.Path.Skip(skip).ToList();
        var valid = kind switch
        {
            ScmKind.Gitlab => rest.Count >= 2,
            ScmKind.Forgejo or ScmKind.Github => rest.Count == 2,
            _ => false,
        };
        if (!valid)
        {
            return -1;
        }

        project = string.Join('/', rest);
        return skip;
    }

    /// <summary>One address an SCM is reached under: its URL, or an alias.</summary>
    /// <param name="Scheme">http, https or ssh; null for a host name alone.</param>
    /// <param name="Port">The port; null for the scheme's default (or any, for ssh and a host alone).</param>
    private sealed record Base(string? Scheme, string Host, int? Port, IReadOnlyList<string> Path)
    {
        public int EffectivePort =>
            Port
            ?? Scheme switch
            {
                "https" => 443,
                "http" => 80,
                _ => 22,
            };
    }
}
