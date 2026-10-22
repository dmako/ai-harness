using System.Text.Json;
using Harness.Cli.Memory;
using Harness.Cli.Setup;
using Harness.Infrastructure.Clients;
using Harness.Infrastructure.Completion;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Hooks;

namespace Harness.Cli.Hooks;

internal sealed class HookCommandRunner(TextWriter output, CliErrorOutput error, SetupTerminal terminal)
{
    private static readonly EditWords GitWords = new("harness hook block", "installed", "not installed");
    private static readonly EditWords ClientWords = new(
        "harness hook",
        "installed, unchanged",
        "not installed, nothing to remove"
    );
    private readonly ClientPaths _paths = ClientPaths.Current();

    public Task<int> RunAsync(string verb, HookOptions settings, CancellationToken token) =>
        GuardAsync(
            "hooks " + verb,
            async () =>
            {
                var defaultKind = verb is "status" ? "all" : "git";
                var kinds = Kinds(settings.Kind.Length is 0 ? defaultKind : settings.Kind);
                var code = 0;
                foreach (var kind in kinds)
                {
                    var result = await GuardAsync(
                        "hooks " + verb + " (" + kind + ")",
                        () => kind is "git" ? GitAsync(verb, settings, token) : ClientAsync(verb, kind, settings, token)
                    );
                    if (result is not 0)
                    {
                        code = result;
                    }
                }
                if (settings.DryRun)
                {
                    await output.WriteLineAsync("\nDry run: nothing was written.");
                }
                return code;
            }
        );

