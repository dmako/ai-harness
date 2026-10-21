using Harness.Core.Projects;
using Harness.Core.Sync;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Projects;

public sealed partial class HarnessStateSession(ProjectLocation location, MachineRegistry registry, HarnessRuntime runtime, string? globalConfigurationPath = null) : IHarnessStateSession, Harness.Core.Memory.IMemorySession, Harness.Core.Tasks.ITaskSession, Harness.Core.Providers.IProviderProjectSession, Harness.Core.Retrieval.IContextProjectSession, Harness.Core.Workspaces.IWorkspaceProjectSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HarnessDatabase? _database;
    private ProjectRegistration? _registration;
    private Task _background = Task.CompletedTask;

    public async Task PutAsync(string key, string value, CancellationToken cancellationToken)
    {
        var database = await StateDatabaseAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        { database.PutDiagnostic(key, value); }
        catch (SqliteException exception) { throw new IOException("store value: " + exception.Message, exception); }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var database = await StateDatabaseAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return database.GetDiagnostic(key);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (SqliteException exception) { throw new IOException("read value: " + exception.Message, exception); }
    }

    public async Task<IReadOnlyDictionary<string, object?>> DiagnosticsAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, object?> result = new(StringComparer.Ordinal)
        {
            ["version"] = runtime.Version,
            ["commit"] = runtime.Commit,
            ["go_version"] = runtime.Runtime,
            ["platform"] = runtime.Platform,
            ["project_root"] = location.Root,
            ["state_root"] = location.StateRoot,
            ["db_path"] = location.DatabasePath,
            ["schema_version"] = 0,
        };
        Add(result, "config_global", location.Configuration.Sources.Global);
        Add(result, "config_project", location.Configuration.Sources.Project);
        GitHead? head = null;
        if (location.Repository is { } repository)
        {
            try
            {
                head = await repository.ReadHeadAsync(cancellationToken);
                Add(result, "git_branch", head.Branch);
                Add(result, "git_commit", head.Commit);
                if (head.Detached)
                {
                    result["git_detached"] = true;
                }

                if (head.Unborn)
                {
                    result["git_unborn"] = true;
                }

                if (repository.Linked)
                {
                    result["git_linked_worktree"] = true;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                result["git_error"] = exception.Message;
            }
        }
        else
        {
            result["git_error"] = "project root is not inside a Git working tree";
        }

        try
        {
            if (!File.Exists(location.DatabasePath))
            {
                throw new FileNotFoundException("project not initialized (run 'harness init'): " + location.DatabasePath);
            }

            var database = await DatabaseAsync(cancellationToken);
            result["schema_version"] = database.SchemaVersion();
            if (_registration is { } registration)
            {
                result["project_id"] = registration.Project.Id;
                result["worktree_id"] = registration.Worktree.Id;
                result["sync"] = ReadSync(database, registration.Worktree.Id, head, out _);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or ArgumentException)
        {
            result["db_error"] = exception.Message;
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _background;
        _database?.Dispose();
        _gate.Dispose();
    }

    private async Task<HarnessDatabase> DatabaseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_database is not null)
            {
                return _database;
            }

            HarnessDatabase? opened = HarnessDatabase.Open(location.DatabasePath,
                new StorageOptions { BusyTimeout = TimeSpan.FromMilliseconds(location.Configuration.Value.Storage.BusyTimeoutMs) }, cancellationToken);
            try
            {
                if (location.Repository is { } repository)
                {
                    var head = await repository.ReadHeadAsync(cancellationToken);
                    var registration = ProjectStore.Register(opened, repository, head, cancellationToken);
                    var worktrees = ProjectStore.Worktrees(opened);
                    _registration = registration;
                    _background = NoteMachineAsync(registration.Project, worktrees);
                }

                _database = opened;
                opened = null;
                return _database;
            }
            finally
            {
                opened?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HarnessDatabase> StateDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        { return await DatabaseAsync(cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or ArgumentException)
        {
            throw new IOException("storage unavailable: " + exception.Message, exception);
        }
    }

    private async Task NoteMachineAsync(ProjectIdentity project, IReadOnlyList<WorktreeIdentity> worktrees)
    {
        await Task.Yield(); // Keep best-effort machine bookkeeping off the first storage request.
        try
        {
            await registry.NoteMcpAsync(project, worktrees, runtime.Version);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // The machine registry is optional and must not fail tool calls or disconnect cleanup.
        }
    }

    private static void Add(Dictionary<string, object?> result, string key, string value)
    {
        if (value.Length > 0)
        {
            result[key] = value;
        }
    }

    private static Dictionary<string, object?> ReadSync(HarnessDatabase database, string worktree, GitHead? head, out bool crossBranch, bool includeRecentRuns = true)
    {
        // Keep facade reads cheap and use the same freshness rules as the synchronization engine.
        crossBranch = false;
        Dictionary<string, object?> result = new(StringComparer.Ordinal);
        if (includeRecentRuns)
        {
            result["recent_runs"] = RecentRuns(database, worktree);
        }

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT branch, head_commit, detached, synced_at FROM sync_state WHERE worktree_id=$worktree";
        command.Parameters.AddWithValue("$worktree", worktree);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            result["stale"] = "this worktree has never been synced; provider indexes may not reflect it (run 'harness sync')";
            return result;
        }

        var branch = reader.GetString(0);
        var commit = reader.GetString(1);
        var detached = reader.GetBoolean(2);
        Add(result, "branch", branch);
        Add(result, "commit", commit);
        Add(result, "synced_at", SecondPrecision(reader.GetString(3)));
        if (head is not null)
        {
            var previous = new SyncState(worktree, branch, commit, detached, false, 0, reader.GetString(3), [], false);
            crossBranch = SyncFreshness.CrossBranch(previous, head.Branch, head.Detached);
            Add(result, "stale", SyncFreshness.Staleness(previous, head.Branch, head.Commit, head.Detached));
        }

        return result;
    }

    private static string SecondPrecision(string value)
    {
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return value;
        }

        var offset = value.AsSpan(dot + 1).IndexOfAny('Z', '+', '-');
        return offset < 0 ? value : value[..dot] + value[(dot + 1 + offset)..];
    }

    private static List<Dictionary<string, object?>> RecentRuns(HarnessDatabase database, string worktree)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, started_at, finished_at, mode, reason, base_commit, head_commit, added, changed, removed, providers, status, error FROM sync_runs WHERE worktree_id=$worktree ORDER BY id DESC LIMIT 5";
        command.Parameters.AddWithValue("$worktree", worktree);
        using var reader = command.ExecuteReader();
        List<Dictionary<string, object?>> runs = [];
        while (reader.Read())
        {
            Dictionary<string, object?> run = new(StringComparer.Ordinal)
            {
                ["id"] = reader.GetInt64(0),
                ["started_at"] = JsonTime(reader.GetString(1)),
                ["mode"] = reader.GetString(3),
                ["added"] = reader.GetInt64(7),
                ["changed"] = reader.GetInt64(8),
                ["removed"] = reader.GetInt64(9),
                ["status"] = reader.GetString(11),
            };
            foreach (var (column, key) in new[] { (2, "finished_at"), (4, "reason"), (5, "base_commit"), (6, "head_commit"), (12, "error") })
            {
                if (!reader.IsDBNull(column))
                {
                    Add(run, key, column == 2 ? JsonTime(reader.GetString(column)) : reader.GetString(column));
                }
            }

            using var providers = System.Text.Json.JsonDocument.Parse(reader.GetString(10));
            run["providers"] = providers.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? providers.RootElement.Clone() : (object)Array.Empty<object>();
            runs.Add(run);
        }

        return runs;
    }

    private static string JsonTime(string value)
    {
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return value;
        }

        var offset = value.AsSpan(dot + 1).IndexOfAny('Z', '+', '-');
        if (offset < 0)
        {
            return value;
        }

        var suffix = dot + 1 + offset;
        var fraction = value[(dot + 1)..suffix].TrimEnd('0');
        return value[..dot] + (fraction.Length == 0 ? "" : "." + fraction) + value[suffix..];
    }
}
