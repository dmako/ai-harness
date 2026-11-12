using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Core.Serialization;
using Harness.Infrastructure.Projects;

namespace Harness.Cli;

internal class ProjectSettings
{
    public string Project { get; init; } = ".";
}

internal sealed class InitSettings : ProjectSettings
{
    public bool DryRun { get; init; }
}

internal class InspectSettings : ProjectSettings
{
    public bool Json { get; init; }
}

internal sealed class StatusSettings : InspectSettings
{
    public int Syncs { get; init; }
}

internal sealed class DoctorSettings : InspectSettings
{
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
        var node = HarnessJson.SerializeToNode(inspection.Status(), JsonOptions)!.AsObject();
        foreach (
            var required in new[] { "project", "git", "worktrees", "clients" }.Where(required =>
                !node.ContainsKey(required)
            )
        )
        {
            node[required] = null;
        }

        await output.WriteLineAsync(node.ToJsonString(JsonOptions));
    }

    internal static object DoctorResult(IReadOnlyList<HealthCheck> checks, string? id = null, string? root = null) =>
        new JsonObjectMap
        {
            ["id"] = id,
            ["root"] = root,
            ["healthy"] = checks.All(check => check.Level != "fail"),
            ["failures"] = checks.Count(check => check.Level == "fail"),
            ["warnings"] = checks.Count(check => check.Level == "warn"),
            ["checks"] = checks,
        };
}

internal sealed class InitCommand(ProjectLifecycle lifecycle, TextWriter output)
{
    public async Task<int> ExecuteAsync(InitSettings settings, CancellationToken cancellationToken)
    {
        var result = await lifecycle.InitAsync(settings.Project, settings.DryRun, cancellationToken);
        var location = result.Location;
        await output.WriteLineAsync(
            $"{(result.DryRun ? "Would initialize" : "Initializing")} {location.Root} ({result.Head}):",
            CancellationToken.None
        );
        if (location.StateRoot != location.Root)
        {
            await output.WriteLineAsync(
                $"  (linked worktree: shared state lives in {location.StateRoot})",
                CancellationToken.None
            );
        }

        foreach (var action in result.Actions)
        {
            var relative = action.Path.Length == 0 ? "" : Path.GetRelativePath(location.Root, action.Path);
            var display =
                relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    ? action.Path
                    : relative;
            await output.WriteLineAsync($"  {action.Verb, -9} {display, -28} {action.Note}", CancellationToken.None);
        }

        await output.WriteLineAsync(CancellationToken.None);
        if (result.Registration is not { } registration)
        {
            await output.WriteLineAsync("Dry run: nothing was changed.", CancellationToken.None);
        }
        else
        {
            await output.WriteLineAsync(
                registration switch
                {
                    { Created: true } =>
                        $"Initialized project {registration.Project.Id} (worktree {registration.Worktree.Id}).",
                    { Copied: true } =>
                        $"This is a copy of the project at {registration.PreviousRoot}: assigned new project {registration.Project.Id} (copied from {registration.Project.CopiedFrom}).",
                    { Moved: true } =>
                        $"Project {registration.Project.Id} moved from {registration.PreviousRoot}; location updated.",
                    _ =>
                        $"Already initialized: project {registration.Project.Id} (worktree {registration.Worktree.Id}).",
                },
                CancellationToken.None
            );
        }

        return 0;
    }
}

internal sealed class StatusCommand(ProjectLifecycle lifecycle, TextWriter output)
{
    public async Task<int> ExecuteAsync(StatusSettings settings, CancellationToken cancellationToken)
    {
        var inspection = await lifecycle.InspectAsync(settings.Project, cancellationToken);
        if (settings.Json)
        {
            await ProjectOutput.WriteStatusAsync(output, inspection);
        }
        else
        {
            await output.WriteLineAsync(
                $"Project     {inspection.Project?.Id ?? "not initialized — run 'harness init'"}",
                CancellationToken.None
            );
            await output.WriteLineAsync($"Root        {inspection.Location.Root}", CancellationToken.None);
            if (inspection.Location.StateRoot != inspection.Location.Root)
            {
                await output.WriteLineAsync(
                    $"State root  {inspection.Location.StateRoot} (shared with linked worktrees)",
                    CancellationToken.None
                );
            }

            await output.WriteLineAsync(
                $"Git         {inspection.GitError ?? inspection.Head?.ToString()}",
                CancellationToken.None
            );
            await output.WriteLineAsync(
                $"Database    {inspection.Location.DatabasePath} ({(inspection.Location.Initialized ? $"schema {inspection.SchemaVersion}/19" : "not created")})",
                CancellationToken.None
            );
            if (inspection.Scm is { Status: not Harness.Scm.ScmResolutionStatus.NoScm } scm)
            {
                // how it was chosen, auto-detection included: several SCMs may serve one server
                var detected = scm is { Match: not null, Source: Harness.Scm.ScmChoiceSource.Automatic }
                    ? ", " + scm.Chosen()
                    : "";
                await output.WriteLineAsync($"SCM         {scm.Summary()}{detected}", CancellationToken.None);
            }
            if (inspection.ConfigurationError is not null || inspection.DatabaseError is not null)
            {
                await output.WriteLineAsync(
                    $"Error       {inspection.ConfigurationError ?? inspection.DatabaseError}",
                    CancellationToken.None
                );
            }
        }

        return inspection.GitError is null ? 0 : 1;
    }
}

