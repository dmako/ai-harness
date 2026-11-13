using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Git;

namespace Harness.Infrastructure.Projects;

/// <summary>What 'harness doctor --fix nuget-config' did or would do to the project's .harness/config.toml.</summary>
public sealed record ConfigurationFix(string Path, bool Changed, IReadOnlyList<string> Lines, string Message, bool Ok);

/// <summary>
/// The nuget-config check (<see cref="NuGetProviderCheck"/>) for a project, and its fix: the suggested
/// [providers.nuget] override written into the project's .harness/config.toml, its other lines and comments kept.
/// </summary>
public static class NuGetProviderFix
{
    /// <summary>The check for a project root with its loaded configuration; null without an enabled nuget provider.</summary>
    /// <param name="userNuGetFile">The user-level NuGet.Config; null for this system's, empty for none.</param>
    public static NuGetProviderCheck? Check(LoadedConfiguration loaded, string root, string? userNuGetFile = null)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (NuGetProviderCheck.Find(loaded.Value.Providers) is not { } found)
        {
            return null;
        }

        var (name, provider) = found;
        var pack = ConfigurationLoader.PackDefaults(provider.Pack.Length > 0 ? provider.Pack : NuGetProviderCheck.Pack);
        return NuGetProviderCheck.Evaluate(name, provider, NuGetSettings.Load(root, userNuGetFile), pack);
    }

    public static HealthCheck Health(NuGetProviderCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return new(NuGetProviderCheck.Name, check.Level, check.Detail, check.Fix);
    }

    /// <summary>
    /// Writes the check's override into the project's .harness/config.toml (only shows it with
    /// <paramref name="dryRun"/>). Nothing to fix, or the override already there, changes nothing.
    /// </summary>
    public static async Task<ConfigurationFix> ApplyAsync(
        string directory,
        string globalConfigurationPath,
        bool dryRun,
        string? userNuGetFile = null,
        CancellationToken cancellationToken = default
    )
    {
        var repository = await GitRepository.DiscoverAsync(directory, cancellationToken).ConfigureAwait(false);
        var root = repository.Root;
        var path = ConfigurationPaths.ProjectPath(root);
        var loaded = ConfigurationLoader.LoadFiles(globalConfigurationPath, path);
        var check = Check(loaded, root, userNuGetFile);
        if (check is null)
        {
            return new(path, false, [], "no enabled nuget provider is configured: nothing to fix", true);
        }

        if (check.Keys.Count is 0)
        {
            return new(path, false, [], $"{NuGetProviderCheck.Name}: nothing to fix ({check.Detail})", true);
        }

        var before = File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : "";
        var bom = before.StartsWith('\uFEFF', StringComparison.Ordinal) ? "\uFEFF" : "";
        var text = before[bom.Length..];
        var update = ConfigurationExample.SetTable(
            text.Length is 0 ? ConfigurationExample.ProjectHeader : text,
            check.Table,
            check.Keys
        );
        if (update.Problem.Length > 0)
        {
            return new(path, false, [], $"{path} {update.Problem}", false);
        }

        if (!update.Changed)
        {
            return new(path, false, [], $"{path} already has this [{check.Table}] override", true);
        }

        try
        {
            ConfigurationLoader.LoadFiles(
                globalConfigurationPath,
                path,
                new Dictionary<string, string> { [path] = update.Text }
            );
        }
        catch (InvalidDataException error)
        {
            return new(path, false, [], "the override would not load: " + error.Message, false);
        }

        var lines = ConfigurationExample.Changes(text, update.Text);
        if (dryRun)
        {
            return new(path, true, lines, $"Would set [{check.Table}] in {path}:", true);
        }

        await ProjectLifecycle
            .WriteConfigurationAsync(path, bom + update.Text, cancellationToken)
            .ConfigureAwait(false);
        return new(path, true, lines, $"Set [{check.Table}] in {path}:", true);
    }
}
