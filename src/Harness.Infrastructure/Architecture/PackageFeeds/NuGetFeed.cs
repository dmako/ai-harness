using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Harness.Core.Architecture;
using Harness.Infrastructure.Configuration;

namespace Harness.Infrastructure.Architecture.PackageFeeds;

/// <summary>What a source's NuGet V3 service index offers: the flat container and the vulnerability index, if any.</summary>
public sealed record NuGetService(string BaseAddress, string Vulnerabilities);

/// <summary>One page the VulnerabilityInfo index lists (base, update), with when it last changed.</summary>
public sealed record VulnerabilityPage(string Name, string Location, string Updated);

/// <summary>Why a package source could not be read: unreachable, unauthorized or invalid (no NuGet V3 feed).</summary>
public sealed record PackageSourceProblem(string Source, string Location, string Kind, string Detail);

/// <summary>A source request that failed in a way that says something about the source.</summary>
public sealed class PackageSourceException : Exception
{
    public PackageSourceException() { }

    public PackageSourceException(string message)
        : base(message) { }

    public PackageSourceException(string message, Exception innerException)
        : base(message, innerException) { }

    public PackageSourceException(string kind, string message)
        : base(message) => Kind = kind;

    /// <summary>unreachable, unauthorized or invalid.</summary>
    public string Kind { get; } = "unreachable";
}

