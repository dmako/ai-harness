namespace Harness.Cli.Hooks;

internal sealed record HookOptions
{
    public string Project { get; init; } = ".";
    public string Scope { get; init; } = "user";
    public string Kind { get; init; } = "";
    public string Command { get; init; } = "";
    public bool DryRun { get; init; }
}

internal class HookChooseSettings
{
    public string Project { get; init; } = ".";
    public string Scope { get; init; } = "user";

    public virtual HookOptions Options() => new() { Project = Project, Scope = Scope };
}

internal class HookStatusSettings : HookChooseSettings
{
    public string Kind { get; init; } = "";

    public override HookOptions Options() => base.Options() with { Kind = Kind };
}

internal class HookChangeSettings : HookStatusSettings
{
    public bool DryRun { get; init; }

    public override HookOptions Options() => base.Options() with { DryRun = DryRun };
}

internal sealed class HookInstallSettings : HookChangeSettings
{
    public string Command { get; init; } = "";

    public override HookOptions Options() => base.Options() with { Command = Command };
}

internal sealed class HookEventSettings
{
    public string Client { get; init; } = "claude-code";
}

internal sealed class CompletionSettings
{
    public string Shell { get; init; } = "";
}

internal sealed class HooksChooseCommand(HookCommandRunner runner)
{
    public Task<int> ExecuteAsync(HookChooseSettings settings, CancellationToken cancellationToken) =>
        runner.ChooseAsync(settings.Options(), cancellationToken);
}

internal sealed class HooksInstallCommand(HookCommandRunner runner)
{
    public Task<int> ExecuteAsync(HookInstallSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("install", settings.Options(), cancellationToken);
}

internal sealed class HooksUninstallCommand(HookCommandRunner runner)
{
    public Task<int> ExecuteAsync(HookChangeSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("uninstall", settings.Options(), cancellationToken);
}

internal sealed class HooksStatusCommand(HookCommandRunner runner)
{
    public Task<int> ExecuteAsync(HookStatusSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("status", settings.Options(), cancellationToken);
}

internal sealed class CompletionStatusCommand(HookCommandRunner runner)
{
    public Task<int> ExecuteAsync(EmptySettings _, CancellationToken cancellationToken) =>
        runner.CompletionAsync("status", new(), cancellationToken);
}

internal sealed class CompletionInstallCommand(HookCommandRunner runner)
{
    public Task<int> ExecuteAsync(CompletionSettings settings, CancellationToken cancellationToken) =>
        runner.CompletionAsync("install", settings, cancellationToken);
}

internal sealed class CompletionUninstallCommand(HookCommandRunner runner)
{
    public Task<int> ExecuteAsync(CompletionSettings settings, CancellationToken cancellationToken) =>
        runner.CompletionAsync("uninstall", settings, cancellationToken);
}
