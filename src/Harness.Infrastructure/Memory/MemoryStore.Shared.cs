using Harness.Core.Memory;

namespace Harness.Infrastructure.Memory;

/// <summary>Which memories are the team's (shared through .harness/knowledge.toml) and the file as last read.</summary>
public sealed partial class MemoryStore
{
    /// <summary>The memories that are not local, by id, with their state (project or removed).</summary>
    public IReadOnlyDictionary<string, string> SharedStates(CancellationToken cancellationToken = default) =>
        database.ReadTransaction(
            (connection, transaction) =>
            {
                using var command = Command(
                    connection,
                    transaction,
                    "SELECT id, shared FROM memories WHERE shared <> '' ORDER BY id"
                );
                using var reader = command.ExecuteReader();
                Dictionary<string, string> states = new(StringComparer.Ordinal);
                while (reader.Read())
                {
                    states[reader.GetString(0)] = reader.GetString(1);
                }

                return (IReadOnlyDictionary<string, string>)states;
            },
            cancellationToken
        );

    /// <summary>Marks a memory local, project or removed.</summary>
    public void SetShared(string id, string state, CancellationToken cancellationToken = default)
    {
        if (state is not (SharedState.Local or SharedState.Project or SharedState.Removed))
        {
            throw new ArgumentException($"unknown shared state \"{state}\"");
        }

        database.WithTransaction(
            (connection, transaction) =>
            {
                using var command = Command(
                    connection,
                    transaction,
                    "UPDATE memories SET shared = $state WHERE id = $id",
                    ("$state", state),
                    ("$id", id)
                );
                return command.ExecuteNonQuery() is 0 ? throw new KeyNotFoundException($"{id}: no such memory") : 0;
            },
            cancellationToken
        );
    }

    /// <summary>The hash of the shared knowledge file at this path as last read; empty when never read.</summary>
    public string KnowledgeFileHash(string path, CancellationToken cancellationToken = default) =>
        database.ReadTransaction(
            (connection, transaction) =>
            {
                using var command = Command(
                    connection,
                    transaction,
                    "SELECT hash FROM knowledge_files WHERE path = $path",
                    ("$path", Path.GetFullPath(path))
                );
                return command.ExecuteScalar() as string ?? "";
            },
            cancellationToken
        );

    /// <summary>Records the shared knowledge file at this path as read with this hash.</summary>
    public void RecordKnowledgeFile(string path, string hash, CancellationToken cancellationToken = default) =>
        database.WithTransaction(
            (connection, transaction) =>
            {
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO knowledge_files (path, hash, read_at) VALUES ($path, $hash, $at)
                    ON CONFLICT(path) DO UPDATE SET hash = excluded.hash, read_at = excluded.read_at
                    """,
                    ("$path", Path.GetFullPath(path)),
                    ("$hash", hash),
                    ("$at", Harness.Infrastructure.Storage.HarnessDatabase.FormatTime(_time.GetUtcNow()))
                );
                return 0;
            },
            cancellationToken
        );
}
