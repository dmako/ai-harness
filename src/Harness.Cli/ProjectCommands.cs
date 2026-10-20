using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.Json;
using Harness.Infrastructure.Projects;
using Spectre.Console.Cli;

namespace Harness.Cli;

internal class ProjectSettings : CommandSettings
{
    [CommandOption("--project")]
    [Description("Directory inside the repository")]
    public string Project { get; init; } = ".";
}

internal sealed class InitSettings : ProjectSettings
{
    [CommandOption("--dry-run")]
    [Description("Show the plan without changing anything")]
    public bool DryRun { get; init; }
}

internal class InspectSettings : ProjectSettings
{
    [CommandOption("--json")]
    [Description("Machine-readable output")]
    public bool Json { get; init; }
}

internal sealed class StatusSettings : InspectSettings
{
    [CommandOption("--syncs")]
    [Description("Number of sync runs to include (available once sync is implemented)")]
    public int Syncs { get; init; }
}

internal sealed class DoctorSettings : InspectSettings
{
    [CommandOption("--all")]
    [Description("Check every project in the machine registry")]
    public bool All { get; init; }
}

internal static class ProjectOutput
{
    // Omit optional null members; required project/git/worktrees/clients nulls are added explicitly below.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static async Task WriteStatusAsync(TextWriter output, ProjectInspection inspection)
    {
        var node = JsonSerializer.SerializeToNode(inspection.Status(), JsonOptions)!.AsObject();
        foreach (var required in new[] { "project", "git", "worktrees", "clients" }.Where(required => !node.ContainsKey(required)))
        {
            node[required] = null;
        }

        await output.WriteLineAsync(node.ToJsonString(JsonOptions));
    }

    internal static object DoctorResult(IReadOnlyList<HealthCheck> checks, string? id = null, string? root = null) => new
    {
        id, root, healthy = checks.All(check => check.Level != "fail"),
        failures = checks.Count(check => check.Level == "fail"), warnings = checks.Count(check => check.Level == "warn"), checks,
    };
}

internal sealed class InitCommand(ProjectLifecycle lifecycle, TextWriter output) : AsyncCommand<InitSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InitSettings settings, CancellationToken cancellationToken)
    {
        var result = await lifecycle.InitAsync(settings.Project, settings.DryRun, cancellationToken);
        var location = result.Location;
        await output.WriteLineAsync($"{(result.DryRun ? "Would initialize" : "Initializing")} {location.Root} ({result.Head}):");
        if (location.StateRoot != location.Root)
        {
            await output.WriteLineAsync($"  (linked worktree: shared state lives in {location.StateRoot})");
        }

        foreach (var action in result.Actions)
        {
            var relative = action.Path.Length == 0 ? "" : Path.GetRelativePath(location.Root, action.Path);
            var display = relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? action.Path : relative;
            await output.WriteLineAsync($"  {action.Verb,-9} {display,-28} {action.Note}");
        }

        await output.WriteLineAsync();
        if (result.Registration is not { } registration)
        {
            await output.WriteLineAsync("Dry run: nothing was changed.");
        }
        else
        {
            await output.WriteLineAsync(registration switch
            {
                { Created: true } => $"Initialized project {registration.Project.Id} (worktree {registration.Worktree.Id}).",
                { Copied: true } => $"This is a copy of the project at {registration.PreviousRoot}: assigned new project {registration.Project.Id} (copied from {registration.Project.CopiedFrom}).",
                { Moved: true } => $"Project {registration.Project.Id} moved from {registration.PreviousRoot}; location updated.",
                _ => $"Already initialized: project {registration.Project.Id} (worktree {registration.Worktree.Id}).",
            });
        }

        return 0;
    }
}

internal sealed class StatusCommand(ProjectLifecycle lifecycle, TextWriter output) : AsyncCommand<StatusSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, StatusSettings settings, CancellationToken cancellationToken)
    {
        var inspection = await lifecycle.InspectAsync(settings.Project, cancellationToken);
        if (settings.Json)
        {
            await ProjectOutput.WriteStatusAsync(output, inspection);
        }
        else
        {
            await output.WriteLineAsync($"Project     {inspection.Project?.Id ?? "not initialized — run 'harness init'"}");
            await output.WriteLineAsync($"Root        {inspection.Location.Root}");
            if (inspection.Location.StateRoot != inspection.Location.Root)
            {
                await output.WriteLineAsync($"State root  {inspection.Location.StateRoot} (shared with linked worktrees)");
            }

            await output.WriteLineAsync($"Git         {inspection.GitError ?? inspection.Head?.ToString()}");
            await output.WriteLineAsync($"Database    {inspection.Location.DatabasePath} ({(inspection.Location.Initialized ? $"schema {inspection.SchemaVersion}/19" : "not created")})");
            if (inspection.ConfigurationError is not null || inspection.DatabaseError is not null)
            {
                await output.WriteLineAsync($"Error       {inspection.ConfigurationError ?? inspection.DatabaseError}");
            }
        }

        return inspection.GitError is null ? 0 : 1;
    }
}

internal sealed class DoctorCommand(ProjectLifecycle lifecycle, BuildInformation information, TextWriter output) : AsyncCommand<DoctorSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings settings, CancellationToken cancellationToken)
    {
        List<object> results = [];
        var failed = false;
        if (settings.All)
        {
            var entries = lifecycle.Registry.Load()["projects"]!.AsArray();
            foreach (var entry in entries)
            {
                var root = entry!["root"]?.GetValue<string>() ?? "";
                var id = entry["id"]?.GetValue<string>() ?? "";
                var checks = Directory.Exists(root)
                    ? (await lifecycle.InspectAsync(root, cancellationToken)).Checks(information.Version)
                    : [new HealthCheck("project", "fail", "repository not found: " + root, "restore it, or remove it from the registry")];
                failed |= checks.Any(check => check.Level == "fail");
                results.Add(ProjectOutput.DoctorResult(checks, id, root));
                if (!settings.Json)
                {
                    await output.WriteLineAsync($"{id}  {root}");
                    await WriteChecksAsync(checks);
                }
            }

            if (settings.Json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { projects = results }, ProjectOutput.JsonOptions));
            }
        }
        else
        {
            var checks = (await lifecycle.InspectAsync(settings.Project, cancellationToken)).Checks(information.Version);
            failed = checks.Any(check => check.Level == "fail");
            if (settings.Json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(ProjectOutput.DoctorResult(checks), ProjectOutput.JsonOptions));
            }
            else
            {
                await WriteChecksAsync(checks);
            }
        }

        return failed ? 1 : 0;
    }

    private async Task WriteChecksAsync(IReadOnlyList<HealthCheck> checks)
    {
        foreach (var check in checks)
        {
            await output.WriteLineAsync($"[{check.Level,-4}] {check.Name,-12} {check.Detail}");
            if (check.Fix is not null)
            {
                await output.WriteLineAsync($"                    fix: {check.Fix}");
            }
        }

        var failures = checks.Count(check => check.Level == "fail");
        var warnings = checks.Count(check => check.Level == "warn");
        await output.WriteLineAsync(failures == 0 ? $"\nHealthy: 0 failures, {warnings} warning(s)." : $"\nUnhealthy: {failures} failure(s), {warnings} warning(s).");
    }
}
