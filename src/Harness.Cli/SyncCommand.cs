using System.ComponentModel;
using Harness.Cli.Memory;
using Harness.Core.Sync;
using Harness.Infrastructure.Processes;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Sync;
using Harness.Infrastructure.Findings;
using Harness.Providers.Packs;
using Spectre.Console.Cli;

namespace Harness.Cli;

internal sealed class SyncSettings : ProjectSettings
{
    [CommandOption("--full")]
    [Description("Rescan every file instead of only what changed")]
    public bool Full { get; init; }
    [CommandOption("--since")]
    [Description("Compute changes from this commit instead of the last sync")]
    public string Since { get; init; } = "";
}

internal sealed class SyncCommand(ProjectLifecycle lifecycle, TextWriter output, CliErrorOutput error) : AsyncCommand<SyncSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SyncSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Full && settings.Since.Length > 0)
        { await error.Writer.WriteLineAsync("harness sync: --full and --since are mutually exclusive"); return 2; }
        var location = await lifecycle.LocateAsync(settings.Project, cancellationToken: cancellationToken);
        // A live graphify watch maintains itself; sync must never start it just to ask whether it refreshes.
        var configured = location.Configuration.Value.Providers.Where(pair => pair.Value.Pack is not "graphify-csharp").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        await using var registry = ConfiguredProviders.Build(configured, location.Root, new ProviderProcessFactory());
        var mode = (settings.Full, settings.Since.Length > 0) switch { (true, _) => "full", (_, true) => "since", _ => "incremental" };
        var result = await new ProjectSynchronizer(lifecycle, providers: registry).RunAsync(settings.Project, new SyncOptions { Mode = mode, Since = settings.Since, Refreshers = registry.Providers().OfType<Core.Providers.IProviderRefresher>().ToArray() }, cancellationToken);
        await PrintAsync(result);
        return 0;
    }

    private async Task PrintAsync(ProjectSyncResult result)
    {
        var registration = result.Registration;
        var tree = registration.Worktree;
        var head = Describe(tree);
        await output.WriteLineAsync($"Project   {registration.Project.Id}");
        await output.WriteLineAsync($"Worktree  {tree.Id} {tree.Path}");
        var suffix = result.Previous switch
        {
            null => "worktree newly registered",
            { } previous when previous.Branch != tree.Branch || previous.Commit != tree.Commit || previous.Detached != tree.Detached => "was " + Describe(previous),
            _ => "unchanged",
        };
        await output.WriteLineAsync($"Git       {head} ({suffix})");
        if (registration.Moved)
            await output.WriteLineAsync($"Note      project moved from {registration.PreviousRoot}; location updated");
        if (registration.Copied)
            await output.WriteLineAsync($"Note      copy of {registration.PreviousRoot}: new project id (copied from {registration.Project.CopiedFrom})");
        var dirty = result.State.Dirty ? $"dirty ({result.State.DirtyPaths} uncommitted path(s))" : "clean";
        await output.WriteLineAsync($"State     recorded {head}, working tree {dirty}");
        if (result.CoveredBy is not 0)
            await output.WriteLineAsync($"Files     already synced to this HEAD by a concurrent run (#{result.CoveredBy})");
        else if (result.Files is { } run)
            await PrintFilesAsync(run);
        foreach (var removed in result.Removed)
            await output.WriteLineAsync($"Removed   worktree {removed.Id} {removed.Path} (its git directory is gone)");
        foreach (var finding in result.Findings)
            await PrintFindingsAsync(finding);
        await output.WriteLineAsync("\nSynced.");
    }

    private async Task PrintFindingsAsync(FindingSync finding)
    {
        var description = finding switch
        {
            { Error.Length: > 0 } => "not read (" + finding.Error + ")",
            { Result: { Found: 0, Snapshot: null or { Gone: 0 } } } => "none",
            { Result: { Snapshot: null } result } => $"{result.Found}, unchanged",
            { Result.Snapshot: { } snapshot } => $"{snapshot.Found} ({snapshot.Added} new, {snapshot.Changed} changed, {snapshot.Gone} gone)",
            _ => "none",
        };
        await output.WriteLineAsync("Findings  " + finding.Where + ": " + description);
        if (finding.Result?.Review.Count is > 0)
            await output.WriteLineAsync($"          {finding.Result.Review.Count} marked by design grew and are back for review");
    }

    private async Task PrintFilesAsync(SyncRun run)
    {
        var mode = run.Mode switch { "full" when run.Reason.Length > 0 => "full (" + run.Reason + ")", _ when run.Base.Length > 0 => run.Mode + " from " + Short(run.Base), _ => run.Mode };
        var files = run.Files;
        await output.WriteLineAsync($"Files     {mode}: {files.Added.Count} added, {files.Changed.Count} changed, {files.Removed.Count} removed, {files.Unchanged} unchanged ({files.Hashed} read)");
        var paths = files.Added.Select(path => "+ " + path).Concat(files.Changed.Select(path => "~ " + path)).Concat(files.Removed.Select(path => "- " + path)).ToArray();
        foreach (var path in paths.Take(8))
            await output.WriteLineAsync("          " + path);
        if (paths.Length > 8)
            await output.WriteLineAsync($"          ... and {paths.Length - 8} more");
        foreach (var provider in run.Providers)
            await output.WriteLineAsync("Provider  " + provider.Name + ": " + provider.Status.Replace('_', ' ') + (provider.Detail.Length > 0 ? " — " + provider.Detail : ""));
    }
    private static string Describe(Core.Projects.WorktreeIdentity tree) => (tree.Detached, tree.Commit.Length) switch { (true, _) => "detached at " + Short(tree.Commit), (_, 0) => tree.Branch + " (no commits yet)", _ => tree.Branch + " at " + Short(tree.Commit) };
    private static string Short(string commit) => commit[..Math.Min(12, commit.Length)];
}
