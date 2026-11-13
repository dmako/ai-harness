using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace Harness.Infrastructure.Configuration;

/// <summary>
/// One package source as the merged NuGet configuration has it (a feed URL or a local folder), and the file that
/// defined it.
/// </summary>
public sealed record NuGetSource(string Key, string Location, bool Enabled, string File)
{
    /// <summary>Whether this is nuget.org's v3 feed.</summary>
    public bool NuGetOrg =>
        Uri.TryCreate(Location, UriKind.Absolute, out var uri)
        && uri.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A source's credentials from packageSourceCredentials; Problem says why they cannot be used.</summary>
public sealed record NuGetCredential(string Username, string Password, string Problem)
{
    public override string ToString() => Username + " (password hidden)";
}

/// <summary>
/// The NuGet configuration a tool started in a folder reads, as NuGet resolves it: nuget.config in that folder and
/// every folder above it (the closest wins), then the user-level NuGet.Config. Only what decides where packages come
/// from is read: package sources (with &lt;clear/&gt;), disabled sources and package source mapping.
/// </summary>
public sealed class NuGetSettings
{
    private static readonly string[] FileNames = ["nuget.config", "NuGet.config", "NuGet.Config"];

    /// <summary>The nuget.config files from the folder up to the root, the closest first.</summary>
    public IReadOnlyList<string> Files { get; private init; } = [];

    /// <summary>The user-level NuGet.Config, when it exists; empty otherwise.</summary>
    public string UserFile { get; private init; } = "";

    public IReadOnlyList<NuGetSource> Sources { get; private init; } = [];

    /// <summary>Package source mapping: a source's key and its package patterns. Empty when mapping is off.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Mapping { get; private init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The files whose packageSourceMapping is in effect.</summary>
    public IReadOnlyList<string> MappingFiles { get; private init; } = [];

    /// <summary>The closest file whose &lt;clear/&gt; drops the sources of the files below it; empty for none.</summary>
    public string ClearedIn { get; private init; } = "";

    /// <summary>Files that could not be read, with why.</summary>
    public IReadOnlyList<string> Errors { get; private init; } = [];

    /// <summary>
    /// The credentials of packageSourceCredentials by source key, the closest file winning: a user name and a
    /// password (ClearTextPassword with %VARIABLES% expanded, or Password decrypted with DPAPI on Windows), or why the
    /// password cannot be used.
    /// </summary>
    public IReadOnlyDictionary<string, NuGetCredential> Credentials { get; private init; } =
        new Dictionary<string, NuGetCredential>();

