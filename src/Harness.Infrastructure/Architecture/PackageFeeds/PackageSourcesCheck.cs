using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Projects;

namespace Harness.Infrastructure.Architecture.PackageFeeds;

/// <summary>
/// harness doctor's 'package-sources' check (TASK-0141): whether the package sources the repository's NuGet
/// configuration names can be read for newer versions and vulnerabilities, and how to fix one that cannot.
/// </summary>
public static class PackageSourcesCheck
{
    public const string Name = "package-sources";

    /// <summary>The check, or null when there is nothing to check: no .NET project, lookups off, or offline.</summary>
    public static async Task<HealthCheck?> CheckAsync(
        string root,
        string globalConfigurationPath,
        string? userFile,
        HttpClient? http,
        CancellationToken token
    )
    {
        if (PackageMetadata.Offline || !UsesNuGet(root) || !Enabled(root, globalConfigurationPath))
        {
            return null;
        }

        var settings = NuGetSettings.Load(root, userFile);
        var read = settings.Sources.Where(source => source.Enabled).ToArray();
        var problems = await PackageMetadata
            .For(globalConfigurationPath, http)
            .CheckSourcesAsync(settings, token)
            .ConfigureAwait(false);
        return Health(read.Select(source => source.Key).ToArray(), problems, settings);
    }

    /// <summary>The check from what reading the sources gave.</summary>
    public static HealthCheck Health(
        IReadOnlyList<string> sources,
        IReadOnlyList<PackageSourceProblem> problems,
        NuGetSettings settings
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(settings);
        if (problems.Count is 0)
        {
            return new(
                Name,
                "ok",
                sources.Count is 0
                    ? "no package source is enabled: no upgrades or vulnerabilities are looked up"
                    : "upgrades and vulnerabilities are read from " + string.Join(", ", sources)
            );
        }

        return new(
            Name,
            "warn",
            "package metadata cannot be read from "
                + string.Join(
                    "; ",
                    problems.Select(problem =>
                        $"{problem.Source} ({problem.Location}): {problem.Kind}, {problem.Detail}"
                    )
                )
                + "; Architecture shows no upgrades or vulnerabilities from it",
            string.Join("\n", problems.Select(problem => Fix(problem, settings)))
        );
    }

    private static string Fix(PackageSourceProblem problem, NuGetSettings settings) =>
        problem.Kind switch
        {
            "unauthorized" => $"give '{problem.Source}' credentials in packageSourceCredentials of nuget.config or "
                + "the user-level NuGet.Config (Username and ClearTextPassword, which may name an environment "
                + "variable such as %FEED_TOKEN%): dotnet nuget update source "
                + Quote(problem.Source)
                + " --username <user> --password <token> --store-password-in-clear-text"
                + (
                    settings.Credentials.GetValueOrDefault(problem.Source) is { Problem.Length: > 0 } credential
                        ? " (its credentials now: " + credential.Problem + ")"
                        : ""
                ),
            "invalid" => $"point '{problem.Source}' at the feed's NuGet V3 service index (…/v3/index.json); "
                + "V2 feeds are not read",
            _ => $"check the URL of '{problem.Source}' and the network or proxy, or disable the source: "
                + "dotnet nuget disable source "
                + Quote(problem.Source),
        };

    private static string Quote(string key) => key.Contains(' ', StringComparison.Ordinal) ? "\"" + key + "\"" : key;

    private static bool Enabled(string root, string globalConfigurationPath)
    {
        try
        {
            return ConfigurationLoader
                .LoadFiles(globalConfigurationPath, ConfigurationPaths.ProjectPath(root))
                .Value.Architecture.Packages.Enabled;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Whether the repository has a .csproj or packages.config within four folders of its root.</summary>
    private static bool UsesNuGet(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4,
            IgnoreInaccessible = true,
        };
        try
        {
            return Directory.Exists(root)
                && (
                    Directory.EnumerateFiles(root, "*.csproj", options).Any()
                    || Directory.EnumerateFiles(root, "packages.config", options).Any()
                );
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
