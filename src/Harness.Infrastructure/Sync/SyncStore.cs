using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Harness.Core.Sync;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Sync;

public sealed record SyncRunRecord(long Id, string StartedAt, string? FinishedAt, string Mode, string Reason, string BaseCommit, string HeadCommit,
    int Added, int Changed, int Removed, IReadOnlyList<ProviderOutcome> Providers, string Status, string Error);

public static class SyncStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    public static SyncState? LoadState(HarnessDatabase database, string worktree)
    {
        using var connection = database.OpenConnection();
        using var command = Command(connection, null, "SELECT branch, head_commit, detached, dirty, dirty_paths, synced_at, pending, providers_stale FROM sync_state WHERE worktree_id=$wt", ("$wt", worktree));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        IReadOnlyList<string> pending;
        try
        { pending = JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? []; }
        catch (JsonException) { pending = []; }
        return new(worktree, reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetInt32(4), reader.GetString(5), pending, reader.GetBoolean(7));
    }

    public static void SaveState(HarnessDatabase database, SyncState state, CancellationToken token = default) => database.WithTransaction((connection, transaction) =>
    {
        using var command = Command(connection, transaction, """
            INSERT INTO sync_state(worktree_id,branch,head_commit,detached,dirty,dirty_paths,synced_at,pending,providers_stale)
            VALUES($wt,$branch,$head,$detached,$dirty,$count,$at,$pending,$stale)
            ON CONFLICT(worktree_id) DO UPDATE SET branch=excluded.branch,head_commit=excluded.head_commit,detached=excluded.detached,
                dirty=excluded.dirty,dirty_paths=excluded.dirty_paths,synced_at=excluded.synced_at,pending=excluded.pending,providers_stale=excluded.providers_stale
            """, ("$wt", state.WorktreeId), ("$branch", state.Branch), ("$head", state.Commit), ("$detached", state.Detached), ("$dirty", state.Dirty),
            ("$count", state.DirtyPaths), ("$at", state.SyncedAt), ("$pending", JsonSerializer.Serialize(state.Pending)), ("$stale", state.ProvidersStale));
        return command.ExecuteNonQuery();
    }, token);

    public static IReadOnlyDictionary<string, string> FileHashes(HarnessDatabase database, string worktree)
    {
        using var connection = database.OpenConnection();
        using var command = Command(connection, null, "SELECT path,hash FROM files WHERE worktree_id=$wt ORDER BY path", ("$wt", worktree));
        using var reader = command.ExecuteReader();
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            hashes.Add(reader.GetString(0), reader.GetString(1));
        }

        return hashes;
    }

    public static IReadOnlyList<SyncRunRecord> RecentRuns(HarnessDatabase database, string worktree, int count = 5)
    {
        using var connection = database.OpenConnection();
        using var command = Command(connection, null, "SELECT id,started_at,finished_at,mode,reason,base_commit,head_commit,added,changed,removed,providers,status,error FROM sync_runs WHERE worktree_id=$wt ORDER BY id DESC LIMIT $n", ("$wt", worktree), ("$n", count));
        using var reader = command.ExecuteReader();
        List<SyncRunRecord> records = [];
        while (reader.Read())
        {
            records.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), Text(reader, 4), Text(reader, 5), Text(reader, 6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), JsonSerializer.Deserialize<ProviderOutcome[]>(reader.GetString(10), JsonOptions) ?? [], reader.GetString(11), Text(reader, 12)));
        }

        return records;
    }

    internal static long StartRun(HarnessDatabase database, string worktree, string mode, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        using var command = Command(connection, null, "INSERT INTO sync_runs(worktree_id,started_at,mode,status) VALUES($wt,$at,$mode,'running'); SELECT last_insert_rowid();", ("$wt", worktree), ("$at", HarnessDatabase.FormatTime(now)), ("$mode", mode));
        return (long)command.ExecuteScalar()!;
    }

    internal static void FinishRun(HarnessDatabase database, SyncRun run, Exception? error, DateTimeOffset now)
    {
        using var connection = database.OpenConnection();
        var status = (error, run.Providers.Any(provider => provider.Status is "failed")) switch { (not null, _) => "failed", (_, true) => "partial", _ => "ok" };
        var providers = run.Providers.Select(provider =>
        {
            var item = new Dictionary<string, string> { ["name"] = provider.Name, ["status"] = provider.Status };
            if (provider.Detail.Length > 0)
            {
                item["detail"] = provider.Detail;
            }

            return item;
        });
        using var command = Command(connection, null, """
            UPDATE sync_runs SET finished_at=$at,mode=$mode,reason=$reason,base_commit=$base,head_commit=$head,
                added=$added,changed=$changed,removed=$removed,unchanged=$unchanged,hashed=$hashed,providers=$providers,status=$status,error=$error WHERE id=$id
            """, ("$at", HarnessDatabase.FormatTime(now)), ("$mode", run.Mode), ("$reason", Nullable(run.Reason)), ("$base", Nullable(run.Base)), ("$head", Nullable(run.Head)),
            ("$added", run.Files.Added.Count), ("$changed", run.Files.Changed.Count), ("$removed", run.Files.Removed.Count), ("$unchanged", run.Files.Unchanged), ("$hashed", run.Files.Hashed),
            ("$providers", JsonSerializer.Serialize(providers)), ("$status", status), ("$error", error is null ? DBNull.Value : Core.Memory.CredentialRedactor.Redact(error.Message)), ("$id", run.RunId));
        command.ExecuteNonQuery();
    }

    internal static long SyncedSince(HarnessDatabase database, string worktree, DateTimeOffset started, string head)
    {
        using var connection = database.OpenConnection();
        using var command = Command(connection, null, "SELECT id FROM sync_runs WHERE worktree_id=$wt AND status='ok' AND started_at > $at AND head_commit IS $head ORDER BY id DESC LIMIT 1", ("$wt", worktree), ("$at", HarnessDatabase.FormatTime(started)), ("$head", Nullable(head)));
        return command.ExecuteScalar() is long id ? id : 0;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only internal constant SQL callers; every data value is bound.")]
    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
    private static object Nullable(string value) => value.Length is 0 ? DBNull.Value : value;
    private static string Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? "" : reader.GetString(index);
}
