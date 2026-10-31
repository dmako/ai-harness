using Harness.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Memory;

/// <summary>Which memories are the team's (shared through .harness/knowledge.toml) and the file as last read.</summary>
public sealed partial class MemoryStore
{
    private const string ProjectKnowledge = "project knowledge";

    /// <summary>The memories that are not local, by id, with their state (project or removed).</summary>
    public IReadOnlyDictionary<string, string> SharedStates(CancellationToken cancellationToken = default) =>
        database.ReadTransaction(
            (connection, transaction) => (IReadOnlyDictionary<string, string>)States(connection, transaction),
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
                RecordKnowledgeFile(connection, transaction, path, hash);
                return 0;
            },
            cancellationToken
        );

    /// <summary>
    /// Merges the shared knowledge file into the project's memories and records it as read. An entry keeps the id
    /// the memory had when it was shared and stands for the current memory along that memory's supersede chain. A
    /// file this harness cannot read changes nothing, and one with bad entries removes nothing.
    /// </summary>
    public SharedKnowledgeMerge MergeShared(
        string path,
        SharedKnowledge file,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(file);
        var merge = new SharedKnowledgeMerge([], [], [], [], [], file.Problems);
        if (file.Problems.Any(problem => problem.Line is 0))
        {
            RecordKnowledgeFile(path, file.Hash, cancellationToken);
            return merge;
        }

        return database.WithTransaction(
            (connection, transaction) =>
            {
                var now = Now();
                var states = States(connection, transaction);
                List<string> added = [];
                List<string> updated = [];
                List<string> linked = [];
                List<string> restored = [];
                HashSet<string> shared = new(StringComparer.Ordinal);
                foreach (var entry in file.Memories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (target, change) = MergeEntry(connection, transaction, entry, now, cancellationToken);
                    if (target is null)
                    {
                        continue;
                    }

                    var list = change switch
                    {
                        "added" => added,
                        "updated" => updated,
                        "linked" => linked,
                        _ => states.GetValueOrDefault(target) is SharedState.Removed ? restored : null,
                    };
                    list?.Add(target);
                    shared.Add(target);
                    Mark(connection, transaction, target, SharedState.Project);
                }

                var removed = file.Problems.Count is 0
                    ? RemoveUnshared(connection, transaction, states, shared, now)
                    : [];
                RecordKnowledgeFile(connection, transaction, path, file.Hash);
                return merge with
                {
                    Added = added,
                    Updated = updated,
                    Linked = linked,
                    Removed = removed,
                    Restored = restored,
                };
            },
            cancellationToken
        );
    }

    /// <summary>Keeps a memory removed from project knowledge as a local one, answering its Inbox item.</summary>
    public MemoryRecord KeepLocal(string id, string by, CancellationToken cancellationToken = default) =>
        database.WithTransaction(
            (connection, transaction) =>
            {
                if (States(connection, transaction).GetValueOrDefault(id) is not SharedState.Removed)
                {
                    throw new InvalidOperationException($"{id} was not removed from project knowledge");
                }

                Mark(connection, transaction, id, SharedState.Local);
                AddEvent(
                    connection,
                    transaction,
                    id,
                    "kept_local",
                    "",
                    by,
                    "removed from project knowledge",
                    Now().ToString()
                );
                return Get(connection, transaction, id);
            },
            cancellationToken
        );

    /// <summary>The memory a shared entry stands for: the last one along the supersede chain from this id.</summary>
    public MemoryRecord? Latest(string id, CancellationToken cancellationToken = default) =>
        database.ReadTransaction(
            (connection, transaction) =>
                Find(connection, transaction, id) is { } memory ? Latest(connection, transaction, memory) : null,
            cancellationToken
        );

    /// <summary>Marks a memory shared with the project or withdrawn from it, recording who did.</summary>
    public void MarkShared(string id, bool shared, string by, CancellationToken cancellationToken = default) =>
        database.WithTransaction(
            (connection, transaction) =>
            {
                _ = Get(connection, transaction, id);
                Mark(connection, transaction, id, shared ? SharedState.Project : SharedState.Local);
                AddEvent(
                    connection,
                    transaction,
                    id,
                    shared ? "shared" : "withdrawn",
                    "",
                    by,
                    KnowledgeFile.FileName,
                    Now().ToString()
                );
                return 0;
            },
            cancellationToken
        );

    /// <summary>
    /// Marks the memories the file's entries stand for as the project's; the earlier ones along their chains, and
    /// project memories no longer current, become local.
    /// </summary>
    public void MarkProject(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default) =>
        database.WithTransaction(
            (connection, transaction) =>
            {
                var now = Now();
                foreach (var (id, state) in States(connection, transaction))
                {
                    if (
                        state is SharedState.Project
                        && !ids.Contains(id)
                        && !Get(connection, transaction, id).Current(now)
                    )
                    {
                        Mark(connection, transaction, id, SharedState.Local);
                    }
                }

                foreach (var id in ids)
                {
                    Mark(connection, transaction, id, SharedState.Project);
                }

                return 0;
            },
            cancellationToken
        );

    private static MemoryRecord Latest(SqliteConnection connection, SqliteTransaction transaction, MemoryRecord memory)
    {
        HashSet<string> seen = new(StringComparer.Ordinal) { memory.Id };
        while (memory.SupersededBy.Length is not 0 && seen.Add(memory.SupersededBy))
        {
            memory = Get(connection, transaction, memory.SupersededBy);
        }

        return memory;
    }

    private (string? Target, string Change) MergeEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SharedMemory entry,
        MemoryTimestamp now,
        CancellationToken cancellationToken
    )
    {
        var input = MemoryText.Validate(
            new MemoryInput
            {
                Type = entry.Type,
                Content = entry.Content,
                Scope = entry.Scope,
                Source = new(
                    "import",
                    entry.SourceRef.Length is 0 ? KnowledgeFile.FileName : entry.SourceRef,
                    entry.SharedBy.Length is 0 ? ProjectKnowledge : entry.SharedBy
                ),
                Force = true,
            }
        );
        if (Find(connection, transaction, entry.Id) is { } known)
        {
            var current = Latest(connection, transaction, known);
            if (current.Type == input.Type && current.Scope == input.Scope && current.Content == input.Content)
            {
                // a memory retired here stays retired while the team's entry is unchanged
                return (current.Current(now) ? current.Id : null, "");
            }

            var replaced = Put(
                connection,
                transaction,
                input with
                {
                    Supersedes = [current.Id],
                    SupersedeReason = "changed in project knowledge",
                },
                cancellationToken
            );
            Mark(connection, transaction, current.Id, SharedState.Local);
            return (replaced.Memory!.Id, replaced.Status is "created" ? "updated" : "");
        }

        var equal = Equal(connection, transaction, input);
        Put(
            connection,
            transaction,
            equal is null
                ? input
                : input with
                {
                    Supersedes = [equal],
                    SupersedeReason = "linked to project knowledge",
                },
            cancellationToken,
            entry.Id
        );
        if (equal is not null)
        {
            Mark(connection, transaction, equal, SharedState.Local);
        }

        return (entry.Id, equal is null ? "added" : "linked");
    }

    /// <summary>Marks the project memories no entry stands for any more removed; they wait in the Inbox.</summary>
    private static List<string> RemoveUnshared(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Dictionary<string, string> states,
        HashSet<string> shared,
        MemoryTimestamp now
    )
    {
        List<string> removed = [];
        foreach (var (id, state) in states)
        {
            if (state is not SharedState.Project || shared.Contains(id))
            {
                continue;
            }

            if (!Get(connection, transaction, id).Current(now))
            {
                Mark(connection, transaction, id, SharedState.Local);
                continue;
            }

            Mark(connection, transaction, id, SharedState.Removed);
            AddEvent(
                connection,
                transaction,
                id,
                "removed_from_project",
                "",
                ProjectKnowledge,
                "no longer in " + KnowledgeFile.FileName,
                now.ToString()
            );
            removed.Add(id);
        }

        return removed;
    }

    private static MemoryRecord? Find(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT " + Columns + " FROM memories m WHERE m.id=$id",
            ("$id", id)
        );
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static string? Equal(SqliteConnection connection, SqliteTransaction transaction, MemoryInput input)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT id FROM memories WHERE type=$type AND scope_kind=$kind AND scope_value=$scope AND content_hash=$hash AND superseded_by IS NULL ORDER BY created_at LIMIT 1",
            ("$type", input.Type),
            ("$kind", input.Scope!.Kind),
            ("$scope", input.Scope.Value),
            ("$hash", MemoryText.Hash(input.Content))
        );
        return command.ExecuteScalar() as string;
    }

    private static Dictionary<string, string> States(SqliteConnection connection, SqliteTransaction transaction)
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

        return states;
    }

    private static void Mark(SqliteConnection connection, SqliteTransaction transaction, string id, string state) =>
        Execute(
            connection,
            transaction,
            "UPDATE memories SET shared = $state WHERE id = $id",
            ("$state", state),
            ("$id", id)
        );

    private void RecordKnowledgeFile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path,
        string hash
    ) =>
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
}
