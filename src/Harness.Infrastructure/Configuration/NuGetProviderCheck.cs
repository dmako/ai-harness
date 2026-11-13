using Harness.Core.Providers;

namespace Harness.Infrastructure.Configuration;

/// <summary>
/// What doctor says about the nuget provider (NugetMcpServer, usually started with dnx) against the repository's
/// NuGet configuration, and the project-level override that fixes what it finds: <see cref="Keys"/> are the
/// [providers.&lt;name&gt;] keys to set in .harness/config.toml, as TOML values.
/// </summary>
public sealed record NuGetProviderCheck(
    string Provider,
    string Level,
    string Detail,
    IReadOnlyList<string> Problems,
    IReadOnlyList<(string Key, string Value)> Keys
)
{
    /// <summary>The check's name in doctor and Health.</summary>
    public const string Name = "nuget-config";

    /// <summary>The pack and table name of the provider.</summary>
    public const string Pack = "nuget";

    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";

    /// <summary>The command harness runs for the user: it writes <see cref="Override"/> into the project's config.</summary>
    public const string FixCommand = "harness doctor --fix " + Name;

    public string Table => "providers." + Provider;

    /// <summary>The project-level override, as TOML.</summary>
    public string Override => $"[{Table}]\n" + string.Concat(Keys.Select(pair => $"{pair.Key} = {pair.Value}\n"));

    public string? Fix =>
        Keys.Count is 0
            ? null
            : $"run '{FixCommand}' to set this in .harness/config.toml (undo: remove the table), or add it "
                + "yourself:\n"
                + Override.TrimEnd('\n');

    /// <summary>
    /// The configured nuget provider: the table named nuget or one using the nuget pack, when it is enabled; null
    /// when there is none.
    /// </summary>
    public static (string Name, ProviderConfiguration Provider)? Find(
        IReadOnlyDictionary<string, ProviderConfiguration> providers
    )
    {
        ArgumentNullException.ThrowIfNull(providers);
        foreach (var (name, provider) in providers.OrderBy(pair => pair.Key != Pack).ThenBy(pair => pair.Key))
        {
            if ((name == Pack || provider.Pack == Pack) && provider.IsEnabled())
            {
                return (name, provider);
            }
        }

        return null;
    }

    /// <summary>
    /// Checks the provider against the NuGet configuration of the project root (where dnx and the server start with
    /// dir = "project"). <paramref name="packDefaults"/> is the pack's own table, so the override repeats only what
    /// the global table changed: a project table replaces the global one whole.
    /// </summary>
    public static NuGetProviderCheck Evaluate(
        string name,
        ProviderConfiguration provider,
        NuGetSettings settings,
        ProviderConfiguration? packDefaults = null
    )
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(settings);
        var line = CommandLine.Parse(provider);
        List<string> problems = [.. settings.Errors.Select(error => "cannot read " + error)];
        var args = provider.Args.ToList();
        var fixArgs = false;
        if (line.Dnx && line.Tool.Length > 0 && !line.Has("--source") && !line.Has("--configfile"))
        {
            var reachable = settings.SourcesFor(line.Tool);
            var added = settings.Mapping.Count is 0 && line.Values("--add-source").Any(IsNuGetOrg);
            if (!added && !reachable.Any(source => source.NuGetOrg))
            {
                problems.Add($"dnx would not find {line.Tool} on nuget.org: " + WhyNotNuGetOrg(settings, line.Tool));
                fixArgs = true;
            }

            if (line.Has("--add-source") && settings.Mapping.Count > 0)
            {
                problems.Add(
                    "dnx refuses --add-source while package source mapping is on (in "
                        + string.Join(", ", settings.MappingFiles)
                        + ")"
                );
                fixArgs = true;
            }
        }

        var feeds = settings
            .Sources.Where(source => source.Enabled && !source.NuGetOrg && settings.Files.Contains(source.File))
            .ToArray();
        var dir = false;
        if (feeds.Length > 0 && provider.Dir != "project" && !line.ServerSources)
        {
            problems.Add(
                "NugetMcpServer reads the NuGet configuration of the folder it starts in; without dir = \"project\" "
                    + "it may not see "
                    + string.Join(", ", feeds.Select(source => $"{source.Key} ({source.File})"))
            );
            dir = true;
        }

        if (problems.Count is 0)
        {
            return new(name, "ok", Fine(settings, provider, line), [], []);
        }

        if (fixArgs)
        {
            args = FixedArgs(args, settings.Mapping.Count > 0);
        }

        List<(string, string)> keys = [];
        if (fixArgs || dir)
        {
            keys.Add(("command", Toml(provider.Command)));
            keys.Add(("args", Toml(args)));
            keys.Add(("dir", Toml("project")));
            keys.AddRange(Carried(provider, packDefaults));
        }

        return new(
            name,
            "warn",
            $"[providers.{name}] with {Describe(settings)}: {string.Join("; ", problems)}",
            problems,
            keys
        );
    }

    private static bool IsNuGetOrg(string source) => new NuGetSource("", source, true, "").NuGetOrg;

    private static string WhyNotNuGetOrg(NuGetSettings settings, string tool)
    {
        if (!settings.Sources.Any(source => source.NuGetOrg))
        {
            return settings.ClearedIn.Length > 0
                ? $"<clear/> in {settings.ClearedIn} drops it and no file adds it back"
                : "no NuGet configuration adds it";
        }

        if (!settings.Sources.Any(source => source.NuGetOrg && source.Enabled))
        {
            return "it is disabled (disabledPackageSources)";
        }

        return $"package source mapping (in {string.Join(", ", settings.MappingFiles)}) maps {tool} to "
            + (
                settings.SourcesFor(tool) is { Count: > 0 } other
                    ? string.Join(", ", other.Select(source => source.Key))
                    : "no source"
            );
    }

    /// <summary>dnx's options with --source nuget.org (before the server's '--' arguments), --add-source dropped.</summary>
    private static List<string> FixedArgs(List<string> args, bool mapping)
    {
        var end = args.IndexOf("--");
        var own = end < 0 ? args : args[..end];
        List<string> rest = end < 0 ? [] : args[end..];
        List<string> kept = [];
        var index = 0;
        while (index < own.Count)
        {
            var arg = own[index++];
            if (mapping && arg is "--add-source")
            {
                index++; // and its value
            }
            else if (!mapping || !arg.StartsWith("--add-source=", StringComparison.Ordinal))
            {
                kept.Add(arg);
            }
        }

        if (!kept.Contains("--source") && !kept.Any(arg => arg.StartsWith("--source=", StringComparison.Ordinal)))
        {
            kept.AddRange(["--source", NuGetOrg]);
        }

        return [.. kept, .. rest];
    }

    /// <summary>What the global table set beyond the pack, which a project table would otherwise drop.</summary>
    private static IEnumerable<(string, string)> Carried(ProviderConfiguration provider, ProviderConfiguration? pack)
    {
        if (pack is null)
        {
            yield break;
        }

        if (provider.TimeoutMs != pack.TimeoutMs && provider.TimeoutMs > 0)
        {
            yield return ("timeout_ms", provider.TimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!provider.Languages.SequenceEqual(pack.Languages))
        {
            yield return ("languages", Toml(provider.Languages));
        }

        if (provider.Only.Length > 0)
        {
            yield return ("only", Toml(provider.Only));
        }

        if (provider.Context is false)
        {
            yield return ("context", "false");
        }
    }

    private static string Describe(NuGetSettings settings)
    {
        var user = settings.UserFile.Length > 0 ? $"user-level {settings.UserFile}" : "";
        if (settings.Files.Count is 0)
        {
            return "no nuget.config in the repository or above" + (user.Length > 0 ? $" ({user})" : "");
        }

        return string.Join(", ", settings.Files) + (user.Length > 0 ? " and " + user : "");
    }

    private static string Fine(NuGetSettings settings, ProviderConfiguration provider, CommandLine line) =>
        Describe(settings)
        + (line.Dnx && line.Has("--source") ? "; dnx fetches the server with --source" : "")
        + (provider.Dir == "project" ? "; the server reads the project's NuGet configuration" : "");

    internal static string Toml(string value) =>
        "\""
        + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
        + "\"";

    internal static string Toml(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Toml)) + "]";

    /// <summary>The provider's command line: whether dnx starts it, the tool package, dnx's own options.</summary>
    private sealed record CommandLine(bool Dnx, string Tool, IReadOnlyList<string> Options, bool ServerSources)
    {
        /// <summary>dnx's options that take a value (dotnet dnx --help, SDK 11.0.100).</summary>
        private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
        {
            "--version",
            "--configfile",
            "--source",
            "--add-source",
            "-v",
            "--verbosity",
        };

        public bool Has(string option) =>
            Options.Any(arg => arg == option || arg.StartsWith(option + "=", StringComparison.Ordinal));

        /// <summary>The values given to an option ('--add-source x' or '--add-source=x').</summary>
        public IEnumerable<string> Values(string option)
        {
            for (var index = 0; index < Options.Count; index++)
            {
                if (Options[index] == option && index + 1 < Options.Count)
                {
                    yield return Options[index + 1];
                }
                else if (Options[index].StartsWith(option + "=", StringComparison.Ordinal))
                {
                    yield return Options[index][(option.Length + 1)..];
                }
            }
        }

        public static CommandLine Parse(ProviderConfiguration provider)
        {
            var command = Path.GetFileNameWithoutExtension(provider.Command);
            IEnumerable<string> args = provider.Args;
            var dnx = command.Equals("dnx", StringComparison.OrdinalIgnoreCase);
            if (command.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && provider.Args is ["dnx", ..])
            {
                dnx = true;
                args = provider.Args.Skip(1);
            }

            var list = args.ToList();
            var end = list.IndexOf("--");
            var own = end < 0 ? list : list[..end];
            List<string> server = end < 0 ? [] : list[(end + 1)..];
            var serverSources = server.Any(arg =>
                arg is "--source" or "-s" or "--sources" or "--nuget-config" or "--nugetconfig"
                || arg.StartsWith("--source=", StringComparison.Ordinal)
                || arg.StartsWith("--sources=", StringComparison.Ordinal)
                || arg.StartsWith("--nuget-config=", StringComparison.Ordinal)
                || arg.StartsWith("--nugetconfig=", StringComparison.Ordinal)
            );
            if (!dnx)
            {
                // the server started directly: its own options are its sources
                return new(false, "", [], serverSources || own.Any(arg => arg is "--source" or "--nuget-config"));
            }

            var tool = "";
            var index = 0;
            while (index < own.Count && tool.Length is 0)
            {
                var arg = own[index++];
                if (ValueOptions.Contains(arg))
                {
                    index++; // its value
                }
                else if (!arg.StartsWith('-', StringComparison.Ordinal))
                {
                    tool = arg;
                }
            }

            var at = tool.IndexOf('@', StringComparison.Ordinal);
            return new(true, at < 0 ? tool : tool[..at], own, serverSources);
        }
    }
}
