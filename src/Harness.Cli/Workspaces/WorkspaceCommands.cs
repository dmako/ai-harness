namespace Harness.Cli.Workspaces;

internal class WorkspaceSettings : InspectSettings
{
    public string Name { get; init; } = "";
}

internal sealed class WorkspaceCreateSettings : WorkspaceSettings
{
    public string Discover { get; init; } = "";
    public string Title { get; init; } = "";
}

internal sealed class WorkspaceDiscoverSettings : InspectSettings
{
    public string Directory { get; init; } = "";
}

internal class RenameSettings : InspectSettings
{
    public string To { get; init; } = "";
    public string Title { get; init; } = "";
    public bool ClearTitle { get; init; }
    public bool ResetName { get; init; }
}

internal sealed class WorkspaceRenameSettings : RenameSettings
{
    public string Name { get; init; } = "";
}

internal sealed class ProjectForgetSettings : InspectSettings
{
    public string Id { get; init; } = "";
}

internal sealed class WorkspaceListCommand(WorkspaceCommandRunner runner)
{
    public Task<int> ExecuteAsync(InspectSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("workspace", "list", settings, cancellationToken);
}

internal sealed class WorkspaceCreateCommand(WorkspaceCommandRunner runner)
{
    public Task<int> ExecuteAsync(WorkspaceCreateSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("workspace", "create", settings, cancellationToken);
}

internal sealed class WorkspaceDiscoverCommand(WorkspaceCommandRunner runner)
{
    public Task<int> ExecuteAsync(WorkspaceDiscoverSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("workspace", "discover", settings, cancellationToken);
}

internal sealed class WorkspaceShowCommand(WorkspaceCommandRunner runner)
{
    public Task<int> ExecuteAsync(WorkspaceSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("workspace", "show", settings, cancellationToken);
}

internal sealed class WorkspaceJoinCommand(WorkspaceCommandRunner runner)
{
    public Task<int> ExecuteAsync(WorkspaceSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("workspace", "join", settings, cancellationToken);
}

internal sealed class WorkspaceLeaveCommand(WorkspaceCommandRunner runner)
{
    public Task<int> ExecuteAsync(WorkspaceSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("workspace", "leave", settings, cancellationToken);
}

internal sealed class WorkspaceDeleteCommand(WorkspaceCommandRunner runner)
{
    public Task<int> ExecuteAsync(WorkspaceSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("workspace", "delete", settings, cancellationToken);
}

internal sealed class WorkspaceRenameCommand(WorkspaceCommandRunner runner)
{
    public Task<int> ExecuteAsync(WorkspaceRenameSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("workspace", "rename", settings, cancellationToken);
}

internal sealed class ProjectsListCommand(WorkspaceCommandRunner runner)
{
    public Task<int> ExecuteAsync(InspectSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("projects", "list", settings, cancellationToken);
}

internal sealed class ProjectsForgetCommand(WorkspaceCommandRunner runner)
{
    public Task<int> ExecuteAsync(ProjectForgetSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("projects", "forget", settings, cancellationToken);
}

internal sealed class ProjectsRenameCommand(WorkspaceCommandRunner runner)
{
    public Task<int> ExecuteAsync(RenameSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("projects", "rename", settings, cancellationToken);
}
