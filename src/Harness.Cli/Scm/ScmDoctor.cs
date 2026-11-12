using Harness.Infrastructure.Projects;
using Harness.Scm;

namespace Harness.Cli.Scm;

/// <summary>The HTTP handler SCM calls use; tests pass a fake server, everyone else the shared handler.</summary>
internal sealed class ScmHttpOptions(HttpMessageHandler? handler = null)
{
    public HttpMessageHandler? Handler => handler;
}

/// <summary>One configured SCM's connection test, or why it could not run (no token).</summary>
internal sealed record ScmTestOutcome(ScmEntry Entry, ScmConnection? Connection, ScmConnectionTest Test);

/// <summary>
/// What doctor says about SCMs, as health checks the user can act on: the machine's scm.toml (readable, owner-only),
/// each SCM's token and connection (tested in parallel with a short timeout), and the project's SCM and whether its
/// project is reachable with that token. Never shows a token.
/// </summary>
internal static class ScmDoctor
{
    /// <summary>Tests every configured SCM (or the one named) side by side.</summary>
    internal static async Task<IReadOnlyList<ScmTestOutcome>> TestAllAsync(
        IScmConfiguration store,
        IEnumerable<ScmEntry> entries,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken
    ) =>
        await Task.WhenAll(
                entries.Select(async entry =>
                {
                    ScmConnection connection;
                    try
                    {
                        connection = store.Resolve(entry.Name);
                    }
                    catch (ScmConfigurationException exception)
                    {
                        return new ScmTestOutcome(
                            entry,
                            null,
                            new ScmConnectionTest(
                                false,
                                null,
                                null,
                                ScmErrorKind.Unauthorized,
                                exception.Message,
                                entry.TokenEnv is { } variable
                                    ? $"Set {variable} in the environment harness runs in, or store a token with 'harness scm set {entry.Name} --token-stdin'."
                                    : $"Store a token with 'harness scm set {entry.Name} --token-stdin'."
                            )
                        );
                    }

                    var test = await ScmProviders
                        .TestAsync(connection, cancellationToken, handler)
                        .ConfigureAwait(false);
                    return new ScmTestOutcome(entry, connection, test);
                })
            )
            .ConfigureAwait(false);

    /// <summary>The checks for this machine's SCMs and, when given, the project's resolution.</summary>
    internal static async Task<IReadOnlyList<HealthCheck>> ChecksAsync(
        IScmConfiguration store,
        ScmResolution? project,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken
    )
    {
        var (checks, outcomes) = await MachineAsync(store, handler, cancellationToken).ConfigureAwait(false);
        if (project is not null && outcomes.Count > 0)
        {
            checks.Add(await ProjectCheckAsync(project, outcomes, handler, cancellationToken).ConfigureAwait(false));
        }

        return checks;
    }

    /// <summary>The machine's checks (scm.toml, each SCM's token and connection) and the tests behind them.</summary>
    internal static async Task<(List<HealthCheck> Checks, IReadOnlyList<ScmTestOutcome> Outcomes)> MachineAsync(
        IScmConfiguration store,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken
    )
    {
        List<HealthCheck> checks = [];
        var file = store.Inspect();
        if (file.Error.Length > 0)
        {
            checks.Add(
                new HealthCheck(
                    "scm",
                    "fail",
                    file.Error,
                    $"correct {file.Path}, or remove it and add the SCMs again with 'harness scm add'"
                )
            );
            return (checks, []);
        }

        var entries = store.List();
        if (entries.Count is 0)
        {
            checks.Add(
                new HealthCheck(
                    "scm",
                    "info",
                    "no SCM configured (optional: 'harness scm add' lets agents work with GitLab, Forgejo and GitHub merge requests)"
                )
            );
            return (checks, []);
        }

        var chmod = OperatingSystem.IsWindows() ? "" : $", or run 'chmod 600 {file.Path}'";
        checks.Add(
            file.OwnerOnly is false
                ? new HealthCheck(
                    "scm",
                    "warn",
                    $"{file.Path} holds access tokens and is {file.PermissionDetail}",
                    "make it readable only by you: any 'harness scm set' rewrites it owner-only" + chmod
                )
                : new HealthCheck("scm", "ok", $"{entries.Count} SCM(s) in {file.Path}")
        );
        if (KeysCheck(file, entries) is { } keys)
        {
            checks.Add(keys);
        }

        var outcomes = await TestAllAsync(store, entries, handler, cancellationToken).ConfigureAwait(false);
        checks.AddRange(outcomes.Select(Check));
        return (checks, outcomes);
    }