internal sealed class DoctorCommand(
    ProjectLifecycle lifecycle,
    BuildInformation information,
    TextWriter output,
    Harness.Cli.Scm.ScmHttpOptions scmHttp
)
{
    private Harness.Infrastructure.Scm.ScmConfigurationFile ScmStore =>
        Harness.Infrastructure.Scm.ScmConfigurationFile.ForGlobal(lifecycle.GlobalConfigurationPath);

    public async Task<int> ExecuteAsync(DoctorSettings settings, CancellationToken cancellationToken)
    {
        List<object> results = [];
        var failed = false;
        if (settings.All)
        {
            // the machine's SCMs are tested once; each project adds whether its own project is reachable
            var (scmChecks, scmTests) = await Harness.Cli.Scm.ScmDoctor.MachineAsync(
                ScmStore,
                scmHttp.Handler,
                cancellationToken
            );
            var entries = lifecycle.Registry.Load()["projects"]!.AsArray();
            foreach (var entry in entries)
            {
                var root = entry!["root"]?.GetValue<string>() ?? "";
                var id = entry["id"]?.GetValue<string>() ?? "";
                List<HealthCheck> checks;
                if (Directory.Exists(root))
                {
                    var inspection = await lifecycle.InspectAsync(root, cancellationToken);
                    checks = [.. inspection.Checks(information.Version)];
                    if (inspection.Scm is { } scm && scmTests.Count > 0)
                    {
                        checks.Add(
                            await Harness.Cli.Scm.ScmDoctor.ProjectCheckAsync(
                                scm,
                                scmTests,
                                scmHttp.Handler,
                                cancellationToken
                            )
                        );
                    }
                }
                else
                {
                    checks =
                    [
                        new HealthCheck(
                            "project",
                            "fail",
                            "repository not found: " + root,
                            "restore it, or remove it from the registry"
                        ),
                    ];
                }

                failed |= checks.Any(check => check.Level == "fail");
                results.Add(ProjectOutput.DoctorResult(checks, id, root));
                if (!settings.Json)
                {
                    await output.WriteLineAsync($"{id}  {root}", CancellationToken.None);
                    await WriteChecksAsync(checks);
                }
            }

            failed |= scmChecks.Any(check => check.Level == "fail");
            if (settings.Json)
            {
                await output.WriteLineAsync(
                    HarnessJson.Serialize(
                        new JsonObjectMap { ["projects"] = results, ["scm"] = ProjectOutput.DoctorResult(scmChecks) },
                        ProjectOutput.JsonOptions
                    ),
                    CancellationToken.None
                );
            }
            else
            {
                await output.WriteLineAsync("SCM connections on this machine", CancellationToken.None);
                await WriteChecksAsync(scmChecks);
            }
        }
        else
        {
            var inspection = await lifecycle.InspectAsync(settings.Project, cancellationToken);
            List<HealthCheck> checks =
            [
                .. inspection.Checks(information.Version),
                .. await Harness.Cli.Scm.ScmDoctor.ChecksAsync(
                    ScmStore,
                    inspection.Scm,
                    scmHttp.Handler,
                    cancellationToken
                ),
            ];
            failed = checks.Any(check => check.Level == "fail");
            if (settings.Json)
            {
                await output.WriteLineAsync(
                    HarnessJson.Serialize(ProjectOutput.DoctorResult(checks), ProjectOutput.JsonOptions),
                    CancellationToken.None
                );
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
            await output.WriteLineAsync($"[{check.Level, -4}] {check.Name, -12} {check.Detail}");
            if (check.Fix is not null)
            {
                await output.WriteLineAsync($"                    fix: {check.Fix}");
            }
        }

        var failures = checks.Count(check => check.Level == "fail");
        var warnings = checks.Count(check => check.Level == "warn");
        await output.WriteLineAsync(
            failures == 0
                ? $"\nHealthy: 0 failures, {warnings} warning(s)."
                : $"\nUnhealthy: {failures} failure(s), {warnings} warning(s)."
        );
    }
}
