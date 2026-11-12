using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Scm;
using Harness.Scm;

namespace Harness.Infrastructure.Projects;

public sealed partial class HarnessStateSession : IScmSession
{
    /// <summary>The machine's SCM connections (scm.toml beside the global configuration).</summary>
    public IScmConfiguration ScmConfiguration =>
        ScmConfigurationFile.ForGlobal(globalConfigurationPath ?? ConfigurationPaths.GlobalPath());

    /// <summary>The SCM providers, supplied by the host (the CLI composes GitLab, Forgejo and GitHub).</summary>
    public ScmProviderRegistry? ScmProviders { get; init; }

    /// <summary>
    /// This project's SCM from its own repository's remotes (a worktree or workspace member resolves against its
    /// repository), or the project's [scm] configuration.
    /// </summary>
    public Task<ScmResolution> ResolveScmAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new ScmProjectResolver(ScmConfiguration).Resolve(
                location.Repository,
                location.Configuration.Value.Scm.ToOverride()
            )
        );
    }
}
