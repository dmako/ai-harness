using Spectre.Console.Cli;

namespace Harness.Cli.Tasks;

internal sealed class TaskListSettings : InspectSettings
{
    [CommandOption("--status")] public string[] Status { get; init; } = [];
    [CommandOption("--milestone")] public string Milestone { get; init; } = "";
    [CommandOption("--all")] public bool All { get; init; }
}
internal sealed class TaskNextSettings : InspectSettings
{
    [CommandOption("--milestone")] public string Milestone { get; init; } = "";
}
internal class TaskIdSettings : InspectSettings
{
    [CommandArgument(0, "<id>")] public string Id { get; init; } = "";
}
internal sealed class TaskShowSettings : TaskIdSettings
{
    [CommandOption("--events")] public bool Events { get; init; }
}
internal sealed class TaskReasonSettings : TaskIdSettings
{
    [CommandOption("--reason")] public string Reason { get; init; } = "";
}
internal sealed class TaskReadySettings : TaskIdSettings
{
    [CommandOption("--note")] public string Note { get; init; } = "";
}
internal sealed class TaskFileSettings : InspectSettings
{
    [CommandArgument(0, "[file]")] public string File { get; init; } = "";
    [CommandOption("--dry-run")] public bool DryRun { get; init; }
}
internal sealed class TaskAddSettings : InspectSettings
{
    [CommandOption("--id")] public string Id { get; init; } = "";
    [CommandOption("--prefix")] public string Prefix { get; init; } = "";
    [CommandOption("--title")] public string Title { get; init; } = "";
    [CommandOption("--goal")] public string Goal { get; init; } = "";
    [CommandOption("--type")] public string Type { get; init; } = "feature";
    [CommandOption("--priority")] public string Priority { get; init; } = "medium";
    [CommandOption("--milestone")] public string Milestone { get; init; } = "";
    [CommandOption("--depends")] public string[] Depends { get; init; } = [];
    [CommandOption("--accept")] public string[] Accept { get; init; } = [];
    [CommandOption("--scope")] public string[] Scope { get; init; } = [];
}
internal sealed class TaskListCommand(TaskCommandRunner runner) : AsyncCommand<TaskListSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskListSettings settings, CancellationToken cancellationToken) => runner.RunAsync("list", settings, cancellationToken); }
internal sealed class TaskMilestonesCommand(TaskCommandRunner runner) : AsyncCommand<InspectSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, InspectSettings settings, CancellationToken cancellationToken) => runner.RunAsync("milestones", settings, cancellationToken); }
internal sealed class TaskShowCommand(TaskCommandRunner runner) : AsyncCommand<TaskShowSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskShowSettings settings, CancellationToken cancellationToken) => runner.RunAsync("show", settings, cancellationToken); }
internal sealed class TaskNextCommand(TaskCommandRunner runner) : AsyncCommand<TaskNextSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskNextSettings settings, CancellationToken cancellationToken) => runner.RunAsync("next", settings, cancellationToken); }
internal sealed class TaskImportCommand(TaskCommandRunner runner) : AsyncCommand<TaskFileSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskFileSettings settings, CancellationToken cancellationToken) => runner.RunAsync("import", settings, cancellationToken); }
internal sealed class TaskExportCommand(TaskCommandRunner runner) : AsyncCommand<TaskFileSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskFileSettings settings, CancellationToken cancellationToken) => runner.RunAsync("export", settings, cancellationToken); }
internal sealed class TaskAddCommand(TaskCommandRunner runner) : AsyncCommand<TaskAddSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskAddSettings settings, CancellationToken cancellationToken) => runner.RunAsync("add", settings, cancellationToken); }
internal sealed class TaskBlockCommand(TaskCommandRunner runner) : AsyncCommand<TaskReasonSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskReasonSettings settings, CancellationToken cancellationToken) => runner.RunAsync("block", settings, cancellationToken); }
internal sealed class TaskDeferCommand(TaskCommandRunner runner) : AsyncCommand<TaskReasonSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskReasonSettings settings, CancellationToken cancellationToken) => runner.RunAsync("defer", settings, cancellationToken); }
internal sealed class TaskReadyCommand(TaskCommandRunner runner) : AsyncCommand<TaskReadySettings>
{ public override Task<int> ExecuteAsync(CommandContext context, TaskReadySettings settings, CancellationToken cancellationToken) => runner.RunAsync("ready", settings, cancellationToken); }

public sealed class TaskUsageException : Exception
{
    public TaskUsageException() { }
    public TaskUsageException(string message) : base(message) { }
    public TaskUsageException(string message, Exception innerException) : base(message, innerException) { }
}
