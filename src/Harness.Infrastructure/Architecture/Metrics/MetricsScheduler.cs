using Harness.Core.Providers;
using Harness.Infrastructure.Processes;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Architecture.Metrics;

/// <summary>Starts a detached worker for a commit; returns its process id.</summary>
public interface IMetricsLauncher
{
    int Start(string root, string commit, string log);
}

/// <summary>Runs 'harness complexity measure' detached from the sync that started it, its output in the worker log.</summary>
public sealed class ProcessMetricsLauncher(string executable) : IMetricsLauncher
{
    public int Start(string root, string commit, string log)
    {
        StorageFiles.CreateDirectory(Path.GetDirectoryName(log)!);
        var watch = DetachedProcess.Start(
            new ProviderProcessOptions(executable, ["complexity", "measure", "--project", root, "--commit", commit])
            {
                WorkingDirectory = root,
                AllowDetachedChildren = true,
            },
            log
        );
        StorageFiles.OwnerOnly(log);
        return watch.Id;
    }
}

/// <summary>
/// Called at the end of a sync: starts one worker per project for HEAD when exact values are enabled and missing,
/// and stops a worker still measuring an older commit. Sync never waits for the run.
/// </summary>
public static class MetricsScheduler
{
    public static async Task<string> ScheduleAsync(
        ProjectLocation location,
        string projectId,
        string commit,
        string globalConfigurationPath,
        IMetricsLauncher launcher,
        CancellationToken token
    )
    {
        if (!location.Configuration.Value.Architecture.Complexity.Metrics.Enabled)
        {
            return "";
        }

        if (!OperatingSystem.IsWindows())
        {
            return "unsupported (Microsoft.CodeAnalysis.Metrics runs on Windows only)";
        }

        if (!MetricsStore.ValidCommit(commit))
        {
            return "";
        }

        var store = MetricsStore.For(globalConfigurationPath, projectId);
        if (store.Load(commit) is { State: "done" })
        {
            return "ready for " + commit[..7];
        }

        if (store.Worker() is { } worker && MetricsStore.Alive(worker))
        {
            if (worker.Commit == commit)
            {
                return $"measuring {commit[..7]} (pid {worker.Pid})";
            }

            MetricsStore.Stop(worker);
        }

        var files = await location.Repository!.ListFilesAsync(token).ConfigureAwait(false);
        if (!files.Any(file => Path.GetExtension(file) is ".csproj" or ".vbproj"))
        {
            return "";
        }

        var pid = launcher.Start(location.Root, commit, store.Log);
        store.WriteWorker(pid, commit);
        return $"started for {commit[..7]} (pid {pid})";
    }
}
