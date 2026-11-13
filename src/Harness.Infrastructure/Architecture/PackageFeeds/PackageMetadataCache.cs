using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Architecture;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Architecture.PackageFeeds;

/// <summary>A cached service index: what the source offers and when it was read.</summary>
public sealed record CachedService(DateTimeOffset Fetched, string BaseAddress, string Vulnerabilities);

/// <summary>A cached version list; Found is false when the source does not have the package.</summary>
public sealed record CachedVersions(DateTimeOffset Fetched, bool Found, IReadOnlyList<string> Versions);

/// <summary>The cached vulnerability index: its pages, each stored beside it, and when it was read.</summary>
public sealed record CachedVulnerabilities(DateTimeOffset Fetched, IReadOnlyList<VulnerabilityPage> Pages);

/// <summary>
/// Package metadata on disk beside the machine's harness configuration (cache/nuget), a folder per source (named by
/// a hash of its URL): service.json, versions/&lt;id-lower&gt;.json and vulnerabilities/ (index.json and one file per
/// page as the source wrote it). Files are replaced whole; a file that cannot be read counts as missing.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Globalization",
    "CA1308:Normalize strings to uppercase",
    Justification = "NuGet's V3 protocol names packages in lower case."
)]
public sealed class PackageMetadataCache(string directory)
{
    private static readonly ConcurrentDictionary<
        string,
        (DateTime Written, IReadOnlyDictionary<string, IReadOnlyList<PackageAdvisory>> Advisories)
    > Pages = new(StringComparer.Ordinal);

    public string Directory => directory;

    public static PackageMetadataCache For(string globalConfigurationPath) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(globalConfigurationPath))!, "cache", "nuget"));

    /// <summary>A source's folder: the first 16 hex digits of the SHA-256 of its lower-case URL.</summary>
    public string SourceDirectory(string source) =>
        Path.Combine(
            directory,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Trim().ToLowerInvariant())))[..16]
        );

    public CachedService? Service(string source) =>
        Read(
            Path.Combine(SourceDirectory(source), "service.json"),
            node => new CachedService(Time(node), Text(node, "base"), Text(node, "vulnerabilities"))
        );

    public void SaveService(string source, CachedService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        Write(
            Path.Combine(SourceDirectory(source), "service.json"),
            new JsonObject
            {
                ["source"] = source,
                ["fetched"] = Stamp(service.Fetched),
                ["base"] = service.BaseAddress,
                ["vulnerabilities"] = service.Vulnerabilities,
            }
        );
    }

    public CachedVersions? Versions(string source, string id) =>
        Read(
            VersionsPath(source, id),
            node => new CachedVersions(
                Time(node),
                node["found"]?.GetValue<bool>() ?? false,
                node["versions"] is JsonArray versions
                    ? versions.Select(version => version?.GetValue<string>() ?? "").Where(v => v.Length > 0).ToArray()
                    : []
            )
        );

    public void SaveVersions(string source, string id, CachedVersions versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        Write(
            VersionsPath(source, id),
            new JsonObject
            {
                ["fetched"] = Stamp(versions.Fetched),
                ["found"] = versions.Found,
                ["versions"] = new JsonArray([.. versions.Versions.Select(version => (JsonNode)version)]),
            }
        );
    }

    public CachedVulnerabilities? Vulnerabilities(string source) =>
        Read(
            Path.Combine(SourceDirectory(source), "vulnerabilities", "index.json"),
            node => new CachedVulnerabilities(
                Time(node),
                node["pages"] is JsonArray pages
                    ? pages
                        .OfType<JsonObject>()
                        .Select(page => new VulnerabilityPage(
                            Text(page, "name"),
                            Text(page, "url"),
                            Text(page, "updated")
                        ))
                        .ToArray()
                    : []
            )
        );

    public void SaveVulnerabilities(string source, CachedVulnerabilities index)
    {
        ArgumentNullException.ThrowIfNull(index);
        Write(
            Path.Combine(SourceDirectory(source), "vulnerabilities", "index.json"),
            new JsonObject
            {
                ["fetched"] = Stamp(index.Fetched),
                ["pages"] = new JsonArray([
                    .. index.Pages.Select(page =>
                        (JsonNode)
                            new JsonObject
                            {
                                ["name"] = page.Name,
                                ["url"] = page.Location,
                                ["updated"] = page.Updated,
                            }
                    ),
                ]),
            }
        );
    }

    /// <summary>Whether a page is stored (as of its update stamp, which the index records).</summary>
    public bool HasPage(string source, string name) => File.Exists(PagePath(source, name));

    public void SavePage(string source, string name, string json) => WriteText(PagePath(source, name), json);

    /// <summary>A stored page's advisories by lower-case id, parsed once per file version; empty when missing.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PackageAdvisory>> Page(string source, string name)
    {
        var path = PagePath(source, name);
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (!File.Exists(path))
            {
                return new Dictionary<string, IReadOnlyList<PackageAdvisory>>();
            }

            if (Pages.TryGetValue(path, out var known) && known.Written == written)
            {
                return known.Advisories;
            }

            var advisories = NuGetFeed.ParseVulnerabilityPage(File.ReadAllText(path));
            Pages[path] = (written, advisories);
            return advisories;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PackageSourceException)
        {
            return new Dictionary<string, IReadOnlyList<PackageAdvisory>>();
        }
    }

    /// <summary>
    /// When the cache last changed, from its folders' times (a file replaced in a folder changes the folder's time):
    /// part of what decides whether stored findings are still current.
    /// </summary>
    public string Stamp()
    {
        try
        {
            if (!System.IO.Directory.Exists(directory))
            {
                return "-";
            }

            var newest = new DirectoryInfo(directory)
                .EnumerateDirectories("*", SearchOption.AllDirectories)
                .Select(folder => folder.LastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            return newest.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "-";
        }
    }

    private string VersionsPath(string source, string id) =>
        Path.Combine(SourceDirectory(source), "versions", SafeName(id.ToLowerInvariant()) + ".json");

    private string PagePath(string source, string name) =>
        Path.Combine(SourceDirectory(source), "vulnerabilities", "page-" + SafeName(name.ToLowerInvariant()) + ".json");

    /// <summary>A package id or page name as a file name: NuGet ids are letters, digits, '.', '-' and '_'.</summary>
    private static string SafeName(string name) =>
        new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]);

    private static T? Read<T>(string path, Func<JsonObject, T> read)
        where T : class
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject node ? read(node) : null;
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or JsonException
                        or InvalidOperationException
                        or FormatException
            )
        {
            return null;
        }
    }

    private static void Write(string path, JsonObject node) => WriteText(path, node.ToJsonString());

    /// <summary>Replaces a file whole (written beside it, then moved); a cache that cannot be written stays as it was.</summary>
    private static void WriteText(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            StorageFiles.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { /* A leftover temporary file is harmless. */
            }
        }
    }

    private static string Stamp(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(JsonObject node) =>
        DateTimeOffset.Parse(Text(node, "fetched"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
