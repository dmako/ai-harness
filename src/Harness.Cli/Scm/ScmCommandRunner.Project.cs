using Harness.Core.Serialization;
using Harness.Infrastructure.Scm;
using Harness.Scm;

namespace Harness.Cli.Scm;

/// <summary>
/// 'harness scm use|which': the SCM a repository uses on this machine. The choice is stored in scm.toml under the
/// repository's common git directory (its worktrees share it), never in the committed .harness/config.toml.
/// </summary>
internal sealed partial class ScmCommandRunner
{
    public Task<int> UseAsync(ScmUseSettings settings, CancellationToken token) =>
        GuardAsync("use", () => UseEntryAsync(settings, token));

    public Task<int> WhichAsync(ScmProjectSettings settings, CancellationToken token) =>
        GuardAsync("which", () => WriteWhichAsync(settings, token));

    /// <summary>
    /// Chooses an SCM for the repository (one that serves its remotes), or forgets the choice; then shows the
    /// resolution, noting when the project's .harness/config.toml still decides.
    /// </summary>
    private async Task<int> UseEntryAsync(ScmUseSettings settings, CancellationToken token)
    {
        var inspection = await lifecycle.InspectAsync(settings.Project, token).ConfigureAwait(false);
        var store = Store;
        var name = await ScmProjectChoice
            .SetAsync(store, inspection, settings.Auto ? null : settings.Name, token)
            .ConfigureAwait(false);
        var resolution = (await lifecycle.InspectAsync(settings.Project, token).ConfigureAwait(false)).Scm;
        if (settings.Json)
        {
            await WriteJsonAsync(
                    Which(inspection.Location.Root, inspection.Location.Repository?.CommonDir, resolution, store)
                )
                .ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync(
            name is null
                ? $"{inspection.Location.Root}: no SCM chosen on this machine; detected from the remotes again."
                : $"{inspection.Location.Root}: uses SCM {name} on this machine.",
            CancellationToken.None
        );
        if (resolution?.Source is ScmChoiceSource.Configuration)
        {
            await output.WriteLineAsync(
                "Note: [scm] name in the project's .harness/config.toml still decides; the choice applies once it is removed there.",
                CancellationToken.None
            );
        }

        await WriteResolutionAsync(resolution).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The repository's SCM, project and remote, how it was chosen and the alternatives; exit 1 without one.</summary>
    private async Task<int> WriteWhichAsync(ScmProjectSettings settings, CancellationToken token)
    {
        var inspection = await lifecycle.InspectAsync(settings.Project, token).ConfigureAwait(false);
        var resolution = inspection.Scm;
        if (settings.Json)
        {
            await WriteJsonAsync(
                    Which(inspection.Location.Root, inspection.Location.Repository?.CommonDir, resolution, Store)
                )
                .ConfigureAwait(false);
        }
        else
        {
            await WriteResolutionAsync(resolution).ConfigureAwait(false);
        }

        return resolution?.Status is ScmResolutionStatus.Matched ? 0 : 1;
    }

    private async Task WriteResolutionAsync(ScmResolution? resolution)
    {
        if (resolution?.Match is not { } match)
        {
            await output.WriteLineAsync(
                "SCM:        none: " + (resolution?.Reason ?? "no SCM is configured"),
                CancellationToken.None
            );
            await WriteCandidatesAsync(resolution).ConfigureAwait(false);
            return;
        }

        var endpoint = match.Endpoint;
        await output.WriteLineAsync(
            $"SCM:        {endpoint.Name} ({ScmKinds.Display(endpoint.Kind)}, {endpoint.BaseUrl.AbsoluteUri.TrimEnd('/')})",
            CancellationToken.None
        );
        await output.WriteLineAsync(
            $"project:    {match.Project}" + (resolution.Remote is { } remote ? $" (from remote {remote})" : ""),
            CancellationToken.None
        );
        await output.WriteLineAsync($"chosen:     {resolution.Chosen()}", CancellationToken.None);
        await WriteCandidatesAsync(resolution).ConfigureAwait(false);
    }

    private async Task WriteCandidatesAsync(ScmResolution? resolution)
    {
        if (resolution is { Candidates.Count: > 1 })
        {
            await output.WriteLineAsync(
                "candidates: "
                    + string.Join(", ", resolution.Candidates.Select(endpoint => endpoint.Name))
                    + " (choose one with 'harness scm use <name>')",
                CancellationToken.None
            );
        }
    }

    private Task WriteJsonAsync(JsonObjectMap value) =>
        output.WriteLineAsync(HarnessJson.Serialize(value, Indented), CancellationToken.None);

    /// <summary>The resolution as --json shows it, with each candidate's endpoint and token source (masked).</summary>
    private static JsonObjectMap Which(
        string root,
        string? repository,
        ScmResolution? resolution,
        ScmConfigurationFile store
    )
    {
        var entries = store.List().ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        return new JsonObjectMap
        {
            ["root"] = root,
            ["repository"] = repository,
            ["scm"] = resolution?.Describe(),
            ["candidates"] = (resolution?.Candidates ?? [])
                .Select(endpoint => entries.TryGetValue(endpoint.Name, out var entry) ? Wire(entry) : null)
                .OfType<JsonObjectMap>()
                .ToList(),
        };
    }
}
