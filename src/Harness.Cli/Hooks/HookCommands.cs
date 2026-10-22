using System.ComponentModel;
using Spectre.Console.Cli;

namespace Harness.Cli.Hooks;

internal sealed record HookOptions
{
    public string Project { get; init; } = ".";
    public string Scope { get; init; } = "user";
    public string Kind { get; init; } = "";
    public string Command { get; init; } = "";
    public bool DryRun { get; init; }
}

internal class HookChooseSettings : CommandSettings
{
    [CommandOption("--project")]
    [DefaultValue(".")]
    public string Project { get; init; } = ".";

    [CommandOption("--scope")]
    [DefaultValue("user")]
    public string Scope { get; init; } = "user";

    public virtual HookOptions Options() => new() { Project = Project, Scope = Scope };
}

internal class HookStatusSettings : HookChooseSettings
{
    [CommandOption("--kind")]
    public string Kind { get; init; } = "";

    public override HookOptions Options() => base.Options() with { Kind = Kind };
}

internal class HookChangeSettings : HookStatusSettings
{
    [CommandOption("--dry-run")]
    public bool DryRun { get; init; }

    public override HookOptions Options() => base.Options() with { DryRun = DryRun };
}

internal sealed class HookInstallSettings : HookChangeSettings
{
    [CommandOption("--command")]
    public string Command { get; init; } = "";

    public override HookOptions Options() => base.Options() with { Command = Command };
}

internal sealed class HookEventSettings : CommandSettings
{
    [CommandOption("--client")]
    [DefaultValue("claude-code")]
    public string Client { get; init; } = "claude-code";
}

internal sealed class CompletionSettings : CommandSettings
{
    [CommandOption("--shell")]
    public string Shell { get; init; } = "";
}

internal sealed class HooksChooseCommand(HookCommandRunner runner) : AsyncCommand<HookChooseSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        HookChooseSettings settings,
        CancellationToken cancellationToken
    ) => runner.ChooseAsync(settings.Options(), cancellationToken);
}

internal sealed class HooksInstallCommand(HookCommandRunner runner) : AsyncCommand<HookInstallSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        HookInstallSettings settings,
        CancellationToken cancellationToken
    ) => runner.RunAsync("install", settings.Options(), cancellationToken);
}

internal sealed class HooksUninstallCommand(HookCommandRunner runner) : AsyncCommand<HookChangeSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        HookChangeSettings settings,
        CancellationToken cancellationToken
    ) => runner.RunAsync("uninstall", settings.Options(), cancellationToken);
}

internal sealed class HooksStatusCommand(HookCommandRunner runner) : AsyncCommand<HookStatusSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        HookStatusSettings settings,
        CancellationToken cancellationToken
    ) => runner.RunAsync("status", settings.Options(), cancellationToken);
}

internal sealed class CompletionStatusCommand(HookCommandRunner runner) : AsyncCommand<CommandSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        CommandSettings settings,
        CancellationToken cancellationToken
    ) => runner.CompletionAsync("status", new(), cancellationToken);
}

internal sealed class CompletionInstallCommand(HookCommandRunner runner) : AsyncCommand<CompletionSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        CompletionSettings settings,
        CancellationToken cancellationToken
    ) => runner.CompletionAsync("install", settings, cancellationToken);
}

internal sealed class CompletionUninstallCommand(HookCommandRunner runner) : AsyncCommand<CompletionSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        CompletionSettings settings,
        CancellationToken cancellationToken
    ) => runner.CompletionAsync("uninstall", settings, cancellationToken);
}
