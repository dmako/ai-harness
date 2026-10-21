using System.Globalization;
using System.Text.RegularExpressions;
using Harness.Core.Tasks;
using Spectre.Console.Cli;

namespace Harness.Cli.Tasks;

internal sealed class TaskClaimSettings : TaskIdSettings
{
    [CommandOption("--lease")] public string Lease { get; init; } = "2h0m0s";
}
internal sealed class TaskCompleteSettings : TaskIdSettings
{
    [CommandOption("--summary")] public string Summary { get; init; } = "";
    [CommandOption("--commit")] public string Commit { get; init; } = "";
    [CommandOption("--acc")] public string[] Acceptance { get; init; } = [];
    [CommandOption("--acc-all")] public string All { get; init; } = "";
    [CommandOption("--check")] public string[] Checks { get; init; } = [];
}
internal sealed class TaskProposalListSettings : InspectSettings
{
    [CommandOption("--status")] public string Status { get; init; } = "pending";
}
internal sealed class TaskApproveSettings : TaskIdSettings
{
    [CommandOption("--prefix")] public string Prefix { get; init; } = "";
}
internal sealed class TaskClaimCommand(TaskCommandRunner runner) : AsyncCommand<TaskClaimSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskClaimSettings settings, CancellationToken cancellationToken) => runner.RunAsync("claim", settings, cancellationToken); }
internal sealed class TaskReleaseCommand(TaskCommandRunner runner) : AsyncCommand<TaskReasonSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskReasonSettings settings, CancellationToken cancellationToken) => runner.RunAsync("release", settings, cancellationToken); }
internal sealed class TaskCompleteCommand(TaskCommandRunner runner) : AsyncCommand<TaskCompleteSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskCompleteSettings settings, CancellationToken cancellationToken) => runner.RunAsync("complete", settings, cancellationToken); }
internal sealed class TaskProposalsCommand(TaskCommandRunner runner) : AsyncCommand<TaskProposalListSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskProposalListSettings settings, CancellationToken cancellationToken) => runner.RunAsync("proposals", settings, cancellationToken); }
internal sealed class TaskApproveCommand(TaskCommandRunner runner) : AsyncCommand<TaskApproveSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskApproveSettings settings, CancellationToken cancellationToken) => runner.RunAsync("approve", settings, cancellationToken); }
internal sealed class TaskRejectCommand(TaskCommandRunner runner) : AsyncCommand<TaskReadySettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskReadySettings settings, CancellationToken cancellationToken) => runner.RunAsync("reject", settings, cancellationToken); }

internal static partial class TaskCliInput
{
    public static TaskCompletion Complete(TaskCompleteSettings input, TaskRecord task)
    {
        List<TaskReport> reports = [];
        if (input.All.Length is not 0)
        {
            var all = input.All.Split(':', 2);
            reports.AddRange(task.Criteria.Select(c => new TaskReport(c.Index, all[0], all.Length > 1 ? all[1] : "")));
        }
        foreach (var value in input.Acceptance)
        {
            var parts = value.Split('=', 2);
            if (parts.Length is not 2)
                throw new TaskUsageException($"--acc {TaskWorkflow.Quote(value)}: want N=pass[:evidence]");
            if (!long.TryParse(parts[0].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
                throw new TaskUsageException($"--acc {TaskWorkflow.Quote(value)}: {TaskWorkflow.Quote(parts[0])} is not a criterion number");
            var result = parts[1].Split(':', 2);
            reports.Add(new(index, result[0].Trim(), result.Length > 1 ? result[1].Trim() : ""));
        }
        List<TaskCheck> checks = [];
        foreach (var value in input.Checks)
        {
            var parts = value.Split('=', 2);
            if (parts.Length is not 2)
                throw new TaskUsageException($"--check {TaskWorkflow.Quote(value)}: want command=pass");
            checks.Add(new(parts[0], parts[1]));
        }
        return new(input.Summary, input.Commit, reports, checks.Count is 0 ? null : checks);
    }

    public static TimeSpan Lease(string value)
    {
        if (value is "0" or "+0" or "-0")
            return TimeSpan.Zero;
        var negative = value.StartsWith('-');
        var input = negative || value.StartsWith('+') ? value[1..] : value;
        var matches = DurationPattern().Matches(input);
        if (matches.Count is 0 || string.Concat(matches.Select(m => m.Value)) != input)
            throw new TaskUsageException($"invalid value {TaskWorkflow.Quote(value)} for flag -lease: parse error");
        var ticks = matches.Sum(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * (m.Groups[2].Value switch
        { "h" => TimeSpan.TicksPerHour, "m" => TimeSpan.TicksPerMinute, "s" => TimeSpan.TicksPerSecond, "ms" => TimeSpan.TicksPerMillisecond, "us" or "µs" or "μs" => 10, _ => 0.01 }));
        if (!double.IsFinite(ticks) || ticks > long.MaxValue / 100d)
            throw new TaskUsageException($"invalid value {TaskWorkflow.Quote(value)} for flag -lease: parse error");
        return TimeSpan.FromTicks((long)(negative ? -Math.Ceiling(ticks) : Math.Ceiling(ticks)));
    }
    [GeneratedRegex(@"([0-9]+(?:\.[0-9]*)?|\.[0-9]+)(ns|us|µs|μs|ms|s|m|h)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DurationPattern();
}
