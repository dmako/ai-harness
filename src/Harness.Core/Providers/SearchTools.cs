using System.Collections.Concurrent;

namespace Harness.Core.Providers;

/// <summary>Where rg and fd were found: empty when missing; fd may be Debian's and Ubuntu's fdfind.</summary>
public sealed record SearchToolset(string Rg = "", string Fd = "")
{
    public bool HasRg => Rg.Length > 0;
    public bool HasFd => Fd.Length > 0;

    /// <summary>Text search needs rg; file search takes fd, or 'rg --files' without it.</summary>
    public bool Any => HasRg || HasFd;
}

/// <summary>Whether the built-in search serves: active, off, superseded (by an rg-fd entry) or missing (no tools).</summary>
public sealed record SearchStatus(string State, string Detail, string? Fix = null, SearchToolset? Tools = null)
{
    public bool Active => State is SearchTools.Active;
}

/// <summary>
/// The built-in text and file search over ripgrep and fd: their detection on PATH (cached per process), the
/// [providers.search] settings and whether an explicit [providers.rg-fd] entry keeps it off.
/// </summary>
public static class SearchTools
{
    public const string ProviderName = "search";
    public const string ProviderType = "builtin";
    public const string Active = "active";
    public const string Off = "off";
    public const string Superseded = "superseded";
    public const string Missing = "missing";

    private static readonly ConcurrentDictionary<(string Rg, string Fd), SearchToolset> Cache = new();

    /// <summary>The tools for these overrides (empty: look on PATH), found once per process.</summary>
    public static SearchToolset Detected(ProviderConfiguration? search = null) =>
        Cache.GetOrAdd((search?.Rg ?? "", search?.Fd ?? ""), key => Detect(key.Rg, key.Fd));

    /// <summary>
    /// Looks for rg and fd (fdfind on Debian and Ubuntu) on PATH, or takes the configured path or name. Windows
    /// tries the executable extensions; a configured tool that is not found counts as missing.
    /// </summary>
    public static SearchToolset Detect(
        string rg = "",
        string fd = "",
        string? path = null,
        bool? windows = null,
        Func<string, bool>? exists = null
    )
    {
        var onWindows = windows ?? OperatingSystem.IsWindows();
        var directories = (path ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(onWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var isFile = exists ?? File.Exists;
        string[] extensions = onWindows ? [".exe", ".cmd", ".bat", ".com"] : [""];
        string Find(string configured, params string[] names) =>
            (configured.Length > 0 ? [configured] : names)
                .Select(name => Locate(name, directories, onWindows ? '\\' : '/', extensions, isFile))
                .FirstOrDefault(found => found is not null)
            ?? "";

        return new(Find(rg, "rg"), Find(fd, "fd", "fdfind"));
    }

    private static string? Locate(
        string name,
        string[] directories,
        char separator,
        string[] extensions,
        Func<string, bool> exists
    )
    {
        var explicitPath =
            name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal);
        var candidates = explicitPath
            ? [name]
            : directories.Select(directory => directory.TrimEnd('/', '\\') + separator + name);
        return candidates
            .SelectMany(candidate =>
                Path.GetExtension(candidate).Length > 0
                    ? [candidate]
                    : extensions.Select(extension => candidate + extension)
            )
            .FirstOrDefault(exists);
    }

    /// <summary>
    /// The built-in search's state for this configuration: off when [providers.search] enabled = false; kept off
    /// by an enabled [providers.rg-fd] entry unless [providers.search] says enabled = true; missing without rg and
    /// fd; else active.
    /// </summary>
    public static SearchStatus Status(
        IReadOnlyDictionary<string, ProviderConfiguration> providers,
        SearchToolset? tools = null,
        string? platform = null
    )
    {
        ArgumentNullException.ThrowIfNull(providers);
        var search = providers.GetValueOrDefault(ProviderName);
        if (search is { Enabled: false })
        {
            return new(Off, "built-in text and file search turned off ([providers.search] enabled = false)");
        }

        tools ??= Detected(search);
        var rgFd = providers
            .Where(pair => pair.Value.IsEnabled() && (pair.Value.Pack is "rg-fd" || pair.Key is "rg-fd"))
            .Select(pair => pair.Key)
            .FirstOrDefault();
        if (rgFd is not null && search is not { Enabled: true })
        {
            return new(
                Superseded,
                $"[providers.{rgFd}] (rg-fd-mcp) serves text and file search; the built-in search stays off",
                tools.HasRg && tools.HasFd
                    ? $"rg and fd are installed: remove [providers.{rgFd}] to use the built-in search instead"
                    : null,
                tools
            );
        }

        if (!tools.Any)
        {
            return new(
                Missing,
                "neither rg nor fd found on PATH: no built-in text and file search",
                InstallHint(platform),
                tools
            );
        }

        var detail = (tools.HasRg, tools.HasFd) switch
        {
            (true, true) => $"rg {tools.Rg}, fd {tools.Fd}",
            (true, false) => $"rg {tools.Rg}; fd not found: file search through 'rg --files'",
            _ => $"fd {tools.Fd}; rg not found: no text search",
        };
        return new(Active, detail, tools.HasRg && tools.HasFd ? null : InstallHint(platform), tools);
    }

    /// <summary>How to install ripgrep and fd on this operating system (windows, linux, macos).</summary>
    public static string InstallHint(string? platform = null) =>
        (platform ?? Platform()) switch
        {
            "windows" =>
                "install ripgrep and fd: winget install BurntSushi.ripgrep.MSVC; winget install sharkdp.fd "
                + "(or scoop install ripgrep fd), then open a new terminal",
            "macos" => "install ripgrep and fd: brew install ripgrep fd",
            _ => "install ripgrep and fd: sudo apt install ripgrep fd-find (Debian and Ubuntu name fd fdfind), "
                + "sudo dnf install ripgrep fd-find, sudo pacman -S ripgrep fd, "
                + "or your distribution's ripgrep and fd packages",
        };

    private static string Platform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        return OperatingSystem.IsMacOS() ? "macos" : "linux";
    }
}
