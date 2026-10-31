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
}
