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
/// Maps a git remote URL to a configured SCM. An SCM matches when its host equals the remote's (case-insensitive);
/// for http(s) remotes the port must match too, while ssh and git remotes may use any port and user. The SCM's base
/// path (an instance under https://host/gitlab) must prefix an http(s) remote's path; an ssh remote may include it or
/// not. Of the matching SCMs the one with the longest base path wins; equally long ones are ambiguous. The rest of
/// the path is the project: at least namespace/repo on GitLab, exactly owner/repo on Forgejo and GitHub.
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
                    + string.Join(", ", configured.Select(endpoint => $"{endpoint.Name} {endpoint.BaseUrl}"))
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
                    + "; choose one in the project's [scm] configuration."
            );
        }

        var (endpoint, _, project) = winners[0];
        return new ScmMatchResult(ScmMatchStatus.Matched, new ScmMatch(endpoint, project, remote), [endpoint], "");
    }

    /// <summary>The length of the base path that matched, or -1 when the endpoint does not serve the remote.</summary>
    private static int Score(ScmEndpoint endpoint, ScmRemoteUrl remote, out string project)
    {
        project = "";
        if (!string.Equals(endpoint.BaseUrl.Host, remote.Host, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (remote.IsHttp && endpoint.BaseUrl.Port != remote.EffectivePort)
        {
            return -1;
        }

        var basePath = endpoint.BasePath;
        var prefixed =
            remote.Path.Count > basePath.Count
            && basePath
                .Select((segment, index) => (segment, index))
                .All(pair => string.Equals(pair.segment, remote.Path[pair.index], StringComparison.OrdinalIgnoreCase));
        var skip = prefixed ? basePath.Count : 0;
        if (!prefixed && remote.IsHttp && basePath.Count > 0)
        {
            return -1;
        }

        var rest = remote.Path.Skip(skip).ToList();
        var valid = endpoint.Kind switch
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
}
