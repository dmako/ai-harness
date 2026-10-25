using System.Globalization;
using Harness.Core.Architecture;
using Harness.Core.Memory;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Sync;

namespace Harness.Infrastructure.Architecture.Metrics;

/// <summary>
/// One background run for one commit: measures the .NET projects whose files changed since the last run (keeping
/// the others' members), stores the result under that commit, and refreshes the findings snapshot while HEAD is
/// still that commit. A run whose commit is no longer HEAD is dropped as superseded.
/// </summary>
public sealed class MetricsWorker(ProjectLifecycle lifecycle, IMetricsTool tool, TimeProvider? clock = null)
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<MetricsRun> RunAsync(string directory, string commit, CancellationToken token)
    {
        if (!MetricsStore.ValidCommit(commit))
        {
            throw new ArgumentException("commit must be a hexadecimal object id", nameof(commit));
        }

        var location = await lifecycle.LocateAsync(directory, cancellationToken: token).ConfigureAwait(false);
        var repository =
            location.Repository ?? throw new InvalidDataException("not a Git working tree: " + location.Root);
        var projectId = ProjectId(location);
        var store = MetricsStore.For(lifecycle.GlobalConfigurationPath, projectId);
        var pid = Environment.ProcessId;
        await using var lease =
            await store.LockAsync(LockWait, token).ConfigureAwait(false)
            ?? throw new IOException("another metrics worker holds the lock for this project");
        if (store.Load(commit) is { State: "done" } measured)
        {
            return measured;
        }

        store.WriteWorker(pid, commit);
        var run = new MetricsRun
        {
            Commit = commit,
            State = "running",
            Tool = tool.Version,
            StartedAt = Now(),
            Pid = pid,
        };
        try
        {
            // Not recorded: a superseded request must not hide an earlier run of the same commit.
            if ((await repository.ReadHeadAsync(token).ConfigureAwait(false)).Commit != commit)
            {
                return run with { State = "superseded", FinishedAt = Now() };
            }

            store.Save(run);
            var settings = location.Configuration.Value.Architecture.Complexity.Metrics;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(settings.TimeoutMs));
            try
            {
                run = await MeasureAsync(location, projectId, store, run, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return Finish(
                    store,
                    run with
                    {
                        Failure = "timeout",
                        Error = $"timed out after {settings.TimeoutMs / 1000} s",
                    },
                    "failed"
                );
            }

            if ((await repository.ReadHeadAsync(token).ConfigureAwait(false)).Commit != commit)
            {
                return Finish(store, run, "superseded");
            }

            run = Finish(store, run, "done");
        }
        catch (MetricsToolException error)
        {
            return Finish(
                store,
                run with
                {
                    Failure = error.Failure,
                    Error = CredentialRedactor.Redact(error.Message),
                },
                "failed"
            );
        }
        catch (Exception error)
            when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException
            )
        {
            return Finish(
                store,
                run with
                {
                    Failure = "worker",
                    Error = CredentialRedactor.Redact(error.Message.Split('\n')[0]),
                },
                "failed"
            );
        }
        finally
        {
            store.ClearWorker(pid);
        }

        await RefreshFindingsAsync(location, projectId, commit, token).ConfigureAwait(false);
        return run;
    }

    private async Task<MetricsRun> MeasureAsync(
        ProjectLocation location,
        string projectId,
        MetricsStore store,
        MetricsRun run,
        CancellationToken token
    )
    {
        var projects = await ProjectsAsync(location, projectId, token).ConfigureAwait(false);
        // Members measured by another tool version are measured again.
        var latest = store.LatestDone(run.Commit);
        var previous = latest?.Tool == run.Tool ? latest : null;
        var changed = previous is null
            ? null
            : await ChangedAsync(location, previous.Commit, run.Commit, token).ConfigureAwait(false);
        // Without an earlier run (or its commit) every project is measured.
        var measure = changed is null
            ? projects
            : projects
                .Where(project =>
                    Holds(project, changed) || !previous!.Measured.Concat(previous.Reused).Contains(project)
                )
                .ToArray();
        var kept = projects.Except(measure, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        List<MeasuredMember> members = [.. previous?.Members.Where(member => kept.Contains(member.Project)) ?? []];
        if (measure.Count > 0)
        {
            var report = await tool.MeasureAsync(
                    location.Root,
                    measure.Select(project => Path.Combine(location.Root, project)).ToArray(),
                    store.Report,
                    token
                )
                .ConfigureAwait(false);
            members.AddRange(MetricsReport.Parse(report, location.Root, measure));
        }

        return run with
        {
            Base = kept.Count > 0 ? previous!.Commit : null,
            Measured = measure,
            Reused = kept.Order(StringComparer.Ordinal).ToArray(),
            Members = members,
        };
    }

    /// <summary>The non-test C# and Visual Basic projects, each after the projects it references.</summary>
    private static async Task<IReadOnlyList<string>> ProjectsAsync(
        ProjectLocation location,
        string projectId,
        CancellationToken token
    )
    {
        var member = new WorkspaceMember("", projectId, location.Root, location.DatabasePath);
        var read = await new ArchitectureReader([member])
            .ProjectAsync(member, null, false, token)
            .ConfigureAwait(false);
        var units = (read.Architecture?.Projects ?? [])
            .Where(unit => unit.Kind is not "test" && Path.GetExtension(unit.Path) is ".csproj" or ".vbproj")
            .ToDictionary(unit => unit.Path, StringComparer.Ordinal);
        List<string> ordered = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        void Visit(string path)
        {
            if (!units.TryGetValue(path, out var unit) || !seen.Add(path))
            {
                return;
            }

            foreach (var reference in unit.References.Order(StringComparer.Ordinal))
            {
                Visit(reference);
            }

            ordered.Add(path);
        }
        foreach (var path in units.Keys.Order(StringComparer.Ordinal))
        {
            Visit(path);
        }

        return ordered;
    }

    private static async Task<IReadOnlyList<string>?> ChangedAsync(
        ProjectLocation location,
        string from,
        string to,
        CancellationToken token
    )
    {
        var repository = location.Repository!;
        if (!await repository.HasCommitAsync(from, token).ConfigureAwait(false))
        {
            return null;
        }

        var committed = await repository.ChangedBetweenAsync(from, to, token).ConfigureAwait(false);
        var dirty = await repository.DirtyPathsAsync(token).ConfigureAwait(false);
        return [.. committed, .. dirty];
    }

    private static bool Holds(string project, IReadOnlyList<string> files)
    {
        var directory = Path.GetDirectoryName(project)?.Replace('\\', '/') ?? "";
        return files.Any(file =>
            directory.Length is 0 || file.StartsWith(directory + "/", StringComparison.Ordinal) || file == project
        );
    }

    /// <summary>The snapshot takes the exact values now, unless a sync has moved HEAD meanwhile.</summary>
    private async Task RefreshFindingsAsync(
        ProjectLocation location,
        string projectId,
        string commit,
        CancellationToken token
    )
    {
        var repository = location.Repository!;
        await using var sync = await WorktreeSyncLock.AcquireAsync(repository.GitDir, token).ConfigureAwait(false);
        if ((await repository.ReadHeadAsync(token).ConfigureAwait(false)).Commit != commit)
        {
            return;
        }

        using var database = HarnessDatabase.Open(
            location.DatabasePath,
            new() { BusyTimeout = TimeSpan.FromMilliseconds(location.Configuration.Value.Storage.BusyTimeoutMs) },
            token
        );
        await new FindingSets(location.Root, projectId, database, lifecycle.GlobalConfigurationPath, lifecycle.Registry)
            .SyncAsync(commit, null, token)
            .ConfigureAwait(false);
    }

    private static string ProjectId(ProjectLocation location)
    {
        if (!location.Initialized)
        {
            throw new InvalidDataException("project not initialized; run 'harness init'");
        }

        using var database = HarnessDatabase.OpenReadOnly(location.DatabasePath);
        return ProjectStore.Load(database)?.Id ?? throw new InvalidDataException("project not registered");
    }

    private MetricsRun Finish(MetricsStore store, MetricsRun run, string state)
    {
        var finished = run with { State = state, FinishedAt = Now() };
        store.Save(finished);
        return finished;
    }

    private string Now() => _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
}
