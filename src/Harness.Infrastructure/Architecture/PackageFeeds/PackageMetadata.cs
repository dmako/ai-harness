using System.Collections.Concurrent;
using Harness.Core.Architecture;
using Harness.Infrastructure.Configuration;

namespace Harness.Infrastructure.Architecture.PackageFeeds;

/// <summary>How a lookup may read: the network for what is not fresh, or the cache alone.</summary>
public enum PackageLookupMode
{
    Network,
    CacheOnly,
}

/// <summary>
/// A lookup's answer: each package's status by <see cref="PackageCheck.Key"/>, the sources that could not be read,
/// and notes for what was skipped.
/// </summary>
public sealed record PackageLookup(
    IReadOnlyDictionary<string, PackageStatus> Packages,
    IReadOnlyList<PackageSourceProblem> Problems,
    IReadOnlyList<string> Notes
);

/// <summary>
/// Newer stable versions and known vulnerabilities of NuGet packages from the sources the project's NuGet
/// configuration resolves to (TASK-0141), through the V3 protocol and a cache on disk: a version list or service index
/// is kept 24 h, the vulnerability index is read again after 24 h and only its changed pages are downloaded. A
/// cache-only lookup (sync's findings, HARNESS_NUGET_OFFLINE) uses what is cached up to a week old.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Globalization",
    "CA1308:Normalize strings to uppercase",
    Justification = "NuGet's V3 protocol names packages in lower case."
)]
public sealed class PackageMetadata(
    PackageMetadataCache cache,
    HttpClient? http = null,
    Func<DateTimeOffset>? clock = null
)
{
    /// <summary>How long a cached answer counts as fresh.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(24);

    /// <summary>The oldest cached answer a cache-only lookup uses.</summary>
    public static readonly TimeSpan KeptAtMost = TimeSpan.FromDays(7);

    /// <summary>Requests to the same source and package in flight, shared by concurrent lookups of this process.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task>> InFlight = new(StringComparer.Ordinal);

    private readonly NuGetFeed _feed = new(http ?? NuGetFeed.Default);
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public static PackageMetadata For(string globalConfigurationPath, HttpClient? http = null) =>
        new(PackageMetadataCache.For(globalConfigurationPath), http);

    /// <summary>Whether this process must not read package sources (HARNESS_NUGET_OFFLINE=1): the cache alone.</summary>
    public static bool Offline =>
        Environment.GetEnvironmentVariable("HARNESS_NUGET_OFFLINE") is { Length: > 0 } value
        && value is not ("0" or "false");

    public PackageMetadataCache Cache => cache;

    /// <summary>
    /// The status of each package at the version it is used in (unresolved or floating versions are skipped), from
    /// the enabled http(s) sources NuGet would take it from.
    /// </summary>
    public async Task<PackageLookup> LookupAsync(
        NuGetSettings settings,
        IEnumerable<(string Name, string Version)> packages,
        PackageLookupMode mode,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(packages);
        var wanted = packages
            .Select(package => (package.Name, Version: NuGetVersion.InUse(package.Version)))
            .Where(package => package.Name.Length > 0 && package.Version is not null)
            .DistinctBy(package => PackageCheck.Key(package.Name, package.Version!.Text))
            .ToArray();
        List<PackageSourceProblem> problems = [];
        HashSet<string> notes = new(StringComparer.Ordinal);
        Dictionary<string, (NuGetService? Service, IReadOnlyList<VulnerabilityPage> Pages)> sources = new(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (
            var source in wanted
                .SelectMany(package => settings.SourcesFor(package.Name))
                .DistinctBy(source => source.Location, StringComparer.OrdinalIgnoreCase)
        )
        {
            if (!Http(source))
            {
                notes.Add($"{source.Key} is a local folder: its packages are not looked up");
                continue;
            }

            var credential = settings.Credentials.GetValueOrDefault(source.Key);
            try
            {
                var service = await ServiceAsync(source, credential, mode, token).ConfigureAwait(false);
                var pages = service is { Vulnerabilities.Length: > 0 }
                    ? await VulnerabilityPagesAsync(source, service, credential, mode, token).ConfigureAwait(false)
                    : [];
                sources[source.Location] = (service, pages);
            }
            catch (PackageSourceException error)
            {
                problems.Add(new(source.Key, source.Location, error.Kind, error.Message));
                sources[source.Location] = (null, []);
            }
        }

        using var gate = new SemaphoreSlim(6);
        var statuses = await Task.WhenAll(
                wanted.Select(async package =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        return await StatusAsync(
                                settings,
                                sources,
                                package.Name,
                                package.Version!,
                                mode,
                                problems,
                                token
                            )
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                })
            )
            .ConfigureAwait(false);
        return new(
            statuses
                .OfType<PackageStatus>()
                .ToDictionary(status => PackageCheck.Key(status.Name, status.Version), StringComparer.Ordinal),
            problems.DistinctBy(problem => problem.Location, StringComparer.OrdinalIgnoreCase).ToArray(),
            [.. notes]
        );
    }

    /// <summary>
    /// Reads each enabled http(s) source's service index from the source itself (as harness doctor does), refreshing
    /// the cache; the sources that could not be read.
    /// </summary>
    public async Task<IReadOnlyList<PackageSourceProblem>> CheckSourcesAsync(
        NuGetSettings settings,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<PackageSourceProblem> problems = [];
        foreach (var source in settings.Sources.Where(source => source.Enabled && Http(source)))
        {
            try
            {
                var service = await _feed
                    .ServiceAsync(source.Location, settings.Credentials.GetValueOrDefault(source.Key), token)
                    .ConfigureAwait(false);
                cache.SaveService(source.Location, new(Now, service.BaseAddress, service.Vulnerabilities));
            }
            catch (PackageSourceException error)
            {
                problems.Add(new(source.Key, source.Location, error.Kind, error.Message));
            }
        }

        return problems;
    }

    private static bool Http(NuGetSource source) =>
        Uri.TryCreate(source.Location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private bool Usable(DateTimeOffset fetched, PackageLookupMode mode) =>
        Now - fetched is var age
        && age >= TimeSpan.Zero
        && age < (mode is PackageLookupMode.CacheOnly ? KeptAtMost : Fresh);

    private async Task<NuGetService?> ServiceAsync(
        NuGetSource source,
        NuGetCredential? credential,
        PackageLookupMode mode,
        CancellationToken token
    )
    {
        if (cache.Service(source.Location) is { } cached && Usable(cached.Fetched, mode))
        {
            return new(cached.BaseAddress, cached.Vulnerabilities);
        }

        if (mode is PackageLookupMode.CacheOnly)
        {
            return null;
        }

        var service = await Shared(
                "service " + source.Location,
                () => _feed.ServiceAsync(source.Location, credential, token)
            )
            .ConfigureAwait(false);
        cache.SaveService(source.Location, new(Now, service.BaseAddress, service.Vulnerabilities));
        return service;
    }

    /// <summary>The vulnerability pages, the index read again once a day and a page downloaded when it changed.</summary>
    private async Task<IReadOnlyList<VulnerabilityPage>> VulnerabilityPagesAsync(
        NuGetSource source,
        NuGetService service,
        NuGetCredential? credential,
        PackageLookupMode mode,
        CancellationToken token
    )
    {
        var cached = cache.Vulnerabilities(source.Location);
        if (cached is not null && Usable(cached.Fetched, mode) || mode is PackageLookupMode.CacheOnly)
        {
            return cached is not null && Usable(cached.Fetched, mode) ? cached.Pages : [];
        }

        return await Shared(
                "vulnerabilities " + source.Location,
                async () =>
                {
                    var pages = await _feed
                        .VulnerabilityIndexAsync(service.Vulnerabilities, credential, token)
                        .ConfigureAwait(false);
                    foreach (var page in pages)
                    {
                        var known = cached?.Pages.FirstOrDefault(old => old.Name == page.Name);
                        if (
                            known is null
                            || known.Updated != page.Updated
                            || !cache.HasPage(source.Location, page.Name)
                        )
                        {
                            cache.SavePage(
                                source.Location,
                                page.Name,
                                await _feed.TextAsync(page.Location, credential, token).ConfigureAwait(false)
                            );
                        }
                    }

                    cache.SaveVulnerabilities(source.Location, new(Now, pages));
                    return pages;
                }
            )
            .ConfigureAwait(false);
    }

    private async Task<PackageStatus?> StatusAsync(
        NuGetSettings settings,
        Dictionary<string, (NuGetService? Service, IReadOnlyList<VulnerabilityPage> Pages)> sources,
        string name,
        NuGetVersion version,
        PackageLookupMode mode,
        List<PackageSourceProblem> problems,
        CancellationToken token
    )
    {
        List<string> versions = [];
        List<PackageAdvisory> advisories = [];
        var found = false;
        foreach (var source in settings.SourcesFor(name))
        {
            if (!sources.TryGetValue(source.Location, out var read) || read.Service is not { } service)
            {
                continue;
            }

            foreach (var page in read.Pages)
            {
                if (cache.Page(source.Location, page.Name).TryGetValue(name, out var listed))
                {
                    advisories.AddRange(listed);
                }
            }

            try
            {
                if (
                    await VersionsAsync(
                            source,
                            service,
                            settings.Credentials.GetValueOrDefault(source.Key),
                            name,
                            mode,
                            token
                        )
                        .ConfigureAwait(false) is
                    { Found: true } list
                )
                {
                    found = true;
                    versions.AddRange(list.Versions);
                }
            }
            catch (PackageSourceException error)
            {
                lock (problems)
                {
                    problems.Add(new(source.Key, source.Location, error.Kind, error.Message));
                }
            }
        }

        if (!found && advisories.Count is 0)
        {
            return null;
        }

        return new(
            name,
            version.Text,
            PackageCheck.NewerStable(version, versions)?.Text,
            PackageCheck.Vulnerable(version, advisories, versions)
        );
    }

    private async Task<CachedVersions?> VersionsAsync(
        NuGetSource source,
        NuGetService service,
        NuGetCredential? credential,
        string name,
        PackageLookupMode mode,
        CancellationToken token
    )
    {
        if (cache.Versions(source.Location, name) is { } cached && Usable(cached.Fetched, mode))
        {
            return cached;
        }

        if (mode is PackageLookupMode.CacheOnly)
        {
            return null;
        }

        return await Shared(
                "versions " + source.Location + " " + name.ToLowerInvariant(),
                async () =>
                {
                    var list = await _feed
                        .VersionsAsync(service.BaseAddress, name, credential, token)
                        .ConfigureAwait(false);
                    CachedVersions read = new(Now, list is not null, list ?? []);
                    cache.SaveVersions(source.Location, name, read);
                    return read;
                }
            )
            .ConfigureAwait(false);
    }

    /// <summary>One request per key at a time in this process; later callers wait for the first one's answer.</summary>
    private static async Task<T> Shared<T>(string key, Func<Task<T>> request)
    {
        var lazy = InFlight.GetOrAdd(key, _ => new Lazy<Task>(request));
        try
        {
            return await ((Task<T>)lazy.Value).ConfigureAwait(false);
        }
        finally
        {
            InFlight.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, lazy));
        }
    }
}
