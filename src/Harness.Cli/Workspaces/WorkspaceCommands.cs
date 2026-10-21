using Spectre.Console.Cli;

namespace Harness.Cli.Workspaces;

internal class WorkspaceSettings : InspectSettings
{
    [CommandArgument(0, "<workspace>")] public string Name { get; init; } = "";
}
internal sealed class WorkspaceCreateSettings : WorkspaceSettings
{
    [CommandOption("--discover")] public string Discover { get; init; } = "";
    [CommandOption("--title")] public string Title { get; init; } = "";
}
internal sealed class WorkspaceDiscoverSettings : InspectSettings
{
    [CommandArgument(0, "<directory>")] public string Directory { get; init; } = "";
}
internal class RenameSettings : InspectSettings
{
    [CommandOption("--to")] public string To { get; init; } = "";
    [CommandOption("--title")] public string Title { get; init; } = "";
    [CommandOption("--clear-title")] public bool ClearTitle { get; init; }
    [CommandOption("--reset-name")] public bool ResetName { get; init; }
}
internal sealed class WorkspaceRenameSettings : RenameSettings
{
    [CommandArgument(0, "<workspace>")] public string Name { get; init; } = "";
}
internal sealed class ProjectForgetSettings : InspectSettings
{
    [CommandArgument(0, "<id>")] public string Id { get; init; } = "";
}
internal sealed class WorkspaceListCommand(WorkspaceCommandRunner runner) : AsyncCommand<InspectSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, InspectSettings settings, CancellationToken cancellationToken) => runner.RunAsync("workspace", "list", settings, cancellationToken); }
internal sealed class WorkspaceCreateCommand(WorkspaceCommandRunner runner) : AsyncCommand<WorkspaceCreateSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, WorkspaceCreateSettings settings, CancellationToken cancellationToken) => runner.RunAsync("workspace", "create", settings, cancellationToken); }
internal sealed class WorkspaceDiscoverCommand(WorkspaceCommandRunner runner) : AsyncCommand<WorkspaceDiscoverSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, WorkspaceDiscoverSettings settings, CancellationToken cancellationToken) => runner.RunAsync("workspace", "discover", settings, cancellationToken); }
internal sealed class WorkspaceShowCommand(WorkspaceCommandRunner runner) : AsyncCommand<WorkspaceSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken) => runner.RunAsync("workspace", "show", settings, cancellationToken); }
internal sealed class WorkspaceJoinCommand(WorkspaceCommandRunner runner) : AsyncCommand<WorkspaceSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken) => runner.RunAsync("workspace", "join", settings, cancellationToken); }
internal sealed class WorkspaceLeaveCommand(WorkspaceCommandRunner runner) : AsyncCommand<WorkspaceSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken) => runner.RunAsync("workspace", "leave", settings, cancellationToken); }
internal sealed class WorkspaceDeleteCommand(WorkspaceCommandRunner runner) : AsyncCommand<WorkspaceSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, WorkspaceSettings settings, CancellationToken cancellationToken) => runner.RunAsync("workspace", "delete", settings, cancellationToken); }
internal sealed class WorkspaceRenameCommand(WorkspaceCommandRunner runner) : AsyncCommand<WorkspaceRenameSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, WorkspaceRenameSettings settings, CancellationToken cancellationToken) => runner.RunAsync("workspace", "rename", settings, cancellationToken); }
internal sealed class ProjectsListCommand(WorkspaceCommandRunner runner) : AsyncCommand<InspectSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, InspectSettings settings, CancellationToken cancellationToken) => runner.RunAsync("projects", "list", settings, cancellationToken); }
internal sealed class ProjectsForgetCommand(WorkspaceCommandRunner runner) : AsyncCommand<ProjectForgetSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, ProjectForgetSettings settings, CancellationToken cancellationToken) => runner.RunAsync("projects", "forget", settings, cancellationToken); }
internal sealed class ProjectsRenameCommand(WorkspaceCommandRunner runner) : AsyncCommand<RenameSettings>
{ public override Task<int> ExecuteAsync(CommandContext context, RenameSettings settings, CancellationToken cancellationToken) => runner.RunAsync("projects", "rename", settings, cancellationToken); }
