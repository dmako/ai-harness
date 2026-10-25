namespace Harness.Cli.Memory;

internal sealed record CliErrorOutput(TextWriter Writer);

internal class MemoryIdSettings : InspectSettings
{
    public string Id { get; init; } = "";
}

internal sealed class MemoryRetireSettings : MemoryIdSettings
{
    public string Reason { get; init; } = "";
}

internal sealed class MemoryPromoteSettings : MemoryIdSettings
{
    public string To { get; init; } = "";
    public bool Force { get; init; }
}

internal sealed class ProposalListSettings : InspectSettings
{
    public string Status { get; init; } = "pending";
}

internal class ProposalNoteSettings : MemoryIdSettings
{
    public string Note { get; init; } = "";
}

internal sealed class ProposalApproveSettings : ProposalNoteSettings
{
    public string Content { get; init; } = "";
    public string[] Supersede { get; init; } = [];
    public bool KeepBoth { get; init; }
}

internal sealed class MemoryShowCommand(MemoryCommandRunner runner)
{
    public Task<int> ExecuteAsync(MemoryIdSettings settings, CancellationToken cancellationToken) =>
        runner.MemoryAsync("show", settings, cancellationToken);
}

internal sealed class MemoryRetireCommand(MemoryCommandRunner runner)
{
    public Task<int> ExecuteAsync(MemoryRetireSettings settings, CancellationToken cancellationToken) =>
        runner.MemoryAsync("retire", settings, cancellationToken);
}

internal sealed class MemoryPromoteCommand(MemoryCommandRunner runner)
{
    public Task<int> ExecuteAsync(MemoryPromoteSettings settings, CancellationToken cancellationToken) =>
        runner.MemoryAsync("promote", settings, cancellationToken);
}

internal sealed class ProposalsListCommand(MemoryCommandRunner runner)
{
    public Task<int> ExecuteAsync(ProposalListSettings settings, CancellationToken cancellationToken) =>
        runner.ProposalAsync("list", settings, cancellationToken);
}

internal sealed class ProposalsShowCommand(MemoryCommandRunner runner)
{
    public Task<int> ExecuteAsync(MemoryIdSettings settings, CancellationToken cancellationToken) =>
        runner.ProposalAsync("show", settings, cancellationToken);
}

internal sealed class ProposalsApproveCommand(MemoryCommandRunner runner)
{
    public Task<int> ExecuteAsync(ProposalApproveSettings settings, CancellationToken cancellationToken) =>
        runner.ProposalAsync("approve", settings, cancellationToken);
}

internal sealed class ProposalsRejectCommand(MemoryCommandRunner runner)
{
    public Task<int> ExecuteAsync(ProposalNoteSettings settings, CancellationToken cancellationToken) =>
        runner.ProposalAsync("reject", settings, cancellationToken);
}
