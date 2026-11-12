using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Scm;
using Harness.Scm;

namespace Harness.Cli.Scm;

/// <summary>
/// Choosing the SCM a repository uses on this machine, for 'harness scm use' and harness ui alike: the choice is stored
/// in scm.toml under the repository's common git directory, and a connection that does not serve the repository's
/// remotes is refused instead of stored.
/// </summary>
internal static class ScmProjectChoice
{
    /// <summary>Chooses <paramref name="name" /> for the inspected project's repository, or clears the choice when null.</summary>
    /// <returns>The chosen SCM's name as configured, or null when cleared.</returns>
    internal static async Task<string?> SetAsync(
        ScmConfigurationFile store,
        ProjectInspection inspection,
        string? name,
        CancellationToken cancellationToken
    )
    {
        var repository =
            inspection.Location.Repository
            ?? throw new ScmConfigurationException(
                $"{inspection.Location.Root} is not in a Git repository: an SCM is chosen for a repository."
            );
        string? chosen = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var entries = store.List();
            var entry =
                entries.FirstOrDefault(scm => string.Equals(scm.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ScmConfigurationException(
                    $"No SCM is named '{name.Trim()}' (configured: "
                        + string.Join(", ", entries.Select(scm => scm.Name).DefaultIfEmpty("none"))
                        + ")."
                );
            var trial = ScmProjectMatcher.Resolve(
                entries.Select(scm => scm.Endpoint).ToList(),
                new ScmProjectResolver(store).Remotes(repository.CommonDir),
                inspection.Location.Configuration.Value.Scm.ToOverride() with
                {
                    Name = "",
                    Choice = entry.Name,
                }
            );
            if (trial.Status is not ScmResolutionStatus.Matched)
            {
                throw new ScmConfigurationException(trial.Reason);
            }

            chosen = entry.Name;
        }

        await store
            .SetChoiceAsync(ScmProjectResolver.RepositoryKey(repository), chosen, cancellationToken)
            .ConfigureAwait(false);
        return chosen;
    }
}
