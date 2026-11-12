using Harness.Scm;
using Harness.Scm.Forgejo;
using Harness.Scm.Github;
using Harness.Scm.Gitlab;

namespace Harness.Cli.Scm;

/// <summary>The SCM providers harness ships, and the connection test every front end runs.</summary>
internal static class ScmProviders
{
    /// <summary>A connection test waits at most this long per request, so a page or doctor stays responsive.</summary>
    internal static ScmLimits TestLimits { get; } = ScmLimits.Default with { Timeout = TimeSpan.FromSeconds(15) };

    internal static ScmProviderRegistry Registry(HttpMessageHandler? handler = null, ScmLimits? limits = null) =>
        new([
            new GitlabProvider(handler, limits),
            new ForgejoProvider(handler, limits),
            new GithubProvider(handler, limits),
        ]);

    /// <summary>Tests one connection; never throws for a failed check.</summary>
    internal static async Task<ScmConnectionTest> TestAsync(
        ScmConnection connection,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null
    )
    {
        using var client = Registry(handler, TestLimits).Connect(connection);
        return await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A test result as the UI and --json show it: never the token.</summary>
    internal static Dictionary<string, object?> Wire(ScmConnectionTest test, DateTimeOffset at) =>
        new(StringComparer.Ordinal)
        {
            ["ok"] = test.Ok,
            ["version"] = test.Version,
            ["user"] = test.User?.Username,
            ["failure"] = test.Failure is { } failure ? ScmErrors.Name(failure) : null,
            ["message"] = test.Message,
            ["hint"] = test.Hint,
            ["at"] = at.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        };
}
