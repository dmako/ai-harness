using System.Text;
using Harness.Core.Memory;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Memory;

/// <summary>
/// Brings the team's shared knowledge in after a Git change: on sync and before knowledge reads. An unchanged file
/// costs one hash comparison; only a changed one is parsed and merged.
/// </summary>
public static class SharedKnowledgeSync
{
    /// <summary>The worktree's shared knowledge file, .harness/knowledge.toml under its root.</summary>
    public static string PathIn(string worktreeRoot) => KnowledgeFile.In(Path.Combine(worktreeRoot, ".harness"));

    /// <summary>Merges the file when it changed since it was last read; null when it did not.</summary>
    public static SharedKnowledgeMerge? Refresh(
        HarnessDatabase database,
        string worktreeRoot,
        CancellationToken cancellationToken = default
    )
    {
        var path = PathIn(worktreeRoot);
        byte[]? bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            bytes = null;
        }

        var hash = bytes is null ? "" : KnowledgeFile.Hash(bytes);
        var store = new MemoryStore(database);
        if (string.Equals(hash, store.KnowledgeFileHash(path, cancellationToken), StringComparison.Ordinal))
        {
            return null;
        }

        var file = bytes is null
            ? SharedKnowledge.Empty
            : KnowledgeFile.Parse(new UTF8Encoding(false).GetString(bytes)) with
            {
                Hash = hash,
            };
        return store.MergeShared(path, file, cancellationToken);
    }

    /// <summary>
    /// Shares a current project memory with the team: writes its entry to the file at once and marks it PROJECT.
    /// Committing, pushing and review stay with the normal Git flow.
    /// </summary>
    public static MemoryRecord Share(
        HarnessDatabase database,
        string worktreeRoot,
        string id,
        string by,
        CancellationToken cancellationToken = default
    )
    {
        Refresh(database, worktreeRoot, cancellationToken);
        var store = new MemoryStore(database);
        var memory = store.Get(id, cancellationToken);
        if (!memory.Current(Now()))
        {
            throw new InvalidOperationException($"{id} is not current (superseded or retired): share the current one");
        }

        if (store.SharedStates(cancellationToken).GetValueOrDefault(id) is SharedState.Project)
        {
            throw new InvalidOperationException($"{id} is already shared with the project");
        }

        var path = PathIn(worktreeRoot);
        var file = Readable(path);
        Write(
            store,
            path,
            [
                .. file.Memories.Where(entry => entry.Id != id),
                new(id, memory.Type, memory.Content, memory.Scope, memory.Source.Ref, by, Now().ToString()),
            ],
            cancellationToken
        );
        store.MarkShared(id, shared: true, by, cancellationToken);
        return store.Get(id, cancellationToken);
    }

    /// <summary>Withdraws a memory from the team's knowledge: removes its entry and keeps it as a local memory.</summary>
    public static MemoryRecord Withdraw(
        HarnessDatabase database,
        string worktreeRoot,
        string id,
        string by,
        CancellationToken cancellationToken = default
    )
    {
        Refresh(database, worktreeRoot, cancellationToken);
        var store = new MemoryStore(database);
        _ = store.Get(id, cancellationToken);
        var path = PathIn(worktreeRoot);
        var file = Readable(path);
        var entry =
            file.Memories.FirstOrDefault(entry => store.Latest(entry.Id, cancellationToken)?.Id == id)
            ?? throw new InvalidOperationException($"{id} is not shared with the project");
        Write(store, path, file.Memories.Where(other => other.Id != entry.Id), cancellationToken);
        store.MarkShared(id, shared: false, by, cancellationToken);
        return store.Get(id, cancellationToken);
    }

    /// <summary>
    /// Keeps the file in step with local edits after a knowledge write: an entry whose memory was corrected here
    /// takes the new statement (teammates' copies are superseded on their next sync), one whose memory was retired
    /// leaves the file. A file changed by Git is merged first; one with problems is left alone.
    /// </summary>
    public static void Publish(HarnessDatabase database, string worktreeRoot, CancellationToken cancellationToken)
    {
        Refresh(database, worktreeRoot, cancellationToken);
        var path = PathIn(worktreeRoot);
        if (!File.Exists(path))
        {
            return;
        }

        var file = KnowledgeFile.Read(path);
        if (file.Problems.Count is not 0)
        {
            return;
        }

        var store = new MemoryStore(database);
        var now = Now();
        List<SharedMemory> entries = [];
        List<string> shared = [];
        foreach (var entry in file.Memories)
        {
            if (store.Latest(entry.Id, cancellationToken) is not { } memory)
            {
                entries.Add(entry);
                continue;
            }

            if (!memory.Current(now))
            {
                continue;
            }

            shared.Add(memory.Id);
            entries.Add(entry with { Type = memory.Type, Content = memory.Content, Scope = memory.Scope });
        }

        if (!entries.SequenceEqual(file.Memories))
        {
            Write(store, path, entries, cancellationToken);
        }

        store.MarkProject(shared, cancellationToken);
    }

    private static SharedKnowledge Readable(string path)
    {
        var file = KnowledgeFile.Read(path);
        return file.Problems.Count is 0
            ? file
            : throw new InvalidOperationException(
                $"fix {Path.Combine(".harness", KnowledgeFile.FileName)} first: "
                    + string.Join(
                        "; ",
                        file.Problems.Select(problem =>
                            (problem.Line > 0 ? "line " + problem.Line + ": " : "") + problem.Message
                        )
                    )
            );
    }

    /// <summary>Writes the file and records it as read, so the next refresh does not merge our own change.</summary>
    private static void Write(
        MemoryStore store,
        string path,
        IEnumerable<SharedMemory> entries,
        CancellationToken cancellationToken
    )
    {
        KnowledgeFile.Write(path, entries);
        store.RecordKnowledgeFile(path, KnowledgeFile.Hash(File.ReadAllBytes(path)), cancellationToken);
    }

    private static MemoryTimestamp Now() => new(DateTimeOffset.UtcNow);
}
