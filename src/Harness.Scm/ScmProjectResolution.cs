namespace Harness.Scm;

/// <summary>A git remote: its name and URL as git would use it (after <c>url.&lt;base&gt;.insteadOf</c>).</summary>
/// <param name="Address">Its URL (http, ssh or scp-like user@host:path).</param>
/// <param name="PushAddresses">Its <c>pushurl</c> values, also tried when the URL matches nothing.</param>
public sealed record ScmRemote(string Name, string Address, IReadOnlyList<string> PushAddresses)
{
    public ScmRemote(string name, string address)
        : this(name, address, []) { }

    /// <summary>The URL as it may be shown: without a password or token embedded in it.</summary>
    public string SafeAddress => ScmRemoteUrls.Safe(Address);
}

/// <summary>A project's <c>[scm]</c> configuration: an SCM, project or remote chosen by hand. Empty fields are unset.</summary>
public sealed record ScmProjectOverride(string Name = "", string Project = "", string Remote = "")
{
    public bool IsEmpty => Name.Length is 0 && Project.Length is 0 && Remote.Length is 0;
}

public enum ScmResolutionStatus
{
    /// <summary>A configured SCM serves the project.</summary>
    Matched,

    /// <summary>No SCM is configured on this machine.</summary>
    NoScm,

    /// <summary>The repository has no remote (or no Git repository).</summary>
    NoRemote,

    /// <summary>No remote matches a configured SCM.</summary>
    NoMatch,

    /// <summary>A remote matches several SCMs equally.</summary>
    Ambiguous,

    /// <summary>The project's [scm] configuration names an SCM or remote that does not exist.</summary>
    Invalid,
}

/// <summary>Which SCM and project a repository belongs to, or why none could be chosen.</summary>
/// <param name="Remote">The remote the match came from, when one did.</param>
/// <param name="Overridden">Whether the project's [scm] configuration chose the SCM or project.</param>
/// <param name="Reason">Why there is no match; empty when matched.</param>
public sealed record ScmResolution(
    ScmResolutionStatus Status,
    ScmMatch? Match,
    string? Remote,
    IReadOnlyList<ScmRemote> Remotes,
    bool Overridden,
    string Reason
)
{
    public static string Name(ScmResolutionStatus status) =>
        status switch
        {
            ScmResolutionStatus.Matched => "matched",
            ScmResolutionStatus.NoScm => "no_scm",
            ScmResolutionStatus.NoRemote => "no_remote",
            ScmResolutionStatus.NoMatch => "no_match",
            ScmResolutionStatus.Ambiguous => "ambiguous",
            ScmResolutionStatus.Invalid => "invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown resolution status."),
        };

    /// <summary>
    /// The resolution as status output and tools show it: SCM name, type, URL and project when matched, else the
    /// reason, with the remotes seen (never a token or password).
    /// </summary>
    public Dictionary<string, object?> Describe()
    {
        Dictionary<string, object?> wire = new(StringComparer.Ordinal) { ["status"] = Name(Status) };
        if (Match is { } match)
        {
            wire["name"] = match.Endpoint.Name;
            wire["type"] = ScmKinds.Name(match.Endpoint.Kind);
            wire["url"] = match.Endpoint.BaseUrl.AbsoluteUri.TrimEnd('/');
            wire["project"] = match.Project;
            if (Remote is not null)
            {
                wire["remote"] = Remote;
            }

            if (Overridden)
            {
                wire["overridden"] = true;
            }

            return wire;
        }

        wire["reason"] = Reason;
        wire["remotes"] = Remotes.Select(remote => remote.Name + " " + remote.SafeAddress).ToArray();
        return wire;
    }

    /// <summary>One line for text output: "work (gitlab) group/repo" or the reason.</summary>
    public string Summary()
    {
        if (Match is not { } match)
        {
            return Reason;
        }

        var from = Remote is null ? "" : $" from remote {Remote}";
        return $"{match.Endpoint.Name} ({ScmKinds.Name(match.Endpoint.Kind)}) {match.Project}{from}";
    }
}

/// <summary>The current project's SCM, for tools that act on it (MCP, doctor, status).</summary>
public interface IScmProjectContext
{
    /// <summary>Which configured SCM and project this repository belongs to; never throws for a missing match.</summary>
    Task<ScmResolution> ResolveScmAsync(CancellationToken cancellationToken);
}

/// <summary>URLs as they may be shown.</summary>
public static class ScmRemoteUrls
{
    /// <summary>
    /// The URL without the password or token an http(s) remote may embed (<c>https://user:token@host/…</c>); other
    /// forms are returned as they are, since an ssh user name is no secret.
    /// </summary>
    public static string Safe(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (
            !address.Contains("://", StringComparison.Ordinal)
            || !Uri.TryCreate(address, UriKind.Absolute, out var parsed)
            || parsed.UserInfo.Length is 0
        )
        {
            return address;
        }

        return new UriBuilder(parsed) { UserName = "", Password = "" }
            .Uri
            .AbsoluteUri;
    }
}