    private static string[] Kinds(string kind)
    {
        if (kind is "all")
        {
            return ["git", "claude-code", "codex"];
        }
        var kinds = kind.Split(',').Select(k => k.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (kinds.Any(k => k is not ("git" or "claude-code" or "codex")))
        {
            throw new ArgumentException("unknown hook kind (want git, claude-code, codex or all)");
        }
        return kinds;
    }

    private async Task<int> GitAsync(string verb, HookOptions settings, CancellationToken token)
    {
        var repository = await GitRepository.DiscoverAsync(settings.Project, token);
        var hooks = await GitHooks.ForAsync(repository, token);
        await output.WriteLineAsync("Git sync hooks (" + hooks.DirectoryPath + "):");
        var changes = verb is "status"
            ? hooks.Status()
            : GitHooks
                .Names.Select(name =>
                    hooks.Set(
                        name,
                        verb is "install",
                        settings.Command.Length is 0 ? "harness" : settings.Command,
                        settings.DryRun
                    )
                )
                .ToArray();
        foreach (var change in changes)
        {
            await EditOutput.PrintAsync(output, "  " + change.Path, GitWords, change, settings.DryRun);
        }
        return changes.Any(c => c.Action is "refuse") ? 1 : 0;
    }

    private async Task<int> ClientAsync(string verb, string client, HookOptions settings, CancellationToken token)
    {
        if (settings.Scope is "all" && verb is "install")
        {
            throw new ArgumentException("choose one --scope (user, project or local)");
        }
        var scope = verb is "status" ? "all" : settings.Scope;
        var root = "";
        try
        {
            root = (await GitRepository.DiscoverAsync(settings.Project, token)).Root;
        }
        catch (NotRepositoryException) when (settings.Scope is "user")
        {
            scope = "user";
        }
        var targets = ClientHookCleanup.Targets(_paths, client, scope, root);
        var command = "";
        if (verb is "install")
        {
            var spec = SetupCommandRunner.Executable(settings.Command);
            // Client hooks accept one executable. The apphost is available beside framework builds.
            var apphost = OperatingSystem.IsWindows() ? "harness.exe" : "harness";
            var executable =
                spec.Args.Count > 1 ? Path.Combine(Path.GetDirectoryName(spec.Args[0])!, apphost) : spec.Command;
            if (!ClientConfiguration.Executable(executable))
            {
                throw new IOException("hook executable is unavailable; pass --command");
            }
            command = ClientHookCleanup.Command(executable, client);
        }
        await output.WriteLineAsync(client + " tool-event hook:");
        var failed = false;
        foreach (var target in targets)
        {
            try
            {
                var label = "  " + target.Path + " (" + target.Scope + ")";
                if (verb is "status")
                {
                    var installed = ClientHookCleanup.Installed(target.Path);
                    await output.WriteLineAsync(
                        label + (installed.Length is 0 ? ": not installed" : ": installed (" + installed + ")")
                    );
                    if (ClientHookCleanup.CommandProblem(installed) is { Length: > 0 } problem)
                    {
                        await output.WriteLineAsync("    " + problem);
                    }
                    if (
                        client is "claude-code"
                        && installed.Length > 0
                        && ClientHookCleanup.Installed(target.Path, "session").Length is 0
                    )
                    {
                        await output.WriteLineAsync("    SessionStart hook missing; run 'harness hooks install' again");
                    }
                    continue;
                }
                var change = verb is "install"
                    ? ClientHookCleanup.Install(target, command, settings.DryRun)
                    : ClientHookCleanup.Remove(target, settings.DryRun);
                await EditOutput.PrintAsync(output, label, ClientWords, change, settings.DryRun);
                if (client is "codex" && verb is "install" && change.Writes && !settings.DryRun)
                {
                    await output.WriteLineAsync("    " + ClientHookCleanup.CodexTrustNote);
                }
            }
            catch (Exception exception) when (EditError(exception))
            {
                failed = true;
                await output.WriteLineAsync(target.Path + ": error: " + exception.Message);
            }
        }
        return failed ? 1 : 0;
    }

    public Task<int> CompletionAsync(string verb, CompletionSettings settings, CancellationToken token) =>
        GuardAsync(
            "completion",
            async () =>
            {
                var manager = new CompletionManager(_paths.Home);
                if (verb is "status")
                {
                    foreach (var shell in await manager.OfferedAsync(token))
                    {
                        var path = await manager.TargetAsync(shell, token);
                        var state = CompletionManager.Installed(shell, path) ? "installed" : "not installed";
                        await output.WriteLineAsync(
                            $"{shell, -5} {state, -14} {path}"
                                + (shell == CompletionManager.Detect() ? " (your shell)" : "")
                        );
                    }
                    return 0;
                }
                var chosen = settings.Shell.Length > 0 ? settings.Shell : CompletionManager.Detect();
                if (chosen.Length is 0)
                {
                    throw new ArgumentException("cannot tell the shell from $SHELL; pass --shell zsh|bash|fish|pwsh");
                }
                var target = await manager.TargetAsync(chosen, token);
                var change = CompletionManager.Set(chosen, target, verb is "install");
                var message = (change.Writes, chosen, verb) switch
                {
                    (false, _, "install") => "already installed",
                    (false, _, _) => "not installed",
                    (true, "fish", "install") => "wrote " + target,
                    (true, "fish", _) => "removed " + target,
                    (true, _, "install") => "added a block to " + target,
                    _ => "removed the block from " + target,
                };
                await output.WriteLineAsync(chosen + ": " + message);
                return 0;
            }
        );

    public Task<int> ChooseAsync(HookOptions settings, CancellationToken token) =>
        GuardAsync(
            "hooks",
            async () =>
                await terminal.RunAsync(
                    new SetupWizard("harness hooks", [await HookStepAsync(settings.Project, token, settings.Scope)]),
                    output,
                    token
                )
        );

    public async Task<SetupStep> HookStepAsync(string directory, CancellationToken token, string scope = "user")
    {
        if (scope is not ("user" or "project" or "local"))
        {
            throw new ArgumentException("choose one --scope (user, project or local)");
        }
        List<SetupItem> items = [];
        string? root = null;
        try
        {
            var repository = await GitRepository.DiscoverAsync(directory, token);
            root = repository.Root;
            var hooks = await GitHooks.ForAsync(repository, token);
            items.Add(
                new(
                    "Git sync",
                    hooks.DirectoryPath,
                    hooks.Status().All(c => c.Action is "unchanged"),
                    install =>
                        RunAsync(install ? "install" : "uninstall", new() { Kind = "git", Project = directory }, token)
                )
            );
        }
        catch (NotRepositoryException)
        { /* Offer client user hooks outside Git. */
        }
        foreach (var client in new[] { "claude-code", "codex" })
        {
            foreach (
                var target in ClientHookCleanup
                    .Targets(_paths, client, "all", root ?? "")
                    .Where(t => t.Scope == scope && (root is not null || t.Scope is "user"))
            )
            {
                var selected = target;
                items.Add(
                    new(
                        client + " (" + target.Scope + ")",
                        target.Path,
                        ClientHookCleanup.Installed(target.Path).Length > 0,
                        install =>
                            RunAsync(
                                install ? "install" : "uninstall",
                                new()
                                {
                                    Kind = selected.Client,
                                    Scope = selected.Scope,
                                    Project = directory,
                                },
                                token
                            )
                    )
                );
            }
        }
        return new("Hooks", "Git sync and client tool events. " + ClientHookCleanup.CodexTrustNote, items);
    }

    public async Task<SetupStep> CompletionStepAsync(CancellationToken token)
    {
        var manager = new CompletionManager(_paths.Home);
        List<SetupItem> items = [];
        foreach (var shell in await manager.OfferedAsync(token))
        {
            var path = await manager.TargetAsync(shell, token);
            var selected = shell;
            items.Add(
                new(
                    shell,
                    path,
                    CompletionManager.Installed(shell, path),
                    install => CompletionAsync(install ? "install" : "uninstall", new() { Shell = selected }, token)
                )
            );
        }
        return new("Shell completion", "complete harness commands and flags in your shell", items);
    }

    private async Task<int> GuardAsync(string verb, Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (ArgumentException exception)
        {
            await error.Writer.WriteLineAsync("harness " + verb + ": " + exception.Message);
            return 2;
        }
        catch (Exception exception) when (EditError(exception))
        {
            await error.Writer.WriteLineAsync("harness " + verb + ": " + exception.Message);
            return 1;
        }
    }

    private static bool EditError(Exception exception) =>
        exception
            is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or JsonException
                or InvalidOperationException;
}
