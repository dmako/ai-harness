using Harness.Core.Instructions;
using Harness.Infrastructure.Clients;

namespace Harness.Infrastructure.Completion;

public sealed class CompletionManager(string home)
{
    public static IReadOnlyList<string> Shells { get; } = ["zsh", "bash", "fish", "pwsh"];
    public static MarkedBlock Section { get; } =
        new("# >>> harness completion >>>", "# <<< harness completion <<<", "harness completion");

    public static string Detect()
    {
        var shell = Path.GetFileName(Environment.GetEnvironmentVariable("SHELL") ?? "");
        if (Shells.Contains(shell, StringComparer.Ordinal))
        {
            return shell;
        }
        return OperatingSystem.IsWindows() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SHELL"))
            ? "pwsh"
            : "";
    }

    public async Task<string> TargetAsync(string shell, CancellationToken token)
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(home, ".config");
        return shell switch
        {
            "zsh" => Path.Combine(
                Environment.GetEnvironmentVariable("ZDOTDIR") is { Length: > 0 } z ? z : home,
                ".zshrc"
            ),
            "bash" => Path.Combine(home, ".bashrc"),
            "fish" => Path.Combine(config, "fish", "completions", "harness.fish"),
            "pwsh" => await PowerShellProfileAsync(config, token),
            _ => throw new ArgumentException("unknown shell (want zsh, bash, fish or pwsh)", nameof(shell)),
        };
    }

    private async Task<string> PowerShellProfileAsync(string config, CancellationToken token)
    {
        var redirected =
            OperatingSystem.IsWindows()
            && !string.Equals(
                Path.GetFullPath(home),
                Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                StringComparison.OrdinalIgnoreCase
            );
        if (!redirected)
        {
            foreach (var executable in new[] { "pwsh", "powershell" })
            {
                var profile = await SetupQuery.QueryAsync(
                    executable,
                    ["-NoProfile", "-NonInteractive", "-Command", "$PROFILE.CurrentUserAllHosts"],
                    token
                );
                if (Path.IsPathRooted(profile))
                {
                    return profile;
                }
            }
        }
        return OperatingSystem.IsWindows()
            ? Path.Combine(home, "Documents", "PowerShell", "profile.ps1")
            : Path.Combine(config, "powershell", "profile.ps1");
    }

    public async Task<IReadOnlyList<string>> OfferedAsync(CancellationToken token)
    {
        List<string> result = OperatingSystem.IsWindows() ? ["pwsh"] : ["zsh", "bash", "fish"];
        foreach (var shell in OperatingSystem.IsWindows() ? new[] { "zsh", "bash", "fish" } : ["pwsh"])
        {
            var path = await TargetAsync(shell, token);
            if (File.Exists(path) || FindExecutable(shell))
            {
                result.Add(shell);
            }
        }
        return result;
    }

    private static bool FindExecutable(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Any(directory => File.Exists(Path.Combine(directory, name + (OperatingSystem.IsWindows() ? ".exe" : ""))));

    public static bool Installed(string shell, string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        var text = FileEditor.Read(path);
        return shell is "fish"
            ? text.Contains(CompletionScripts.For("fish").TrimEnd(), StringComparison.Ordinal)
            : Section.Find(text).Start >= 0;
    }

    public static string Block(string shell) =>
        Section.Begin
        + "\n"
        + (
            shell is "pwsh"
                ? "if (Get-Command harness -ErrorAction SilentlyContinue) { harness completion pwsh | Out-String | Invoke-Expression }"
                : "command -v harness >/dev/null 2>&1 && eval \"$(harness completion " + shell + ")\""
        )
        + "\n"
        + Section.End;

    public static FileChange Set(string shell, string path, bool install, bool dryRun = false)
    {
        if (!Shells.Contains(shell, StringComparer.Ordinal))
        {
            throw new ArgumentException("unknown shell", nameof(shell));
        }
        return FileEditor.Apply(
            path,
            (source, exists) =>
            {
                if (shell is "fish")
                {
                    var script = CompletionScripts.For(shell).TrimEnd();
                    if (install)
                    {
                        return exists ? new(source, "unchanged") : new(script + "\n", "add");
                    }
                    var lines = source.Split('\n');
                    var kept = lines.Where(line => line.Trim() != script).ToArray();
                    return kept.Length != lines.Length ? new(string.Join('\n', kept), "remove") : new(source, "absent");
                }
                if (!install)
                {
                    return exists ? Section.Cut(source) : new(source, "absent");
                }
                return Section.Find(source).Start >= 0 ? new(source, "unchanged") : Section.Put(source, Block(shell));
            },
            dryRun,
            backup: true
        );
    }
}
