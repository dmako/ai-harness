using Harness.Cli.Workspaces;
using Harness.Infrastructure.Projects;
using Harness.Cli.Memory;
using Harness.Cli.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using Spectre.Console;

namespace Harness.Cli;

public static class CliApplication
{
    public static Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default) =>
        RunConfiguredAsync(args, stdout, stderr, null, null, cancellationToken);

    internal static async Task<int> RunConfiguredAsync(string[] args, TextWriter stdout, TextWriter stderr,
        Action<HostApplicationBuilder>? configureHost, Action<IConfigurator>? configureCommands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        using var loggerProvider = new WriterLoggerProvider(TextWriter.Synchronized(stderr));
        builder.Logging.AddProvider(loggerProvider);
        builder.Services.AddSingleton(BuildInformation.Current());
        builder.Services.AddSingleton(services => new ProjectLifecycle(version: services.GetRequiredService<BuildInformation>().Version));
        builder.Services.AddSingleton(stdout);
        builder.Services.AddSingleton(new CliErrorOutput(stderr));
        builder.Services.AddSingleton<MemoryCommandRunner>();
        builder.Services.AddSingleton<TaskCommandRunner>();
        builder.Services.AddSingleton<WorkspaceCommandRunner>();
        configureHost?.Invoke(builder);
        await using var registrar = new HostTypeRegistrar(builder);
        var app = new CommandApp(registrar);
        app.Configure(configuration =>
        {
            configuration.SetApplicationName("harness");
            configuration.UseStrictParsing();
            configuration.PropagateExceptions();
            configuration.Settings.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(stdout),
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
            });
            configuration.AddCommand<VersionCommand>("version").WithDescription("Print version and build information");
            configuration.AddCommand<InitCommand>("init").WithDescription("Initialize project state");
            configuration.AddCommand<StatusCommand>("status").WithDescription("Show project state without changing it");
            configuration.AddCommand<DoctorCommand>("doctor").WithDescription("Check project health without changing it");
            configuration.AddCommand<McpCommand>("mcp").WithDescription("Serve MCP over standard input/output");
            configuration.AddCommand<SyncCommand>("sync").WithDescription("Refresh recorded project state (worktree, branch, HEAD)");
            configuration.AddBranch("tasks", tasks =>
            {
                tasks.AddCommand<TaskListCommand>("list");
                tasks.AddCommand<TaskMilestonesCommand>("milestones");
                tasks.AddCommand<TaskShowCommand>("show");
                tasks.AddCommand<TaskNextCommand>("next");
                tasks.AddCommand<TaskImportCommand>("import");
                tasks.AddCommand<TaskExportCommand>("export");
                tasks.AddCommand<TaskAddCommand>("add");
                tasks.AddCommand<TaskBlockCommand>("block");
                tasks.AddCommand<TaskDeferCommand>("defer");
                tasks.AddCommand<TaskReadyCommand>("ready");
                tasks.AddCommand<TaskClaimCommand>("claim");
                tasks.AddCommand<TaskReleaseCommand>("release");
                tasks.AddCommand<TaskCompleteCommand>("complete");
                tasks.AddCommand<TaskProposalsCommand>("proposals");
                tasks.AddCommand<TaskApproveCommand>("approve");
                tasks.AddCommand<TaskRejectCommand>("reject");
            }).WithAlias("task");
            configuration.AddBranch("memory", memory =>
            {
                memory.AddCommand<MemoryShowCommand>("show").WithDescription("Show a memory with history and audit trail");
                memory.AddCommand<MemoryRetireCommand>("retire").WithDescription("End a memory's validity without deleting its history");
                memory.AddCommand<MemoryPromoteCommand>("promote").WithDescription("Promote a project memory to workspace or user scope");
            });
            configuration.AddBranch("proposals", proposals =>
            {
                proposals.AddCommand<ProposalsListCommand>("list").WithDescription("List memory proposals");
                proposals.AddCommand<ProposalsShowCommand>("show").WithDescription("Show a proposal and look-alike memories");
                proposals.AddCommand<ProposalsApproveCommand>("approve").WithDescription("Approve a proposal, optionally edited");
                proposals.AddCommand<ProposalsRejectCommand>("reject").WithDescription("Reject a proposal");
            });
            configuration.AddBranch("workspace", workspace =>
            {
                workspace.AddCommand<WorkspaceListCommand>("list");
                workspace.AddCommand<WorkspaceCreateCommand>("create");
                workspace.AddCommand<WorkspaceDiscoverCommand>("discover");
                workspace.AddCommand<WorkspaceShowCommand>("show");
                workspace.AddCommand<WorkspaceJoinCommand>("join");
                workspace.AddCommand<WorkspaceLeaveCommand>("leave");
                workspace.AddCommand<WorkspaceDeleteCommand>("delete");
                workspace.AddCommand<WorkspaceRenameCommand>("rename");
            }).WithAlias("workspaces");
            configuration.AddBranch("projects", projects =>
            {
                projects.AddCommand<ProjectsListCommand>("list");
                projects.AddCommand<ProjectsForgetCommand>("forget");
                projects.AddCommand<ProjectsRenameCommand>("rename");
            });
            configureCommands?.Invoke(configuration);
        });

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await app.RunAsync(Normalize(args), cancellationToken);
            return result < 0 ? 2 : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (CommandParseException exception)
        {
            await stderr.WriteLineAsync($"harness: {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or CommandRuntimeException or UnauthorizedAccessException or SqliteException or System.Text.Json.JsonException)
        {
            await stderr.WriteLineAsync($"harness: {exception.Message}");
            return 1;
        }
    }

    private static string[] Normalize(string[] args)
    {
        if (args.Length == 0)
        {
            return ["--help"];
        }

        var normalized = NormalizeLegacyFlags(args);
        if (normalized[0] is "workspace" or "workspaces" or "projects" && (normalized.Length is 1 || normalized[1].StartsWith('-') && normalized[1] is not ("--help" or "-h")))
            normalized = [normalized[0], "list", .. normalized[1..]];
        return normalized[0] switch
        {
            "-v" or "--version" => ["version", .. normalized[1..]],
            "help" => [.. normalized[1..], "--help"],
            _ => normalized,
        };
    }

    private static string[] NormalizeLegacyFlags(string[] args)
    {
        var normalized = (string[])args.Clone();
        var index = 0;
        while (index < normalized.Length)
        {
            var argument = normalized[index];
            if (argument == "--")
            {
                break;
            }

            var equals = argument.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? argument : argument[..equals];
            var replacement = name switch
            {
                "-help" => "--help",
                "-project" => "--project",
                "-dry-run" => "--dry-run",
                "-json" => "--json",
                "-syncs" => "--syncs",
                "-full" => "--full",
                "-since" => "--since",
                "-all" => "--all",
                "-reason" => "--reason",
                "-to" => "--to",
                "-force" => "--force",
                "-status" => "--status",
                "-content" => "--content",
                "-note" => "--note",
                "-supersede" => "--supersede",
                "-keep-both" => "--keep-both",
                "-milestone" => "--milestone",
                "-events" => "--events",
                "-id" => "--id",
                "-prefix" => "--prefix",
                "-title" => "--title",
                "-discover" => "--discover",
                "-clear-title" => "--clear-title",
                "-reset-name" => "--reset-name",
                "-goal" => "--goal",
                "-type" => "--type",
                "-priority" => "--priority",
                "-depends" => "--depends",
                "-accept" => "--accept",
                "-scope" => "--scope",
                "-lease" => "--lease",
                "-summary" => "--summary",
                "-commit" => "--commit",
                "-acc" => "--acc",
                "-acc-all" => "--acc-all",
                "-check" => "--check",
                _ => name,
            };
            normalized[index] = replacement + (equals < 0 ? "" : argument[equals..]);
            if (equals < 0 && replacement is "--discover" or "--project" or "--syncs" or "--reason" or "--to" or "--status" or "--content" or "--note" or "--supersede" or "--milestone" or "--id" or "--prefix" or "--title" or "--goal" or "--type" or "--priority" or "--depends" or "--accept" or "--scope" or "--lease" or "--summary" or "--commit" or "--acc" or "--acc-all" or "--check")
            {
                index++; // A value is data, even when it resembles another option.
            }

            index++;
        }

        return normalized;
    }
}