    /// <summary>The user-level NuGet.Config's path on this system (it may not exist).</summary>
    public static string DefaultUserFile() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NuGet",
                "NuGet.Config"
            )
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "NuGet",
                "NuGet.Config"
            );

    /// <summary>Reads the configuration a tool started in <paramref name="directory"/> sees.</summary>
    /// <param name="userFile">The user-level file; null for this system's, empty for none.</param>
    public static NuGetSettings Load(string directory, string? userFile = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        List<string> files = [];
        for (var folder = new DirectoryInfo(Path.GetFullPath(directory)); folder is not null; folder = folder.Parent)
        {
            if (Find(folder) is { } file)
            {
                files.Add(file);
            }
        }

        userFile ??= DefaultUserFile();
        var user = userFile.Length > 0 && File.Exists(userFile) ? userFile : "";

        // the lowest priority first: the user-level file, then the folders from the root down to the closest
        var order = (user.Length > 0 ? [user] : Array.Empty<string>()).Concat(Enumerable.Reverse(files)).ToArray();
        List<NuGetSource> sources = [];
        HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> mapping = new(StringComparer.OrdinalIgnoreCase);
        List<string> mappingFiles = [];
        List<string> errors = [];
        Dictionary<string, NuGetCredential> credentials = new(StringComparer.OrdinalIgnoreCase);
        var cleared = "";
        foreach (var file in order)
        {
            XElement root;
            try
            {
                root = XDocument.Load(file).Root ?? new XElement("configuration");
            }
            catch (Exception error) when (error is XmlException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{file}: {error.Message}");
                continue;
            }

            if (root.Element("packageSources") is { } declared)
            {
                foreach (var item in declared.Elements())
                {
                    switch (item.Name.LocalName)
                    {
                        case "clear":
                            sources.Clear();
                            cleared = file;
                            break;
                        case "add" when Attribute(item, "key") is { Length: > 0 } key:
                            sources.RemoveAll(source => source.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                            sources.Add(new(key, Attribute(item, "value"), true, file));
                            break;
                        case "remove" when Attribute(item, "key") is { Length: > 0 } key:
                            sources.RemoveAll(source => source.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                            break;
                    }
                }
            }

            if (root.Element("disabledPackageSources") is { } off)
            {
                foreach (var item in off.Elements())
                {
                    switch (item.Name.LocalName)
                    {
                        case "clear":
                            disabled.Clear();
                            break;
                        case "add" when Attribute(item, "key") is { Length: > 0 } key:
                            if (Attribute(item, "value").Equals("true", StringComparison.OrdinalIgnoreCase))
                            {
                                disabled.Add(key);
                            }
                            else
                            {
                                disabled.Remove(key);
                            }

                            break;
                    }
                }
            }

            if (root.Element("packageSourceCredentials") is { } secrets)
            {
                ReadCredentials(secrets, credentials);
            }

            if (root.Element("packageSourceMapping") is { } mapped)
            {
                foreach (var item in mapped.Elements())
                {
                    if (item.Name.LocalName is "clear")
                    {
                        mapping.Clear();
                        mappingFiles.Clear();
                    }
                    else if (item.Name.LocalName is "packageSource" && Attribute(item, "key") is { Length: > 0 } key)
                    {
                        if (!mapping.TryGetValue(key, out var patterns))
                        {
                            mapping[key] = patterns = [];
                        }

                        patterns.AddRange(
                            item.Elements()
                                .Where(package => package.Name.LocalName is "package")
                                .Select(package => Attribute(package, "pattern"))
                                .Where(pattern => pattern.Length > 0)
                        );
                        if (!mappingFiles.Contains(file))
                        {
                            mappingFiles.Add(file);
                        }
                    }
                }
            }
        }

        if (sources.Count is 0 && cleared.Length is 0)
        {
            // no file names a source: NuGet uses nuget.org (and writes it to a new user-level NuGet.Config)
            sources.Add(new("nuget.org", "https://api.nuget.org/v3/index.json", true, ""));
        }

        return new()
        {
            Files = files,
            UserFile = user,
            Sources = sources.Select(source => source with { Enabled = !disabled.Contains(source.Key) }).ToArray(),
            Mapping = mapping
                .Where(pair => pair.Value.Count > 0)
                .ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value,
                    StringComparer.OrdinalIgnoreCase
                ),
            MappingFiles = mappingFiles,
            ClearedIn = cleared,
            Errors = errors,
            Credentials = credentials,
        };
    }

    /// <summary>One file's packageSourceCredentials: an element per source key (spaces encoded as _x0020_).</summary>
    private static void ReadCredentials(XElement secrets, Dictionary<string, NuGetCredential> credentials)
    {
        foreach (var source in secrets.Elements())
        {
            if (source.Name.LocalName is "clear")
            {
                credentials.Clear();
                continue;
            }

            var values = source
                .Elements()
                .Where(item => item.Name.LocalName is "add")
                .GroupBy(item => Attribute(item, "key"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => Attribute(group.Last(), "value"),
                    StringComparer.OrdinalIgnoreCase
                );
            var user = values.GetValueOrDefault("Username", "");
            var (password, problem) = ("", "no password");
            if (values.TryGetValue("ClearTextPassword", out var clear))
            {
                (password, problem) = (Environment.ExpandEnvironmentVariables(clear), "");
            }
            else if (values.TryGetValue("Password", out var encrypted))
            {
                (password, problem) = NuGetPasswords.Decrypt(encrypted);
            }

            if (problem.Length is 0 && password.Contains('%', StringComparison.Ordinal))
            {
                problem = "its ClearTextPassword names an environment variable that is not set";
            }

            credentials[XmlConvert.DecodeName(source.Name.LocalName)] = new(user, password, problem);
        }
    }

    /// <summary>A folder's nuget.config in its on-disk spelling (NuGet looks for these three names).</summary>
    private static string? Find(DirectoryInfo folder)
    {
        try
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var present = folder
                .EnumerateFiles("*.config")
                .Where(file => FileNames.Contains(file.Name, comparer))
                .ToDictionary(file => file.Name, file => file.FullName, StringComparer.Ordinal);
            return FileNames
                .Select(name => present.FirstOrDefault(pair => comparer.Equals(pair.Key, name)).Value)
                .FirstOrDefault(path => path is not null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// The enabled sources NuGet may take <paramref name="packageId"/> from: every enabled source without package
    /// source mapping; with it, the sources of the most specific matching pattern (the id itself, then the longest
    /// prefix ending in *), none when no pattern matches.
    /// </summary>
    public IReadOnlyList<NuGetSource> SourcesFor(string packageId)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        var enabled = Sources.Where(source => source.Enabled).ToArray();
        if (Mapping.Count is 0)
        {
            return enabled;
        }

        var best = -1;
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, patterns) in Mapping)
        {
            foreach (var pattern in patterns)
            {
                var rank = Rank(pattern, packageId);
                if (rank > best)
                {
                    best = rank;
                    keys.Clear();
                }

                if (rank >= 0 && rank == best)
                {
                    keys.Add(key);
                }
            }
        }

        return enabled.Where(source => keys.Contains(source.Key)).ToArray();
    }

    /// <summary>How specifically a pattern matches an id: -1 not at all, an exact id above any prefix.</summary>
    private static int Rank(string pattern, string id)
    {
        if (pattern.EndsWith('*', StringComparison.Ordinal))
        {
            return id.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase) ? pattern.Length - 1 : -1;
        }

        return pattern.Equals(id, StringComparison.OrdinalIgnoreCase) ? int.MaxValue : -1;
    }

    private static string Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.Ordinal))?.Value.Trim()
        ?? "";
}
