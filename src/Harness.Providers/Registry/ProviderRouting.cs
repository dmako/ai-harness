using Harness.Core.Providers;

namespace Harness.Providers.Registry;

/// <summary>Adapter-neutral registration settings; Cli translates loaded configuration at composition time.</summary>
public sealed record ProviderRegistration(string Name, string Type, Func<IProvider> Create,
    bool Enabled = true, bool Context = true, IReadOnlyList<string>? Languages = null, IReadOnlyList<string>? Only = null, TimeSpan Timeout = default);

public static class ProviderRouting
{
    public static ProviderRegistry Build(IEnumerable<ProviderRegistration> registrations, string root, bool light = false)
    {
        var entries = registrations.ToArray();
        var timeout = entries.Aggregate(TimeSpan.FromSeconds(30), (current, entry) => entry.Timeout + TimeSpan.FromSeconds(1) > current ? entry.Timeout + TimeSpan.FromSeconds(1) : current);
        var registry = new ProviderRegistry(timeout);
        registry.SetPathFilter(new NestedWorktreeFilter(root).Skip);
        var languages = DetectLanguages(root);
        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            if (!entry.Enabled || light && !entry.Context || entry.Type is not ("mcp" or "harness"))
            {
                continue;
            }
            registry.Register(Route(entry.Create(), entry.Languages ?? [], entry.Only ?? [], languages));
            if (!entry.Context)
            {
                registry.ExcludeFromContext(entry.Name);
            }
        }
        return registry;
    }

    public static string Inactive(IReadOnlyList<string> allowed, IReadOnlyList<string> detected)
    {
        var project = detected.Count is 0 ? "no recognized language" : string.Join(", ", detected);
        return allowed.Count is 0 || allowed.Any(language => detected.Contains(language, StringComparer.Ordinal)) ? "" :
            $"not used here: serves {string.Join(", ", allowed)}; project: {project}";
    }

    public static IProvider Route(IProvider provider, IReadOnlyList<string> languages, IReadOnlyList<string> only, IReadOnlyList<string> detected)
    {
        var inactive = Inactive(languages, detected);
        return (inactive.Length, only.Count, provider) switch
        {
            (0, 0, _) => provider,
            (_, _, IProviderRefresher refresher) => new RoutedRefresher(refresher, only.ToArray(), inactive),
            _ => new Routed(provider, only.ToArray(), inactive),
        };
    }

    public static IReadOnlyList<string> DetectLanguages(string root)
    {
        var markers = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["csharp"] = ["*.sln", "*.slnx", "*.csproj"],
            ["typescript"] = ["tsconfig.json", "package.json"],
            ["go"] = ["go.mod"],
            ["python"] = ["pyproject.toml", "requirements.txt", "setup.py"],
            ["java"] = ["pom.xml", "build.gradle", "build.gradle.kts"],
        };
        var directories = new List<string> { root };
        try
        {
            directories.AddRange(Directory.GetDirectories(root));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return markers.Where(pair => directories.Any(directory => pair.Value.Any(pattern => HasMatch(directory, pattern))))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool HasMatch(string directory, string pattern)
    {
        try
        {
            // Go's filepath.Glob also matches directories bearing a marker's name.
            return Directory.EnumerateFileSystemEntries(directory).Any(path => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path), ignoreCase: false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private class Routed(IProvider provider, string[] only, string inactive) : IProvider, IVersionedProvider
    {
        protected string InactiveReason => inactive;
        public string Name => provider.Name;
        public string ServerVersion => (provider as IVersionedProvider)?.ServerVersion ?? "";
        public IReadOnlyList<CapabilityInfo> Capabilities() => inactive.Length > 0 ? [] : provider.Capabilities().Where(info => only.Contains(info.Capability, StringComparer.Ordinal)).ToArray();
        public ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default) => inactive.Length > 0 ? ValueTask.FromResult(new ProviderHealth("idle", inactive)) : provider.HealthAsync(probe, cancellationToken);
        public Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken = default) =>
            inactive.Length > 0 || !only.Contains(request.Capability, StringComparer.Ordinal) ? Task.FromException<ProviderResult>(new UnsupportedCapabilityException()) : provider.ExecuteAsync(request, cancellationToken);
        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private sealed class RoutedRefresher(IProviderRefresher provider, string[] only, string inactive) : Routed(provider, only, inactive), IProviderRefresher
    {
        public Task<RefreshResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default) =>
            InactiveReason.Length > 0 ? Task.FromResult(new RefreshResult(true, InactiveReason)) : provider.RefreshAsync(request, cancellationToken);
    }
}
