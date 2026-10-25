using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Harness.Core.Architecture;
using Harness.Core.Serialization;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Architecture.Metrics;

public sealed record MetricsWorkerRecord(int Pid, string Commit, DateTimeOffset Started);

/// <summary>
/// Per-project Microsoft.CodeAnalysis.Metrics runs, one file per commit, beside the machine's harness
/// configuration: results the worker can rebuild, kept out of the repository and the project database.
/// </summary>
public sealed class MetricsStore(string directory)
{
    private const int Kept = 20;

    public string Directory => directory;
    public string Report => Path.Combine(directory, "report.xml");
    public string Log => Path.Combine(directory, "worker.log");

    private string Runs => Path.Combine(directory, "runs");
    private string WorkerPath => Path.Combine(directory, "worker.json");

    public static MetricsStore For(string globalConfigurationPath, string projectId) =>
        new(Path.Combine(MachineDirectory(globalConfigurationPath), "metrics", projectId));

    public static string ToolCache(string globalConfigurationPath) =>
        Path.Combine(MachineDirectory(globalConfigurationPath), "tools");

    public MetricsRun? Load(string commit)
    {
        if (!ValidCommit(commit))
        {
            return null;
        }

        try
        {
            return HarnessJson.Deserialize<MetricsRun>(File.ReadAllText(RunPath(commit)));
        }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The newest completed run of another commit, whose unchanged projects a new run can keep.</summary>
    public MetricsRun? LatestDone(string except) =>
        System.IO.Directory.Exists(Runs)
            ? new DirectoryInfo(Runs)
                .EnumerateFiles("*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => Load(Path.GetFileNameWithoutExtension(file.Name)))
                .FirstOrDefault(run => run is { State: "done" } && run.Commit != except)
            : null;

    public void Save(MetricsRun run)
    {
        StorageFiles.CreateDirectory(Runs);
        Write(RunPath(run.Commit), HarnessJson.Serialize(run));
        foreach (
            var old in new DirectoryInfo(Runs)
                .EnumerateFiles("*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(Kept)
        )
        {
            old.Delete();
        }
    }

    public MetricsWorkerRecord? Worker()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(WorkerPath));
            return new(
                node?["pid"]?.GetValue<int>() ?? 0,
                node?["commit"]?.GetValue<string>() ?? "",
                DateTimeOffset.Parse(node?["started"]?.GetValue<string>() ?? "", CultureInfo.InvariantCulture)
            );
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or System.Text.Json.JsonException
                        or FormatException
                        or InvalidOperationException
            )
        {
            return null;
        }
    }

    public void WriteWorker(int pid, string commit)
    {
        StorageFiles.CreateDirectory(directory);
        var started = Started(pid) ?? DateTimeOffset.UtcNow;
        Write(
            WorkerPath,
            new JsonObject
            {
                ["pid"] = pid,
                ["commit"] = commit,
                ["started"] = started.ToString("O", CultureInfo.InvariantCulture),
            }.ToJsonString()
        );
    }

    public void ClearWorker(int pid)
    {
        if (Worker() is { } worker && worker.Pid == pid)
        {
            File.Delete(WorkerPath);
        }
    }

    /// <summary>The worker process the record names, if it still runs (a reused pid has another start time).</summary>
    public static bool Alive(MetricsWorkerRecord worker) =>
        Started(worker.Pid) is { } started && (started - worker.Started).Duration() < TimeSpan.FromSeconds(2);

    public static void Stop(MetricsWorkerRecord worker)
    {
        if (!Alive(worker))
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(worker.Pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { /* It exited meanwhile. */
        }
    }

    /// <summary>One worker per project: the lock is held for the whole run and released with the process.</summary>
    public async Task<FileStream?> LockAsync(TimeSpan wait, CancellationToken token)
    {
        StorageFiles.CreateDirectory(directory);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    Path.Combine(directory, "worker.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                );
            }
            catch (IOException) when (timer.Elapsed < wait)
            {
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>What provider_status and doctor say about exact values for HEAD.</summary>
    public ComplexityMetricsStatus Status(string head)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(
                "unsupported",
                "Microsoft.CodeAnalysis.Metrics runs on Windows only; complexity comes from harness's own count"
            );
        }

        var shortHead = Short(head);
        if (Worker() is { } worker && Alive(worker))
        {
            return new("running", $"measuring {Short(worker.Commit)} (pid {worker.Pid})", Commit: worker.Commit);
        }

        var run = head.Length is 0 ? null : Load(head);
        return run switch
        {
            { State: "done" } => new(
                "ready",
                $"exact values for {shortHead}: {run.Members.Count} members of {run.Measured.Count + run.Reused.Count} projects ({run.Tool})",
                Commit: head
            ),
            { State: "failed" } => new("failed", $"{shortHead}: {run.Error}", Fix(run.Failure), head),
            { State: "running" } => new(
                "failed",
                $"{shortHead}: the worker stopped before finishing",
                "run 'harness sync' to start it again",
                head
            ),
            _ when LatestDone(head) is { } older => new(
                "stale",
                $"exact values are for {Short(older.Commit)}; {shortHead} uses harness's own count until the next run",
                "run 'harness sync' (the git hooks do) to measure HEAD",
                older.Commit
            ),
            _ => new("idle", "no run yet; the next 'harness sync' starts one", "run 'harness sync'"),
        };
    }

    private static string? Fix(string? failure) =>
        failure switch
        {
            "sdk" => "install the .NET SDK and make sure dotnet is on PATH",
            "restore" => "check the network and NuGet sources, then run 'harness sync'",
            "load" => "run 'dotnet restore' for the solution so its projects load, then 'harness sync'",
            "timeout" => "raise [architecture.complexity.metrics] timeout_ms, then run 'harness sync'",
            _ => "see the worker log, then run 'harness sync'",
        };

    private static DateTimeOffset? Started(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception error)
            when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>A commit names a file: only a hexadecimal object id is accepted.</summary>
    public static bool ValidCommit(string commit) =>
        commit.Length is >= 7 and <= 64 && commit.All(char.IsAsciiHexDigit);

    private string RunPath(string commit) =>
        ValidCommit(commit)
            ? Path.Combine(Runs, commit + ".json")
            : throw new ArgumentException("commit must be a hexadecimal object id", nameof(commit));

    private static void Write(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
        StorageFiles.OwnerOnly(path);
    }

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;

    private static string MachineDirectory(string globalConfigurationPath) =>
        Path.GetDirectoryName(Path.GetFullPath(globalConfigurationPath))!;
}
