using System.ComponentModel;
using Spectre.Console.Cli;

namespace Harness.Cli.Memory;

internal sealed record CliErrorOutput(TextWriter Writer);

internal class MemoryIdSettings : InspectSettings
{
    [CommandArgument(0, "<id>")]
    public string Id { get; init; } = "";
}

internal sealed class MemoryRetireSettings : MemoryIdSettings
{
    [CommandOption("--reason")]
    [Description("Why it no longer applies (required)")]
    public string Reason { get; init; } = "";
}

internal sealed class MemoryPromoteSettings : MemoryIdSettings
{
    [CommandOption("--to")]
    public string To { get; init; } = "";

    [CommandOption("--force")]
    public bool Force { get; init; }
}

internal sealed class ProposalListSettings : InspectSettings
{
    [CommandOption("--status")]
    public string Status { get; init; } = "pending";
}

internal class ProposalNoteSettings : MemoryIdSettings
{
    [CommandOption("--note")]
    public string Note { get; init; } = "";
}

internal sealed class ProposalApproveSettings : ProposalNoteSettings
{
    [CommandOption("--content")]
    public string Content { get; init; } = "";

    [CommandOption("--supersede")]
    public string[] Supersede { get; init; } = [];

    [CommandOption("--keep-both")]
    public bool KeepBoth { get; init; }
}

internal sealed class MemoryShowCommand(MemoryCommandRunner runner) : AsyncCommand<MemoryIdSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        MemoryIdSettings settings,
        CancellationToken cancellationToken
    ) => runner.MemoryAsync("show", settings, cancellationToken);
}

internal sealed class MemoryRetireCommand(MemoryCommandRunner runner) : AsyncCommand<MemoryRetireSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        MemoryRetireSettings settings,
        CancellationToken cancellationToken
    ) => runner.MemoryAsync("retire", settings, cancellationToken);
}

internal sealed class MemoryPromoteCommand(MemoryCommandRunner runner) : AsyncCommand<MemoryPromoteSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        MemoryPromoteSettings settings,
        CancellationToken cancellationToken
    ) => runner.MemoryAsync("promote", settings, cancellationToken);
}

internal sealed class ProposalsListCommand(MemoryCommandRunner runner) : AsyncCommand<ProposalListSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        ProposalListSettings settings,
        CancellationToken cancellationToken
    ) => runner.ProposalAsync("list", settings, cancellationToken);
}

internal sealed class ProposalsShowCommand(MemoryCommandRunner runner) : AsyncCommand<MemoryIdSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        MemoryIdSettings settings,
        CancellationToken cancellationToken
    ) => runner.ProposalAsync("show", settings, cancellationToken);
}

internal sealed class ProposalsApproveCommand(MemoryCommandRunner runner) : AsyncCommand<ProposalApproveSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        ProposalApproveSettings settings,
        CancellationToken cancellationToken
    ) => runner.ProposalAsync("approve", settings, cancellationToken);
}

internal sealed class ProposalsRejectCommand(MemoryCommandRunner runner) : AsyncCommand<ProposalNoteSettings>
{
    public override Task<int> ExecuteAsync(
        CommandContext context,
        ProposalNoteSettings settings,
        CancellationToken cancellationToken
    ) => runner.ProposalAsync("reject", settings, cancellationToken);
}
