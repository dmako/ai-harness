using Harness.Cli.Memory;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Instructions;
using Harness.Infrastructure.Clients;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Projects;

namespace Harness.Cli.Setup;

internal sealed class SetupCommandRunner(ProjectLifecycle lifecycle, TextWriter output, CliErrorOutput error, SetupInvocation invocation, SetupTerminal terminal)
{
    private readonly ClientPaths _paths = ClientPaths.Current();
    private ClientConfiguration Clients => new(_paths);
    public Task<int> InstallAsync(InstallSettings settings, CancellationToken token) => GuardAsync("install", () => invocation.Bare ? WizardAsync("harness install", settings.Project, token) : InstallFlagsAsync(settings, token));
    public Task<int> UninstallAsync(UninstallSettings settings, CancellationToken token) => GuardAsync("uninstall", async () =>
    {
        if (settings.DeleteData && !settings.All)
            return await UsageAsync("uninstall", "--delete-data only goes with --all");
        if (settings.DeleteData && !invocation.ExplicitProject)
            return await UsageAsync("uninstall", "--delete-data needs an explicit --project <repository>");
        if (invocation.Bare)
            return await WizardAsync("harness uninstall", settings.Project, token);
        return settings.All ? await UninstallAllAsync(settings, token) : await EntriesAsync(settings, null, token);
    });
    public Task<int> InstructionsAsync(InstructionsSettings settings, CancellationToken token) => GuardAsync("instructions", async () =>
    {
        var root = (await GitRepository.DiscoverAsync(settings.Project, token)).Root;
        var changes = settings.Remove ? ProjectInstructions.RemoveProject(root, settings.DryRun) : ProjectInstructions.Project(root, Clients.Detect(Clients.Get("codex"), "").Configured, settings.DryRun);
        foreach (var change in changes)
        {
            var words = change.Action is not "remove" && change.After.Contains(AgentGuidance.ImportBlock, StringComparison.Ordinal) ? EditWords.Instructions with { Thing = "import of AGENTS.md" } : EditWords.Instructions;
            await EditOutput.PrintAsync(output, Path.GetRelativePath(root, change.Path), words, change, settings.DryRun);
        }
        if (settings.DryRun)
            await DryRunAsync();
        return 0;
    });
    private async Task<int> InstallFlagsAsync(InstallSettings settings, CancellationToken token)
    {
        if (settings.Scope is "all")
            return await UsageAsync("install", "choose one --scope (user, project or local)");
        // Validate the selected clients/scopes before inspecting an executable.
        Clients.Targets(settings.Client, settings.Scope, "");
        var spec = Executable(settings.Command);
        return await EntriesAsync(settings, spec, token);
    }
    private async Task<int> EntriesAsync(ClientSettings settings, ServerSpec? spec, CancellationToken token)
    {
        var root = await RootAsync(settings.Project, settings.Scope, token);
        var targets = Clients.Targets(settings.Client, settings.Scope, root);
        var failed = false;
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            var label = target.Client + " (" + target.Scope + ") " + target.Path;
            try
            {
                var wanted = spec is not null && target.Scope is "project" ? spec with { Command = HomeRelative(spec.Command) } : spec;
                var change = wanted is null ? Clients.Uninstall(target, settings.DryRun) : Clients.Install(target, wanted, settings.DryRun);
                await EditOutput.PrintAsync(output, label, EditWords.Server, change, settings.DryRun);
            }
            catch (Exception exception) when (EditError(exception))
            { failed = true; await output.WriteLineAsync(label + ": error: " + exception.Message); }
        }
        if (settings.DryRun)
            await DryRunAsync();
        else if (spec is not null && !failed)
            await output.WriteLineAsync("\nRestart Claude Code / Codex sessions to pick up changes.");
        return failed ? 1 : 0;
    }
    private static async Task<string> RootAsync(string directory, string scope, CancellationToken token)
    {
        try
        { return (await GitRepository.DiscoverAsync(directory, token)).Root; }
        catch (NotRepositoryException) when (scope is "user") { return Path.GetFullPath(directory); }
    }
    internal static ServerSpec Executable(string explicitPath)
    {
        var command = explicitPath;
        IReadOnlyList<string> args = ["mcp"];
        if (command.Length is 0)
        {
            command = Environment.ProcessPath ?? throw new IOException("cannot locate the running executable");
            var entryPoint = Environment.GetCommandLineArgs()[0];
            if (Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(entryPoint).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                args = [Path.GetFullPath(entryPoint), "mcp"];
            else
                command = FileEditor.Resolve(command);
            var relative = Path.GetRelativePath(Path.GetTempPath(), command);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                throw new IOException("this binary (" + command + ") is a temporary build; install it first or pass --command");
        }
        command = Path.GetFullPath(command);
        if (!ClientConfiguration.Executable(command))
            throw new IOException(command + " is not an executable file");
        return new(command, args);
    }
    internal string HomeRelative(string command)
    {
        var prefix = Path.TrimEndingDirectorySeparator(_paths.Home) + Path.DirectorySeparatorChar;
        return command.StartsWith(prefix, StringComparison.Ordinal) ? "${HOME}/" + command[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/') : command;
    }
    private async Task<int> GuardAsync(string verb, Func<Task<int>> action)
    {
        try
        { return await action(); }
        catch (ArgumentException exception) when (exception is not System.Text.DecoderFallbackException) { return await UsageAsync(verb, exception.Message); }
        catch (Exception exception) when (EditError(exception))
        { await error.Writer.WriteLineAsync("harness " + verb + ": " + exception.Message); return 1; }
    }
    private async Task<int> UsageAsync(string verb, string message)
    { await error.Writer.WriteLineAsync("harness " + verb + ": " + message); return 2; }
    private Task DryRunAsync() => output.WriteLineAsync("\nDry run: nothing was written.");
    private static bool EditError(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or Tomlyn.TomlException or InvalidOperationException or System.Text.DecoderFallbackException;

    private async Task<int> UninstallAllAsync(UninstallSettings settings, CancellationToken token)
    {
        GitRepository? repository = null;
        try
        { repository = await GitRepository.DiscoverAsync(settings.Project, token); }
        catch (NotRepositoryException) { /* User configuration can still be removed outside a repository. */ }
        await output.WriteLineAsync("== MCP server entries");
        var code = await EntriesAsync(new ClientSettings { Client = "all", Scope = repository is null ? "user" : "all", Project = settings.Project, DryRun = settings.DryRun }, null, token);
        await output.WriteLineAsync("\n== Hooks");
        if (await ApplyRemovalsAsync(SetupCleanup.ClientHooks(_paths, repository?.Root, settings.DryRun), new("harness hook", "installed, unchanged", "not installed, nothing to remove"), settings.DryRun) is not 0)
            code = 1;
        if (repository is not null)
        {
            if (await ApplyRemovalsAsync(await SetupCleanup.GitHooksAsync(repository, settings.DryRun, token), new("harness hook block", "installed", "not installed"), settings.DryRun) is not 0)
                code = 1;
            await output.WriteLineAsync("\n== Agent instructions");
            var result = await InstructionsAsync(new() { Project = repository.Root, Remove = true, DryRun = settings.DryRun }, token);
            if (result is not 0)
                code = result;
        }
        await output.WriteLineAsync("\n== Shell completion");
        if (await ApplyRemovalsAsync(await SetupCleanup.CompletionsAsync(_paths.Home, settings.DryRun, token), new("harness completion", "installed", "not installed"), settings.DryRun, onlyChanges: true) is not 0)
            code = 1;
        await output.WriteLineAsync("\n== Project data");
        if (repository is null)
            await output.WriteLineAsync("not in a Git repository: no project data here");
        else
        {
            var directory = Path.GetFullPath(Path.Combine(repository.StateRoot, ".harness"));
            if (!settings.DeleteData)
                await output.WriteLineAsync("kept " + directory + " (add --delete-data to remove it)");
            else if (settings.DryRun)
                await output.WriteLineAsync("would delete " + directory + " and forget the project");
            else
            {
                // Never follow a substituted state-directory link during recursive removal.
                if (FileEditor.Resolve(directory) != directory || Path.GetDirectoryName(directory) != Path.GetFullPath(repository.StateRoot))
                    throw new IOException("refusing to delete a redirected project data directory");
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                foreach (var project in lifecycle.Registry.Load()["projects"]!.AsArray().OfType<JsonObject>().Where(p => p["root"]?.GetValue<string>() == repository.StateRoot))
                    await lifecycle.Registry.ForgetAsync(project["id"]!.GetValue<string>(), token);
                await output.WriteLineAsync("deleted " + directory + " and forgot the project");
            }
        }
        await output.WriteLineAsync("\nThe binaries stay: delete harness (and harness-graphify) from your PATH to finish.");
        if (settings.DryRun)
            await DryRunAsync();
        return code;
    }
    private async Task<int> ApplyRemovalsAsync(IEnumerable<SetupCleanup.Removal> removals, EditWords words, bool dryRun, bool onlyChanges = false)
    {
        var code = 0;
        foreach (var removal in removals)
        {
            try
            {
                var change = removal.Apply();
                if (!onlyChanges || change.Writes)
                    await EditOutput.PrintAsync(output, change.Path, words, change, dryRun);
            }
            catch (Exception exception) when (EditError(exception))
            { code = 1; await output.WriteLineAsync(removal.Path + ": error: " + exception.Message); }
        }
        return code;
    }
    private async Task<int> WizardAsync(string title, string directory, CancellationToken token)
    {
        GitRepository? repository = null;
        try
        { repository = await GitRepository.DiscoverAsync(directory, token); }
        catch (NotRepositoryException) { /* User-scope setup remains available. */ }
        var root = repository?.Root ?? "";
        List<SetupStep> steps = [];
        List<SetupItem> mcp = [];
        foreach (var client in Clients.All)
        {
            var status = Clients.Detect(client, root);
            foreach (var scope in client.Scopes.Where(s => s is "user" || root.Length > 0))
            {
                var target = client.Targets(scope, root)[0];
                var selectedClient = client.Id;
                var selectedScope = scope;
                mcp.Add(new(client.Name + (client.Scopes.Count > 1 ? " (" + scope + ")" : ""), HomeRelative(target.Path), status.Entries.Any(e => e.Scope == scope), async install =>
                    install ? await InstallFlagsAsync(new() { Client = selectedClient, Scope = selectedScope, Project = directory }, token) : await EntriesAsync(new() { Client = selectedClient, Scope = selectedScope, Project = directory }, null, token)));
            }
        }
        steps.Add(new("MCP server", "the harness tools in your agents; Claude Code per scope (user: every project)", mcp));
        List<SetupItem> guidance = [];
        if (repository is not null)
        {
            var files = new[] { AgentGuidance.FileName, AgentGuidance.ClaudeFileName }.Where(f => File.Exists(Path.Combine(root, f))).ToArray();
            var installed = files.Any(f => FileEditor.Read(Path.Combine(root, f)).Contains(AgentGuidance.Marker, StringComparison.Ordinal));
            guidance.Add(new("Harness guidance", files.Length is 0 ? "AGENTS.md (new)" : string.Join(", ", files), installed, install => InstructionsAsync(new() { Project = root, Remove = !install }, token)));
        }
        steps.Add(new("Agent instructions", repository is null ? "not in a Git repository: run it inside one to add the guidance" : "a short harness section in AGENTS.md (or an existing CLAUDE.md)", guidance));
        return await terminal.RunAsync(new SetupWizard(title, steps), output, token);
    }
}
