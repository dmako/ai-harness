namespace Harness.Infrastructure.Configuration;

public static class ConfigurationPaths
{
    public static string GlobalPath() => GlobalPath((OperatingSystem.IsWindows(), OperatingSystem.IsMacOS()) switch
    {
        (true, _) => "windows",
        (_, true) => "darwin",
        _ => "linux",
    }, Environment.GetEnvironmentVariable);

    public static string GlobalPath(string platform, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment("HARNESS_CONFIG_DIR") is { Length: > 0 } overridden)
        {
            return Path.Combine(overridden, "config.toml");
        }

        var home = environment("HOME");
        var xdgDirectory = environment("XDG_CONFIG_HOME");
        var defaultLinuxDirectory = string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".config");
        var linuxDirectory = string.IsNullOrEmpty(xdgDirectory) ? defaultLinuxDirectory : xdgDirectory;
        var directory = platform switch
        {
            "windows" => environment("APPDATA"),
            "darwin" => string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Library", "Application Support"),
            _ => linuxDirectory,
        };
        return string.IsNullOrEmpty(directory)
            ? throw new InvalidOperationException("Cannot locate user config directory; set HARNESS_CONFIG_DIR.")
            : Path.Combine(directory, "harness", "config.toml");
    }

    public static string ProjectPath(string root) => Path.Combine(root, ".harness", "config.toml");
}
