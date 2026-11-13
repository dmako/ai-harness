using System.Text.Json.Nodes;
using Harness.Core.Web;
using Harness.Infrastructure.Architecture.PackageFeeds;
using Harness.Infrastructure.Configuration;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    /// <summary>The longest the packages endpoint reads package sources before it answers with what it has.</summary>
    private static readonly TimeSpan PackagesWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Newer stable versions and known vulnerabilities of the NuGet packages named in ?packages=name@version,… from
    /// the sources the project's NuGet configuration resolves to (TASK-0141). The Architecture views ask for it after
    /// they have drawn, so they never wait for a package source; the answer is keyed by lower-case name@version, and
    /// sources that cannot be read become notes. HARNESS_NUGET_OFFLINE answers from the cache alone.
    /// </summary>
    private async Task<JsonNode?> PackagesAsync(JsonObject project, UiReadRequest request, CancellationToken token)
    {
        var root = UiProjectCatalog.Text(project, "root");
        var wanted = request
            .Parameter("packages")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Split('@', 2))
            .Where(parts => parts is [{ Length: > 0 }, { Length: > 0 }])
            .Select(parts => (Name: parts[0], Version: parts[1]))
            .Take(1000)
            .ToArray();
        JsonObject result = new() { ["packages"] = new JsonObject(), ["notes"] = new JsonArray() };
        if (wanted.Length is 0 || root.Length is 0 || !Directory.Exists(root))
        {
            return result;
        }

        if (!PackagesEnabled(root))
        {
            result["notes"]!
                .AsArray()
                .Add((JsonNode)"package lookups are off ([architecture.packages] enabled = false)");
            return result;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(PackagesWait);
        PackageLookup lookup;
        try
        {
            lookup = await PackageMetadata
                .For(lifecycle.GlobalConfigurationPath)
                .LookupAsync(
                    NuGetSettings.Load(root, lifecycle.NuGetUserFile),
                    wanted,
                    PackageMetadata.Offline ? PackageLookupMode.CacheOnly : PackageLookupMode.Network,
                    limit.Token
                );
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            result["notes"]!
                .AsArray()
                .Add(
                    (JsonNode)
                        $"the package sources did not answer within {PackagesWait.TotalSeconds:0} s; no marks for now"
                );
            return result;
        }

        var packages = result["packages"]!.AsObject();
        foreach (var (key, status) in lookup.Packages)
        {
            packages[key] = new JsonObject
            {
                ["name"] = status.Name,
                ["version"] = status.Version,
                ["latest"] = status.Latest,
                ["vulnerabilities"] = new JsonArray([
                    .. status.Vulnerabilities.Select(vulnerability =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["severity"] = vulnerability.Severity,
                                ["url"] = vulnerability.Link,
                                ["versions"] = vulnerability.Versions,
                                ["fixed"] = vulnerability.Fixed,
                            }
                    ),
                ]),
            };
        }

        var notes = result["notes"]!.AsArray();
        foreach (var problem in lookup.Problems)
        {
            notes.Add(
                (JsonNode)
                    $"{problem.Source} could not be read ({problem.Kind}): no marks from it; harness doctor says more"
            );
        }

        foreach (var note in lookup.Notes)
        {
            notes.Add((JsonNode)note);
        }

        return result;
    }

    private bool PackagesEnabled(string root)
    {
        try
        {
            return ConfigurationLoader
                .LoadFiles(lifecycle.GlobalConfigurationPath, ConfigurationPaths.ProjectPath(root))
                .Value.Architecture.Packages.Enabled;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