/// <summary>
/// Reads the NuGet V3 protocol: the service index (PackageBaseAddress/3.0.0, VulnerabilityInfo/6.7.0), a package's
/// version list from the flat container ({base}/{id-lower}/index.json) and the vulnerability pages
/// ({"id-lower": [{"severity": 0-3, "url", "versions": range}]}). The parsing is separate from the requests.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Globalization",
    "CA1308:Normalize strings to uppercase",
    Justification = "NuGet's V3 protocol names packages in lower case."
)]
public sealed class NuGetFeed(HttpClient http)
{
    private static readonly Lazy<HttpClient> Shared = new(() =>
        Create(
            new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            }
        )
    );

    /// <summary>The process's client for package sources: decompressing, 20 s per request.</summary>
    public static HttpClient Default => Shared.Value;

    public static HttpClient Create(HttpMessageHandler handler, bool disposeHandler = true)
    {
        var client = new HttpClient(handler, disposeHandler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("harness");
        return client;
    }

    public async Task<NuGetService> ServiceAsync(
        string location,
        NuGetCredential? credential,
        CancellationToken token
    ) => ParseService(await GetAsync(location, credential, token).ConfigureAwait(false) ?? throw Invalid(location));

    /// <summary>The versions the flat container lists, or null when the source does not have the package.</summary>
    public async Task<IReadOnlyList<string>?> VersionsAsync(
        string baseAddress,
        string id,
        NuGetCredential? credential,
        CancellationToken token
    )
    {
        var location = baseAddress.TrimEnd('/') + "/" + Uri.EscapeDataString(id.ToLowerInvariant()) + "/index.json";
        return await GetAsync(location, credential, token).ConfigureAwait(false) is { } json
            ? ParseVersions(json)
            : null;
    }

    public async Task<IReadOnlyList<VulnerabilityPage>> VulnerabilityIndexAsync(
        string location,
        NuGetCredential? credential,
        CancellationToken token
    ) =>
        ParseVulnerabilityIndex(
            await GetAsync(location, credential, token).ConfigureAwait(false) ?? throw Invalid(location)
        );

    public async Task<string> TextAsync(string location, NuGetCredential? credential, CancellationToken token) =>
        await GetAsync(location, credential, token).ConfigureAwait(false) ?? throw Invalid(location);

    /// <summary>The body of a GET, null for 404; a failure says whether the source is unreachable or unauthorized.</summary>
    private async Task<string?> GetAsync(string location, NuGetCredential? credential, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        if (
            credential is { Problem.Length: 0, Username.Length: > 0 }
            || credential is { Problem.Length: 0, Password.Length: > 0 }
        )
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.Username + ":" + credential.Password))
            );
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new PackageSourceException("unreachable", error.Message);
        }
        catch (TaskCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new PackageSourceException(
                "unreachable",
                "no answer within " + http.Timeout.TotalSeconds + " s: " + error.Message
            );
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return null;
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new PackageSourceException(
                        "unauthorized",
                        (int)response.StatusCode
                            + " "
                            + response.ReasonPhrase
                            + (credential is { Problem.Length: > 0 } ? " (" + credential.Problem + ")" : "")
                    );
                case var status when !response.IsSuccessStatusCode:
                    throw new PackageSourceException(
                        "unreachable",
                        ((int)status).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase
                    );
            }

            try
            {
                return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new PackageSourceException("unreachable", error.Message);
            }
        }
    }

    private static PackageSourceException Invalid(string location) =>
        new("invalid", location + " was not found: not a NuGet V3 service index");

    /// <summary>The service index's flat container and vulnerability index (empty when it offers none).</summary>
    public static NuGetService ParseService(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var resources =
                document.RootElement.TryGetProperty("resources", out var list) && list.ValueKind is JsonValueKind.Array
                    ? list.EnumerateArray().ToArray()
                    : throw new PackageSourceException("invalid", "the service index lists no resources");
            string Find(string prefix) =>
                resources
                    .Where(resource => resource.ValueKind is JsonValueKind.Object)
                    .Where(resource =>
                        Types(resource).Any(type => type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    )
                    .Select(resource => Text(resource, "@id"))
                    .FirstOrDefault(id => id.Length > 0)
                ?? "";
            var flat = Find("PackageBaseAddress/3.0.0");
            return flat.Length is 0
                ? throw new PackageSourceException("invalid", "the service index offers no PackageBaseAddress/3.0.0")
                : new(flat, Find("VulnerabilityInfo/"));
        }
        catch (JsonException error)
        {
            throw new PackageSourceException("invalid", "the service index is not JSON: " + error.Message);
        }
    }

    private static IEnumerable<string> Types(JsonElement resource) =>
        resource.TryGetProperty("@type", out var type)
            ? type.ValueKind switch
            {
                JsonValueKind.String => [type.GetString() ?? ""],
                JsonValueKind.Array => type.EnumerateArray()
                    .Where(item => item.ValueKind is JsonValueKind.String)
                    .Select(item => item.GetString() ?? ""),
                _ => [],
            }
            : [];

    /// <summary>The flat container's version list ({"versions": [...]}).</summary>
    public static IReadOnlyList<string> ParseVersions(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return
                document.RootElement.ValueKind is JsonValueKind.Object
                && document.RootElement.TryGetProperty("versions", out var versions)
                && versions.ValueKind is JsonValueKind.Array
                ? versions
                    .EnumerateArray()
                    .Where(item => item.ValueKind is JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToArray()
                : [];
        }
        catch (JsonException error)
        {
            throw new PackageSourceException("invalid", "a version list is not JSON: " + error.Message);
        }
    }

    /// <summary>The VulnerabilityInfo index: its pages (base, update) with when each last changed.</summary>
    public static IReadOnlyList<VulnerabilityPage> ParseVulnerabilityIndex(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind is JsonValueKind.Array
                ? document
                    .RootElement.EnumerateArray()
                    .Where(item => item.ValueKind is JsonValueKind.Object)
                    .Select(item => new VulnerabilityPage(
                        Text(item, "@name"),
                        Text(item, "@id"),
                        Text(item, "@updated")
                    ))
                    .Where(page => page.Name.Length > 0 && page.Location.Length > 0)
                    .ToArray()
                : throw new PackageSourceException("invalid", "the vulnerability index is no list");
        }
        catch (JsonException error)
        {
            throw new PackageSourceException("invalid", "the vulnerability index is not JSON: " + error.Message);
        }
    }

    /// <summary>A vulnerability page: the advisories by lower-case package id.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<PackageAdvisory>> ParseVulnerabilityPage(string json)
    {
        Dictionary<string, IReadOnlyList<PackageAdvisory>> advisories = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return advisories;
            }

            foreach (var package in document.RootElement.EnumerateObject())
            {
                if (package.Value.ValueKind is not JsonValueKind.Array)
                {
                    continue;
                }

                advisories[package.Name] = package
                    .Value.EnumerateArray()
                    .Where(item => item.ValueKind is JsonValueKind.Object)
                    .Select(item => new PackageAdvisory(
                        item.TryGetProperty("severity", out var severity) && severity.TryGetInt32(out var level)
                            ? level
                            : -1,
                        Text(item, "url"),
                        Text(item, "versions")
                    ))
                    .Where(advisory => advisory.Versions.Length > 0)
                    .ToArray();
            }
        }
        catch (JsonException error)
        {
            throw new PackageSourceException("invalid", "a vulnerability page is not JSON: " + error.Message);
        }

        return advisories;
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
