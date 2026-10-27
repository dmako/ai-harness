namespace Harness.Cli.Tasks;

internal sealed class TaskListSettings : InspectSettings
{
    public string[] Status { get; init; } = [];
    public string Blueprint { get; init; } = "";
    public bool All { get; init; }
}

internal sealed class TaskNextSettings : InspectSettings
{
    public string Blueprint { get; init; } = "";
}

internal class TaskIdSettings : InspectSettings
{
    public string Id { get; init; } = "";
}

internal sealed class TaskShowSettings : TaskIdSettings
{
    public bool Events { get; init; }
}

internal sealed class TaskReasonSettings : TaskIdSettings
{
    public string Reason { get; init; } = "";
}

internal sealed class TaskReadySettings : TaskIdSettings
{
    public string Note { get; init; } = "";
}

internal sealed class TaskFileSettings : InspectSettings
{
    public string File { get; init; } = "";
    public bool DryRun { get; init; }
}

internal sealed class TaskAddSettings : InspectSettings
{
    public string Id { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string Title { get; init; } = "";
    public string Goal { get; init; } = "";
    public string Type { get; init; } = "feature";
    public string Priority { get; init; } = "medium";
    public string Blueprint { get; init; } = "";
    public string[] Depends { get; init; } = [];
    public string[] Accept { get; init; } = [];
    public string[] Scope { get; init; } = [];
}

internal sealed class TaskListCommand(TaskCommandRunner runner)
{
    public Task<int> ExecuteAsync(TaskListSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("list", settings, cancellationToken);
}

internal sealed class TaskBlueprintsCommand(TaskCommandRunner runner)
{
    public Task<int> ExecuteAsync(InspectSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("blueprints", settings, cancellationToken);
}

internal sealed class TaskShowCommand(TaskCommandRunner runner)
{
    public Task<int> ExecuteAsync(TaskShowSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("show", settings, cancellationToken);
}

internal sealed class TaskNextCommand(TaskCommandRunner runner)
{
    public Task<int> ExecuteAsync(TaskNextSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("next", settings, cancellationToken);
}

internal sealed class TaskImportCommand(TaskCommandRunner runner)
{
    public Task<int> ExecuteAsync(TaskFileSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("import", settings, cancellationToken);
}

internal sealed class TaskExportCommand(TaskCommandRunner runner)
{
    public Task<int> ExecuteAsync(TaskFileSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("export", settings, cancellationToken);
}

internal sealed class TaskAddCommand(TaskCommandRunner runner)
{
    public Task<int> ExecuteAsync(TaskAddSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("add", settings, cancellationToken);
}

internal sealed class TaskBlockCommand(TaskCommandRunner runner)
{
    public Task<int> ExecuteAsync(TaskReasonSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("block", settings, cancellationToken);
}

internal sealed class TaskDeferCommand(TaskCommandRunner runner)
{
    public Task<int> ExecuteAsync(TaskReasonSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("defer", settings, cancellationToken);
}

internal sealed class TaskReadyCommand(TaskCommandRunner runner)
{
    public Task<int> ExecuteAsync(TaskReadySettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("ready", settings, cancellationToken);
}

public sealed class TaskUsageException : Exception
{
    public TaskUsageException() { }

    public TaskUsageException(string message)
        : base(message) { }

    public TaskUsageException(string message, Exception innerException)
        : base(message, innerException) { }
}
