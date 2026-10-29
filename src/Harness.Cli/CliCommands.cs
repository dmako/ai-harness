using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Cli;

internal static class CliCommands
{
    internal static RootCommand Create(IServiceProvider services, TextWriter output)
    {
        var root = new RootCommand("Shared project context, memory and tasks for coding agents");
        var groups = new Dictionary<string, Command>();
        Add(
            root,
            "version",
            "Print version and build information",
            CommandBindings.BindVersionSettings,
            (settings, token) =>
                Task.FromResult(
                    ActivatorUtilities.CreateInstance<Harness.Cli.VersionCommand>(services).Execute(settings, token)
                )
        );
        Add(
            root,
            "init",
            "Initialize project state",
            CommandBindings.BindInitSettings,
            (settings, token) =>
                ActivatorUtilities.CreateInstance<Harness.Cli.InitCommand>(services).ExecuteAsync(settings, token)
        );
        Add(
            root,
            "status",
            "Show project state without changing it",
            CommandBindings.BindStatusSettings,
            (settings, token) =>
                ActivatorUtilities.CreateInstance<Harness.Cli.StatusCommand>(services).ExecuteAsync(settings, token)
        );
        Add(
            root,
            "doctor",
            "Check project health without changing it",
            CommandBindings.BindDoctorSettings,
            (settings, token) =>
                ActivatorUtilities.CreateInstance<Harness.Cli.DoctorCommand>(services).ExecuteAsync(settings, token)
        );
        Add(
            root,
            "ui",
            "Open the local harness UI",
            CommandBindings.BindUiSettings,
            (settings, token) =>
                ActivatorUtilities.CreateInstance<Harness.Cli.Ui.UiCommand>(services).ExecuteAsync(settings, token)
        );
        Add(
            root,
            "mcp",
            "Serve MCP over standard input/output",
            CommandBindings.BindProjectSettings,
            (settings, token) =>
                ActivatorUtilities.CreateInstance<Harness.Cli.McpCommand>(services).ExecuteAsync(settings, token)
        );
        Add(
            root,
            "sync",
            "Refresh recorded project state (worktree, branch, HEAD)",
            CommandBindings.BindSyncSettings,
            (settings, token) =>
                ActivatorUtilities.CreateInstance<Harness.Cli.SyncCommand>(services).ExecuteAsync(settings, token)
        );
        Add(
            root,
            "install",
            null,
            CommandBindings.BindInstallSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Setup.InstallCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            root,
            "uninstall",
            null,
            CommandBindings.BindUninstallSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Setup.UninstallCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            root,
            "instructions",
            null,
            CommandBindings.BindInstructionsSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Setup.InstructionsCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "hooks"),
            "choose",
            "Choose hooks to install",
            CommandBindings.BindHookChooseSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Hooks.HooksChooseCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "hooks"),
            "install",
            "Install optional hooks",
            CommandBindings.BindHookInstallSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Hooks.HooksInstallCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "hooks"),
            "uninstall",
            "Remove harness hooks",
            CommandBindings.BindHookChangeSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Hooks.HooksUninstallCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "hooks"),
            "status",
            "Show installed hooks",
            CommandBindings.BindHookStatusSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Hooks.HooksStatusCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        // Started detached by 'harness sync' when [architecture.complexity.metrics] is enabled; not for people.
        var complexity = Group(root, groups, "complexity");
        complexity.Hidden = true;
        Add(
            complexity,
            "measure",
            "Measure one commit's .NET projects with Microsoft.CodeAnalysis.Metrics",
            CommandBindings.BindComplexityMeasureSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.ComplexityMeasureCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "completion"),
            "status",
            null,
            CommandBindings.BindCommandSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Hooks.CompletionStatusCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "completion"),
            "install",
            null,
            CommandBindings.BindCompletionSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Hooks.CompletionInstallCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "completion"),
            "uninstall",
            null,
            CommandBindings.BindCompletionSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Hooks.CompletionUninstallCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "telemetry"),
            "status",
            null,
            CommandBindings.BindExportSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Telemetry.ExportStatusCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "telemetry"),
            "enable",
            null,
            CommandBindings.BindExportSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Telemetry.ExportEnableCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "telemetry"),
            "disable",
            null,
            CommandBindings.BindExportSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Telemetry.ExportDisableCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "telemetry"),
            "preview",
            null,
            CommandBindings.BindExportSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Telemetry.ExportPreviewCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "telemetry"),
            "send",
            null,
            CommandBindings.BindExportSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Telemetry.ExportSendCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            root,
            "docs",
            "Read embedded documentation by section",
            CommandBindings.BindDocsSettings,
            (settings, token) =>
                ActivatorUtilities.CreateInstance<Harness.Cli.DocsCommand>(services).ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "providers sessions"),
            "list",
            null,
            CommandBindings.BindSessionsListSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.SessionsListCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "providers sessions"),
            "start",
            null,
            CommandBindings.BindProjectSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.SessionStartCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "providers sessions"),
            "restart",
            null,
            CommandBindings.BindProjectSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.SessionRestartCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "providers sessions"),
            "stop",
            null,
            CommandBindings.BindSessionStopSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.SessionStopCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            root,
            "metrics",
            null,
            CommandBindings.BindTelemetrySettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Telemetry.MetricsCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            root,
            "report",
            null,
            CommandBindings.BindReportSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Telemetry.ReportCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "context"),
            "stats",
            null,
            CommandBindings.BindTelemetrySettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Telemetry.ContextStatsCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "list",
            "List tasks",
            CommandBindings.BindTaskListSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskListCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "blueprints",
            null,
            CommandBindings.BindInspectSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskBlueprintsCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        // 'tasks blueprints' lists them and holds the commands that change them
        groups["tasks blueprints"] = Group(root, groups, "tasks").Subcommands.Single(c => c.Name is "blueprints");
        Add(
            Group(root, groups, "tasks blueprints"),
            "add",
            "Add a blueprint: the next free id and a placeholder name unless given",
            CommandBindings.BindBlueprintAddSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskBlueprintAddCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks blueprints"),
            "edit",
            "Change a blueprint's title, goal, exit criteria or notes; what is not given stays",
            CommandBindings.BindBlueprintEditSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskBlueprintEditCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks blueprints"),
            "remove",
            "Remove a blueprint; one with tasks needs --tasks move (with --to, or to none) or --tasks delete",
            CommandBindings.BindBlueprintRemoveSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskBlueprintRemoveCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "show",
            null,
            CommandBindings.BindTaskShowSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskShowCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "next",
            null,
            CommandBindings.BindTaskNextSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskNextCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "import",
            null,
            CommandBindings.BindTaskFileSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskImportCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "export",
            null,
            CommandBindings.BindTaskFileSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskExportCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "add",
            null,
            CommandBindings.BindTaskAddSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskAddCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "block",
            null,
            CommandBindings.BindTaskReasonSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskBlockCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "defer",
            null,
            CommandBindings.BindTaskReasonSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskDeferCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "ready",
            null,
            CommandBindings.BindTaskReadySettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskReadyCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "claim",
            null,
            CommandBindings.BindTaskClaimSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskClaimCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "release",
            null,
            CommandBindings.BindTaskReasonSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskReleaseCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "complete",
            null,
            CommandBindings.BindTaskCompleteSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskCompleteCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "proposals",
            null,
            CommandBindings.BindTaskProposalListSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskProposalsCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "approve",
            null,
            CommandBindings.BindTaskApproveSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskApproveCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "tasks"),
            "reject",
            null,
            CommandBindings.BindTaskReadySettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Tasks.TaskRejectCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "memory"),
            "show",
            "Show a memory with history and audit trail",
            CommandBindings.BindMemoryIdSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Memory.MemoryShowCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "memory"),
            "retire",
            "End a memory's validity without deleting its history",
            CommandBindings.BindMemoryRetireSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Memory.MemoryRetireCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "memory"),
            "promote",
            "Promote a project memory to workspace or user scope",
            CommandBindings.BindMemoryPromoteSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Memory.MemoryPromoteCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "proposals"),
            "list",
            "List memory proposals",
            CommandBindings.BindProposalListSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Memory.ProposalsListCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "proposals"),
            "show",
            "Show a proposal and look-alike memories",
            CommandBindings.BindMemoryIdSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Memory.ProposalsShowCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "proposals"),
            "approve",
            "Approve a proposal, optionally edited",
            CommandBindings.BindProposalApproveSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Memory.ProposalsApproveCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "proposals"),
            "reject",
            "Reject a proposal",
            CommandBindings.BindProposalNoteSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Memory.ProposalsRejectCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "workspace"),
            "list",
            null,
            CommandBindings.BindInspectSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Workspaces.WorkspaceListCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "workspace"),
            "create",
            null,
            CommandBindings.BindWorkspaceCreateSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Workspaces.WorkspaceCreateCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "workspace"),
            "discover",
            null,
            CommandBindings.BindWorkspaceDiscoverSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Workspaces.WorkspaceDiscoverCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "workspace"),
            "show",
            null,
            CommandBindings.BindWorkspaceSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Workspaces.WorkspaceShowCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "workspace"),
            "join",
            null,
            CommandBindings.BindWorkspaceSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Workspaces.WorkspaceJoinCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "workspace"),
            "leave",
            null,
            CommandBindings.BindWorkspaceSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Workspaces.WorkspaceLeaveCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "workspace"),
            "delete",
            null,
            CommandBindings.BindWorkspaceSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Workspaces.WorkspaceDeleteCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "workspace"),
            "rename",
            null,
            CommandBindings.BindWorkspaceRenameSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Workspaces.WorkspaceRenameCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "projects"),
            "list",
            null,
            CommandBindings.BindInspectSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Workspaces.ProjectsListCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "projects"),
            "forget",
            null,
            CommandBindings.BindProjectForgetSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Workspaces.ProjectsForgetCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        Add(
            Group(root, groups, "projects"),
            "rename",
            null,
            CommandBindings.BindRenameSettings,
            (settings, token) =>
                ActivatorUtilities
                    .CreateInstance<Harness.Cli.Workspaces.ProjectsRenameCommand>(services)
                    .ExecuteAsync(settings, token)
        );
        groups["hooks"].Aliases.Add("hook");
        groups["tasks"].Aliases.Add("task");
        groups["workspace"].Aliases.Add("workspaces");
        foreach (var shell in Harness.Infrastructure.Completion.CompletionManager.Shells)
        {
            var command = new Command(shell, "Print the " + shell + " completion script");
            command.SetAction(_ =>
            {
                output.Write(Harness.Infrastructure.Completion.CompletionScripts.For(shell));
                return 0;
            });
            groups["completion"].Subcommands.Add(command);
        }
        foreach (var name in new[] { "event", "session" })
        {
            var command = new Command(
                name,
                name is "event" ? "Record one client tool event" : "Tell the agent this repository uses harness"
            );
            command.Options.Add(new Option<string>("--client") { DefaultValueFactory = _ => "claude-code" });
            groups["hooks"].Subcommands.Add(command);
        }
        return root;
    }

    private static Command Group(RootCommand root, Dictionary<string, Command> groups, string path)
    {
        if (groups.TryGetValue(path, out var found))
        {
            return found;
        }
        var split = path.LastIndexOf(' ');
        var parent = split < 0 ? root : Group(root, groups, path[..split]);
        var command = new Command(path[(split + 1)..]);
        parent.Subcommands.Add(command);
        groups[path] = command;
        return command;
    }

    private static void Add<T>(
        Command parent,
        string name,
        string? description,
        Func<Command, Func<ParseResult, T>> bind,
        Func<T, CancellationToken, Task<int>> run
    )
    {
        var command = new Command(name, description);
        var read = bind(command);
        command.SetAction((result, token) => run(read(result), token));
        parent.Subcommands.Add(command);
    }
}