/// <summary>
/// Chooses a repository's SCM: the project's [scm] configuration first (an SCM by name, a fixed project, a remote),
/// otherwise the remotes in order (origin first, then the rest as git config lists them), the first whose URL (or
/// a push URL) <see cref="ScmMatcher" /> maps to one SCM winning. A remote that matches several SCMs equally stops
/// the search as ambiguous instead of falling through to a later remote.
/// </summary>
public static class ScmProjectMatcher
{
    public static ScmResolution Resolve(
        IReadOnlyList<ScmEndpoint> endpoints,
        IReadOnlyList<ScmRemote> remotes,
        ScmProjectOverride? configured = null
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(remotes);
        var overrides = configured ?? new ScmProjectOverride();
        var ordered = remotes.OrderBy(remote => remote.Name == "origin" ? 0 : 1).ToList();
        if (endpoints.Count is 0)
        {
            return Failed(
                ScmResolutionStatus.NoScm,
                ordered,
                "No SCM is configured on this machine; add one with 'harness scm add'."
            );
        }

        ScmEndpoint? chosen = null;
        if (overrides.Name.Length > 0)
        {
            chosen = endpoints.FirstOrDefault(endpoint =>
                string.Equals(endpoint.Name, overrides.Name, StringComparison.OrdinalIgnoreCase)
            );
            if (chosen is null)
            {
                return Failed(
                    ScmResolutionStatus.Invalid,
                    ordered,
                    $"The project's [scm] name '{overrides.Name}' is not a configured SCM (configured: "
                        + string.Join(", ", endpoints.Select(endpoint => endpoint.Name))
                        + ")."
                );
            }
        }

        if (overrides.Remote.Length > 0)
        {
            ordered = ordered.Where(remote => remote.Name == overrides.Remote).ToList();
            if (ordered.Count is 0)
            {
                return Failed(
                    ScmResolutionStatus.Invalid,
                    remotes,
                    $"The project's [scm] remote '{overrides.Remote}' is not a remote of this repository (remotes: "
                        + (remotes.Count is 0 ? "none" : string.Join(", ", remotes.Select(remote => remote.Name)))
                        + ")."
                );
            }
        }

        if (chosen is not null && overrides.Project.Length > 0)
        {
            var remote = ordered.FirstOrDefault();
            return new ScmResolution(
                ScmResolutionStatus.Matched,
                new ScmMatch(
                    chosen,
                    overrides.Project.Trim().Trim('/'),
                    remote is not null && ScmRemoteUrl.TryParse(remote.Address, out var parsed)
                        ? parsed
                        : new ScmRemoteUrl("https", chosen.BaseUrl.Host, null, [])
                ),
                remote?.Name,
                remotes,
                true,
                ""
            );
        }

        if (ordered.Count is 0)
        {
            return Failed(
                ScmResolutionStatus.NoRemote,
                remotes,
                "The repository has no remote; add one, or set [scm] name and project in the project's configuration."
            );
        }

        return MatchRemotes(chosen is null ? endpoints : [chosen], ordered, remotes, overrides);
    }

    private static ScmResolution MatchRemotes(
        IReadOnlyList<ScmEndpoint> endpoints,
        IReadOnlyList<ScmRemote> ordered,
        IReadOnlyList<ScmRemote> remotes,
        ScmProjectOverride overrides
    )
    {
        foreach (var remote in ordered)
        {
            foreach (var url in new[] { remote.Address }.Concat(remote.PushAddresses))
            {
                var result = ScmMatcher.Match(endpoints, url);
                switch (result.Status)
                {
                    case ScmMatchStatus.Matched:
                        var match =
                            overrides.Project.Length > 0
                                ? result.Match! with
                                {
                                    Project = overrides.Project.Trim().Trim('/'),
                                }
                                : result.Match!;
                        return new ScmResolution(
                            ScmResolutionStatus.Matched,
                            match,
                            remote.Name,
                            remotes,
                            !overrides.IsEmpty,
                            ""
                        );
                    case ScmMatchStatus.Ambiguous:
                        return Failed(ScmResolutionStatus.Ambiguous, remotes, $"Remote {remote.Name}: {result.Reason}");
                    default:
                        break;
                }
            }
        }

        return Failed(
            ScmResolutionStatus.NoMatch,
            remotes,
            "No remote ("
                + string.Join(", ", ordered.Select(remote => remote.Name + " " + remote.SafeAddress))
                + ") matches a configured SCM ("
                + string.Join(
                    ", ",
                    endpoints.Select(endpoint => endpoint.Name + " " + endpoint.BaseUrl.AbsoluteUri.TrimEnd('/'))
                )
                + "); check the SCM's URL, or set [scm] name and project in the project's configuration."
        );
    }

    private static ScmResolution Failed(ScmResolutionStatus status, IReadOnlyList<ScmRemote> remotes, string reason) =>
        new(status, null, null, remotes, false, reason);
}