    /// <summary>
    /// The stored tokens' encryption: tokens still in plain text, and the key folder they are encrypted with (missing,
    /// or open to others). Null when no SCM stores its token in the file.
    /// </summary>
    internal static HealthCheck? KeysCheck(ScmFileState file, IReadOnlyList<ScmEntry> entries)
    {
        var stored = entries.Where(entry => entry.TokenSource is ScmTokenSource.Stored).ToList();
        if (stored.Count is 0)
        {
            return null;
        }

        var plain = stored.Where(entry => entry.PlainText).Select(entry => entry.Name).ToList();
        if (plain.Count > 0)
        {
            return new HealthCheck(
                "scm keys",
                "warn",
                $"{plain.Count} token(s) stored unencrypted in {file.Path} ({string.Join(", ", plain)})",
                $"encrypt them: any change with 'harness scm' re-saves every stored token encrypted, e.g. 'harness scm set {plain[0]} --token-stdin'"
            );
        }

        if (!file.KeysExist)
        {
            return new HealthCheck(
                "scm keys",
                "fail",
                $"the stored tokens are encrypted, but their key folder {file.KeyPath} is missing",
                "restore scm-keys from the backup it was taken with scm.toml, or store each token again with 'harness scm set <name> --token-stdin'"
            );
        }

        var chmod = OperatingSystem.IsWindows() ? "" : $", or run 'chmod 700 {file.KeyPath}'";
        return file.KeysOwnerOnly is false
            ? new HealthCheck(
                "scm keys",
                "warn",
                $"{file.KeyPath} holds the keys the tokens are encrypted with and is {file.KeysPermissionDetail}",
                "make it accessible only to you: any 'harness scm' change tightens it" + chmod
            )
            : new HealthCheck(
                "scm keys",
                "ok",
                $"{stored.Count} stored token(s) encrypted with the keys in {file.KeyPath}"
            );
    }

    internal static HealthCheck Check(ScmTestOutcome outcome)
    {
        var name = "scm:" + outcome.Entry.Name;
        var where =
            $"{ScmKinds.Display(outcome.Entry.Endpoint.Kind)} {outcome.Entry.Endpoint.BaseUrl.AbsoluteUri.TrimEnd('/')}";
        var test = outcome.Test;
        if (test.Ok)
        {
            return new HealthCheck(name, "ok", $"{where}: version {test.Version}, signed in as {test.User?.Username}");
        }

        var kind = test.Failure is { } failure ? ScmErrors.Name(failure) + ": " : "";
        return new HealthCheck(name, "fail", $"{where}: {kind}{test.Message}", test.Hint);
    }

    /// <summary>The project's SCM and whether its project is reachable with that SCM's token (one call).</summary>
    internal static async Task<HealthCheck> ProjectCheckAsync(
        ScmResolution resolution,
        IReadOnlyList<ScmTestOutcome> outcomes,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken
    )
    {
        const string Name = "scm project";
        if (resolution.Match is not { } match)
        {
            return resolution.Status switch
            {
                ScmResolutionStatus.NoRemote => new HealthCheck(Name, "info", resolution.Reason),
                ScmResolutionStatus.NoScm => new HealthCheck(Name, "info", resolution.Reason),
                ScmResolutionStatus.Ambiguous => new HealthCheck(
                    Name,
                    "warn",
                    resolution.Reason,
                    "choose the SCM this project uses with 'harness scm use <name>' (one of "
                        + string.Join(", ", resolution.Candidates.Select(endpoint => endpoint.Name))
                        + "), or set [scm] name in .harness/config.toml"
                ),
                ScmResolutionStatus.Invalid when resolution.Source is ScmChoiceSource.Machine => new HealthCheck(
                    Name,
                    "warn",
                    resolution.Reason,
                    "choose an SCM that serves the repository with 'harness scm use <name>', or go back to detecting it with 'harness scm use --auto'"
                ),
                _ => new HealthCheck(
                    Name,
                    "warn",
                    resolution.Reason,
                    "fix the SCM's URL with 'harness scm set', or set [scm] name and project in .harness/config.toml"
                ),
            };
        }

        var summary = resolution.Summary();
        var outcome = outcomes.FirstOrDefault(item =>
            string.Equals(item.Entry.Name, match.Endpoint.Name, StringComparison.OrdinalIgnoreCase)
        );
        if (outcome is not { Connection: { } connection, Test.Ok: true })
        {
            return new HealthCheck(
                Name,
                "warn",
                summary + $": not checked, SCM {match.Endpoint.Name} failed its connection test",
                "fix the scm:" + match.Endpoint.Name + " check above"
            );
        }

        try
        {
            using var client = ScmProviders.Registry(handler, ScmProviders.TestLimits).Connect(connection);
            var found = await client.GetProjectAsync(match.Project, cancellationToken).ConfigureAwait(false);
            return new HealthCheck(Name, "ok", $"{summary}: reachable ({found.Path})");
        }
        catch (ScmException exception)
        {
            return new HealthCheck(
                Name,
                "fail",
                $"{summary}: {ScmErrors.Name(exception.Kind)}: {exception.Message}",
                exception.Kind is ScmErrorKind.NotFound
                    ? "check the project path (set [scm] project in .harness/config.toml if the remote's differs) and that the token's user can see it"
                    : ScmErrors.Hint(exception.Kind)
            );
        }
    }
}
