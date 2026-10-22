using System.CommandLine;
using System.CommandLine.Parsing;
using Harness.Web;

namespace Harness.Cli;

internal static class CommandBindings
{
    internal static Func<ParseResult, Harness.Cli.VersionSettings> BindVersionSettings(Command command)
    {
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        return result => new Harness.Cli.VersionSettings { Json = result.GetValue(jsonOption) };
    }

    internal static Func<ParseResult, Harness.Cli.InitSettings> BindInitSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var dryRunOption = new Option<bool>("--dry-run")
        {
            DefaultValueFactory = _ => false,
            Description = "Show the plan without changing anything",
        };
        command.Options.Add(dryRunOption);
        return result => new Harness.Cli.InitSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            DryRun = result.GetValue(dryRunOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.StatusSettings> BindStatusSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var syncsOption = new Option<int>("--syncs")
        {
            DefaultValueFactory = _ => 0,
            Description = "Number of sync runs to include (available once sync is implemented)",
        };
        command.Options.Add(syncsOption);
        return result => new Harness.Cli.StatusSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Syncs = result.GetValue(syncsOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.DoctorSettings> BindDoctorSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var allOption = new Option<bool>("--all")
        {
            DefaultValueFactory = _ => false,
            Description = "Check every project in the machine registry",
        };
        command.Options.Add(allOption);
        return result => new Harness.Cli.DoctorSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            All = result.GetValue(allOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.Ui.UiSettings> BindUiSettings(Command command)
    {
        var addressOption = new Option<string>("--addr")
        {
            DefaultValueFactory = _ => UiHost.DefaultAddress,
            Description = "Loopback listen address (default 127.0.0.1:17477)",
        };
        command.Options.Add(addressOption);
        var noOpenOption = new Option<bool>("--no-open")
        {
            DefaultValueFactory = _ => false,
            Description = "Do not open the browser",
        };
        command.Options.Add(noOpenOption);
        return result => new Harness.Cli.Ui.UiSettings
        {
            Address = result.GetValue(addressOption) ?? "",
            NoOpen = result.GetValue(noOpenOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.ProjectSettings> BindProjectSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        return result => new Harness.Cli.ProjectSettings { Project = result.GetValue(projectOption) ?? "" };
    }

    internal static Func<ParseResult, Harness.Cli.SyncSettings> BindSyncSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var fullOption = new Option<bool>("--full")
        {
            DefaultValueFactory = _ => false,
            Description = "Rescan every file instead of only what changed",
        };
        command.Options.Add(fullOption);
        var sinceOption = new Option<string>("--since")
        {
            DefaultValueFactory = _ => "",
            Description = "Compute changes from this commit instead of the last sync",
        };
        command.Options.Add(sinceOption);
        return result => new Harness.Cli.SyncSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Full = result.GetValue(fullOption),
            Since = result.GetValue(sinceOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Setup.InstallSettings> BindInstallSettings(Command command)
    {
        var projectOption = new Option<string>("--project") { DefaultValueFactory = _ => ".", Description = null };
        command.Options.Add(projectOption);
        var dryRunOption = new Option<bool>("--dry-run") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(dryRunOption);
        var clientOption = new Option<string>("--client") { DefaultValueFactory = _ => "all", Description = null };
        command.Options.Add(clientOption);
        var scopeOption = new Option<string>("--scope") { DefaultValueFactory = _ => "user", Description = null };
        command.Options.Add(scopeOption);
        var commandOption = new Option<string>("--command") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(commandOption);
        return result => new Harness.Cli.Setup.InstallSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            DryRun = result.GetValue(dryRunOption),
            Client = result.GetValue(clientOption) ?? "",
            Scope = result.GetValue(scopeOption) ?? "",
            Command = result.GetValue(commandOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Setup.UninstallSettings> BindUninstallSettings(Command command)
    {
        var projectOption = new Option<string>("--project") { DefaultValueFactory = _ => ".", Description = null };
        command.Options.Add(projectOption);
        var dryRunOption = new Option<bool>("--dry-run") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(dryRunOption);
        var clientOption = new Option<string>("--client") { DefaultValueFactory = _ => "all", Description = null };
        command.Options.Add(clientOption);
        var scopeOption = new Option<string>("--scope") { DefaultValueFactory = _ => "user", Description = null };
        command.Options.Add(scopeOption);
        var allOption = new Option<bool>("--all") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(allOption);
        var deleteDataOption = new Option<bool>("--delete-data")
        {
            DefaultValueFactory = _ => false,
            Description = null,
        };
        command.Options.Add(deleteDataOption);
        return result => new Harness.Cli.Setup.UninstallSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            DryRun = result.GetValue(dryRunOption),
            Client = result.GetValue(clientOption) ?? "",
            Scope = result.GetValue(scopeOption) ?? "",
            All = result.GetValue(allOption),
            DeleteData = result.GetValue(deleteDataOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.Setup.InstructionsSettings> BindInstructionsSettings(Command command)
    {
        var projectOption = new Option<string>("--project") { DefaultValueFactory = _ => ".", Description = null };
        command.Options.Add(projectOption);
        var dryRunOption = new Option<bool>("--dry-run") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(dryRunOption);
        var removeOption = new Option<bool>("--remove") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(removeOption);
        return result => new Harness.Cli.Setup.InstructionsSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            DryRun = result.GetValue(dryRunOption),
            Remove = result.GetValue(removeOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.Hooks.HookChooseSettings> BindHookChooseSettings(Command command)
    {
        var projectOption = new Option<string>("--project") { DefaultValueFactory = _ => ".", Description = null };
        command.Options.Add(projectOption);
        var scopeOption = new Option<string>("--scope") { DefaultValueFactory = _ => "user", Description = null };
        command.Options.Add(scopeOption);
        return result => new Harness.Cli.Hooks.HookChooseSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Scope = result.GetValue(scopeOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Hooks.HookInstallSettings> BindHookInstallSettings(Command command)
    {
        var projectOption = new Option<string>("--project") { DefaultValueFactory = _ => ".", Description = null };
        command.Options.Add(projectOption);
        var scopeOption = new Option<string>("--scope") { DefaultValueFactory = _ => "user", Description = null };
        command.Options.Add(scopeOption);
        var kindOption = new Option<string>("--kind") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(kindOption);
        var dryRunOption = new Option<bool>("--dry-run") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(dryRunOption);
        var commandOption = new Option<string>("--command") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(commandOption);
        return result => new Harness.Cli.Hooks.HookInstallSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Scope = result.GetValue(scopeOption) ?? "",
            Kind = result.GetValue(kindOption) ?? "",
            DryRun = result.GetValue(dryRunOption),
            Command = result.GetValue(commandOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Hooks.HookChangeSettings> BindHookChangeSettings(Command command)
    {
        var projectOption = new Option<string>("--project") { DefaultValueFactory = _ => ".", Description = null };
        command.Options.Add(projectOption);
        var scopeOption = new Option<string>("--scope") { DefaultValueFactory = _ => "user", Description = null };
        command.Options.Add(scopeOption);
        var kindOption = new Option<string>("--kind") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(kindOption);
        var dryRunOption = new Option<bool>("--dry-run") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(dryRunOption);
        return result => new Harness.Cli.Hooks.HookChangeSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Scope = result.GetValue(scopeOption) ?? "",
            Kind = result.GetValue(kindOption) ?? "",
            DryRun = result.GetValue(dryRunOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.Hooks.HookStatusSettings> BindHookStatusSettings(Command command)
    {
        var projectOption = new Option<string>("--project") { DefaultValueFactory = _ => ".", Description = null };
        command.Options.Add(projectOption);
        var scopeOption = new Option<string>("--scope") { DefaultValueFactory = _ => "user", Description = null };
        command.Options.Add(scopeOption);
        var kindOption = new Option<string>("--kind") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(kindOption);
        return result => new Harness.Cli.Hooks.HookStatusSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Scope = result.GetValue(scopeOption) ?? "",
            Kind = result.GetValue(kindOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.EmptySettings> BindCommandSettings(Command _)
    {
        return _ => new Harness.Cli.EmptySettings();
    }

    internal static Func<ParseResult, Harness.Cli.Hooks.CompletionSettings> BindCompletionSettings(Command command)
    {
        var shellOption = new Option<string>("--shell") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(shellOption);
        return result => new Harness.Cli.Hooks.CompletionSettings { Shell = result.GetValue(shellOption) ?? "" };
    }

    internal static Func<ParseResult, Harness.Cli.Telemetry.ExportSettings> BindExportSettings(Command command)
    {
        var endpointOption = new Option<string>("--endpoint") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(endpointOption);
        var tokenOption = new Option<string>("--token") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(tokenOption);
        var autoOption = new Option<bool>("--auto") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(autoOption);
        return result => new Harness.Cli.Telemetry.ExportSettings
        {
            Endpoint = result.GetValue(endpointOption) ?? "",
            Token = result.GetValue(tokenOption) ?? "",
            Auto = result.GetValue(autoOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.DocsSettings> BindDocsSettings(Command command)
    {
        var sectionOption = new Argument<string>("section")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = null,
            DefaultValueFactory = _ => "",
        };
        command.Arguments.Add(sectionOption);
        return result => new Harness.Cli.DocsSettings { Section = result.GetValue(sectionOption) ?? "" };
    }

    internal static Func<ParseResult, Harness.Cli.SessionsListSettings> BindSessionsListSettings(Command command)
    {
        var jsonOption = new Option<bool>("--json") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(jsonOption);
        return result => new Harness.Cli.SessionsListSettings { Json = result.GetValue(jsonOption) };
    }

    internal static Func<ParseResult, Harness.Cli.SessionStopSettings> BindSessionStopSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var idOption = new Argument<string>("id")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = null,
            DefaultValueFactory = _ => "",
        };
        command.Arguments.Add(idOption);
        return result => new Harness.Cli.SessionStopSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Id = result.GetValue(idOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Telemetry.TelemetrySettings> BindTelemetrySettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var daysOption = new Option<int>("--days") { DefaultValueFactory = _ => 30, Description = "Window in days" };
        command.Options.Add(daysOption);
        return result => new Harness.Cli.Telemetry.TelemetrySettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Days = result.GetValue(daysOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.Telemetry.ReportSettings> BindReportSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var daysOption = new Option<int>("--days") { DefaultValueFactory = _ => 30, Description = "Window in days" };
        command.Options.Add(daysOption);
        var allOption = new Option<bool>("--all")
        {
            DefaultValueFactory = _ => false,
            Description = "Every registered project",
        };
        command.Options.Add(allOption);
        return result => new Harness.Cli.Telemetry.ReportSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Days = result.GetValue(daysOption),
            All = result.GetValue(allOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.Tasks.TaskListSettings> BindTaskListSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var statusOption = new Option<string[]>("--status")
        {
            DefaultValueFactory = _ => [],
            Description = null,
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.OneOrMore,
        };
        command.Options.Add(statusOption);
        var milestoneOption = new Option<string>("--milestone") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(milestoneOption);
        var allOption = new Option<bool>("--all") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(allOption);
        return result => new Harness.Cli.Tasks.TaskListSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Status = result.GetValue(statusOption) ?? [],
            Milestone = result.GetValue(milestoneOption) ?? "",
            All = result.GetValue(allOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.InspectSettings> BindInspectSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        return result => new Harness.Cli.InspectSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.Tasks.TaskShowSettings> BindTaskShowSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        var eventsOption = new Option<bool>("--events") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(eventsOption);
        return result => new Harness.Cli.Tasks.TaskShowSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
            Events = result.GetValue(eventsOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.Tasks.TaskNextSettings> BindTaskNextSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var milestoneOption = new Option<string>("--milestone") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(milestoneOption);
        return result => new Harness.Cli.Tasks.TaskNextSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Milestone = result.GetValue(milestoneOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Tasks.TaskFileSettings> BindTaskFileSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var fileOption = new Argument<string>("file")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = null,
            DefaultValueFactory = _ => "",
        };
        command.Arguments.Add(fileOption);
        var dryRunOption = new Option<bool>("--dry-run") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(dryRunOption);
        return result => new Harness.Cli.Tasks.TaskFileSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            File = result.GetValue(fileOption) ?? "",
            DryRun = result.GetValue(dryRunOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.Tasks.TaskAddSettings> BindTaskAddSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Option<string>("--id") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(idOption);
        var prefixOption = new Option<string>("--prefix") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(prefixOption);
        var titleOption = new Option<string>("--title") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(titleOption);
        var goalOption = new Option<string>("--goal") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(goalOption);
        var typeOption = new Option<string>("--type") { DefaultValueFactory = _ => "feature", Description = null };
        command.Options.Add(typeOption);
        var priorityOption = new Option<string>("--priority")
        {
            DefaultValueFactory = _ => "medium",
            Description = null,
        };
        command.Options.Add(priorityOption);
        var milestoneOption = new Option<string>("--milestone") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(milestoneOption);
        var dependsOption = new Option<string[]>("--depends")
        {
            DefaultValueFactory = _ => [],
            Description = null,
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.OneOrMore,
        };
        command.Options.Add(dependsOption);
        var acceptOption = new Option<string[]>("--accept")
        {
            DefaultValueFactory = _ => [],
            Description = null,
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.OneOrMore,
        };
        command.Options.Add(acceptOption);
        var scopeOption = new Option<string[]>("--scope")
        {
            DefaultValueFactory = _ => [],
            Description = null,
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.OneOrMore,
        };
        command.Options.Add(scopeOption);
        return result => new Harness.Cli.Tasks.TaskAddSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
            Prefix = result.GetValue(prefixOption) ?? "",
            Title = result.GetValue(titleOption) ?? "",
            Goal = result.GetValue(goalOption) ?? "",
            Type = result.GetValue(typeOption) ?? "",
            Priority = result.GetValue(priorityOption) ?? "",
            Milestone = result.GetValue(milestoneOption) ?? "",
            Depends = result.GetValue(dependsOption) ?? [],
            Accept = result.GetValue(acceptOption) ?? [],
            Scope = result.GetValue(scopeOption) ?? [],
        };
    }

    internal static Func<ParseResult, Harness.Cli.Tasks.TaskReasonSettings> BindTaskReasonSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        var reasonOption = new Option<string>("--reason") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(reasonOption);
        return result => new Harness.Cli.Tasks.TaskReasonSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
            Reason = result.GetValue(reasonOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Tasks.TaskReadySettings> BindTaskReadySettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        var noteOption = new Option<string>("--note") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(noteOption);
        return result => new Harness.Cli.Tasks.TaskReadySettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
            Note = result.GetValue(noteOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Tasks.TaskClaimSettings> BindTaskClaimSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        var leaseOption = new Option<string>("--lease") { DefaultValueFactory = _ => "2h0m0s", Description = null };
        command.Options.Add(leaseOption);
        return result => new Harness.Cli.Tasks.TaskClaimSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
            Lease = result.GetValue(leaseOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Tasks.TaskCompleteSettings> BindTaskCompleteSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        var summaryOption = new Option<string>("--summary") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(summaryOption);
        var commitOption = new Option<string>("--commit") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(commitOption);
        var acceptanceOption = new Option<string[]>("--acc")
        {
            DefaultValueFactory = _ => [],
            Description = null,
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.OneOrMore,
        };
        command.Options.Add(acceptanceOption);
        var allOption = new Option<string>("--acc-all") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(allOption);
        var checksOption = new Option<string[]>("--check")
        {
            DefaultValueFactory = _ => [],
            Description = null,
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.OneOrMore,
        };
        command.Options.Add(checksOption);
        return result => new Harness.Cli.Tasks.TaskCompleteSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
            Summary = result.GetValue(summaryOption) ?? "",
            Commit = result.GetValue(commitOption) ?? "",
            Acceptance = result.GetValue(acceptanceOption) ?? [],
            All = result.GetValue(allOption) ?? "",
            Checks = result.GetValue(checksOption) ?? [],
        };
    }

    internal static Func<ParseResult, Harness.Cli.Tasks.TaskProposalListSettings> BindTaskProposalListSettings(
        Command command
    )
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var statusOption = new Option<string>("--status") { DefaultValueFactory = _ => "pending", Description = null };
        command.Options.Add(statusOption);
        return result => new Harness.Cli.Tasks.TaskProposalListSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Status = result.GetValue(statusOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Tasks.TaskApproveSettings> BindTaskApproveSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        var prefixOption = new Option<string>("--prefix") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(prefixOption);
        return result => new Harness.Cli.Tasks.TaskApproveSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
            Prefix = result.GetValue(prefixOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Memory.MemoryIdSettings> BindMemoryIdSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        return result => new Harness.Cli.Memory.MemoryIdSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Memory.MemoryRetireSettings> BindMemoryRetireSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        var reasonOption = new Option<string>("--reason")
        {
            DefaultValueFactory = _ => "",
            Description = "Why it no longer applies (required)",
        };
        command.Options.Add(reasonOption);
        return result => new Harness.Cli.Memory.MemoryRetireSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
            Reason = result.GetValue(reasonOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Memory.MemoryPromoteSettings> BindMemoryPromoteSettings(
        Command command
    )
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        var toOption = new Option<string>("--to") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(toOption);
        var forceOption = new Option<bool>("--force") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(forceOption);
        return result => new Harness.Cli.Memory.MemoryPromoteSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
            To = result.GetValue(toOption) ?? "",
            Force = result.GetValue(forceOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.Memory.ProposalListSettings> BindProposalListSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var statusOption = new Option<string>("--status") { DefaultValueFactory = _ => "pending", Description = null };
        command.Options.Add(statusOption);
        return result => new Harness.Cli.Memory.ProposalListSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Status = result.GetValue(statusOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Memory.ProposalApproveSettings> BindProposalApproveSettings(
        Command command
    )
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        var noteOption = new Option<string>("--note") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(noteOption);
        var contentOption = new Option<string>("--content") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(contentOption);
        var supersedeOption = new Option<string[]>("--supersede")
        {
            DefaultValueFactory = _ => [],
            Description = null,
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.OneOrMore,
        };
        command.Options.Add(supersedeOption);
        var keepBothOption = new Option<bool>("--keep-both") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(keepBothOption);
        return result => new Harness.Cli.Memory.ProposalApproveSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
            Note = result.GetValue(noteOption) ?? "",
            Content = result.GetValue(contentOption) ?? "",
            Supersede = result.GetValue(supersedeOption) ?? [],
            KeepBoth = result.GetValue(keepBothOption),
        };
    }

    internal static Func<ParseResult, Harness.Cli.Memory.ProposalNoteSettings> BindProposalNoteSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        var noteOption = new Option<string>("--note") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(noteOption);
        return result => new Harness.Cli.Memory.ProposalNoteSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
            Note = result.GetValue(noteOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Workspaces.WorkspaceCreateSettings> BindWorkspaceCreateSettings(
        Command command
    )
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var nameOption = new Argument<string>("workspace") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(nameOption);
        var discoverOption = new Option<string>("--discover") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(discoverOption);
        var titleOption = new Option<string>("--title") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(titleOption);
        return result => new Harness.Cli.Workspaces.WorkspaceCreateSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Name = result.GetValue(nameOption) ?? "",
            Discover = result.GetValue(discoverOption) ?? "",
            Title = result.GetValue(titleOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Workspaces.WorkspaceDiscoverSettings> BindWorkspaceDiscoverSettings(
        Command command
    )
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var directoryOption = new Argument<string>("directory")
        {
            Arity = ArgumentArity.ExactlyOne,
            Description = null,
        };
        command.Arguments.Add(directoryOption);
        return result => new Harness.Cli.Workspaces.WorkspaceDiscoverSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Directory = result.GetValue(directoryOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Workspaces.WorkspaceSettings> BindWorkspaceSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var nameOption = new Argument<string>("workspace") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(nameOption);
        return result => new Harness.Cli.Workspaces.WorkspaceSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Name = result.GetValue(nameOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Workspaces.WorkspaceRenameSettings> BindWorkspaceRenameSettings(
        Command command
    )
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var toOption = new Option<string>("--to") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(toOption);
        var titleOption = new Option<string>("--title") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(titleOption);
        var clearTitleOption = new Option<bool>("--clear-title")
        {
            DefaultValueFactory = _ => false,
            Description = null,
        };
        command.Options.Add(clearTitleOption);
        var resetNameOption = new Option<bool>("--reset-name") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(resetNameOption);
        var nameOption = new Argument<string>("workspace") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(nameOption);
        return result => new Harness.Cli.Workspaces.WorkspaceRenameSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            To = result.GetValue(toOption) ?? "",
            Title = result.GetValue(titleOption) ?? "",
            ClearTitle = result.GetValue(clearTitleOption),
            ResetName = result.GetValue(resetNameOption),
            Name = result.GetValue(nameOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Workspaces.ProjectForgetSettings> BindProjectForgetSettings(
        Command command
    )
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var idOption = new Argument<string>("id") { Arity = ArgumentArity.ExactlyOne, Description = null };
        command.Arguments.Add(idOption);
        return result => new Harness.Cli.Workspaces.ProjectForgetSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            Id = result.GetValue(idOption) ?? "",
        };
    }

    internal static Func<ParseResult, Harness.Cli.Workspaces.RenameSettings> BindRenameSettings(Command command)
    {
        var projectOption = new Option<string>("--project")
        {
            DefaultValueFactory = _ => ".",
            Description = "Directory inside the repository",
        };
        command.Options.Add(projectOption);
        var jsonOption = new Option<bool>("--json")
        {
            DefaultValueFactory = _ => false,
            Description = "Machine-readable output",
        };
        command.Options.Add(jsonOption);
        var toOption = new Option<string>("--to") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(toOption);
        var titleOption = new Option<string>("--title") { DefaultValueFactory = _ => "", Description = null };
        command.Options.Add(titleOption);
        var clearTitleOption = new Option<bool>("--clear-title")
        {
            DefaultValueFactory = _ => false,
            Description = null,
        };
        command.Options.Add(clearTitleOption);
        var resetNameOption = new Option<bool>("--reset-name") { DefaultValueFactory = _ => false, Description = null };
        command.Options.Add(resetNameOption);
        return result => new Harness.Cli.Workspaces.RenameSettings
        {
            Project = result.GetValue(projectOption) ?? "",
            Json = result.GetValue(jsonOption),
            To = result.GetValue(toOption) ?? "",
            Title = result.GetValue(titleOption) ?? "",
            ClearTitle = result.GetValue(clearTitleOption),
            ResetName = result.GetValue(resetNameOption),
        };
    }
}

internal readonly record struct EmptySettings;
