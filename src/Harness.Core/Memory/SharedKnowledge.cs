namespace Harness.Core.Memory;

/// <summary>A memory the team shares through Git, as .harness/knowledge.toml holds it.</summary>
public sealed record SharedMemory(
    string Id,
    string Type,
    string Content,
    MemoryScope Scope,
    string SourceRef = "",
    string SharedBy = "",
    string SharedAt = ""
);

/// <summary>A problem with the shared knowledge file, at its 1-based line (0 for the whole file).</summary>
public sealed record SharedKnowledgeProblem(int Line, string Message);

/// <summary>
/// The shared knowledge file as read: its valid memories, what was wrong with the rest, and the hash of its bytes
/// (empty when there is no file).
/// </summary>
public sealed record SharedKnowledge(
    IReadOnlyList<SharedMemory> Memories,
    IReadOnlyList<SharedKnowledgeProblem> Problems,
    string Hash
)
{
    public static SharedKnowledge Empty { get; } = new([], [], "");
}

/// <summary>Whether a memory is the team's: local, in the project's shared knowledge, or removed from it by a Git change.</summary>
public static class SharedState
{
    public const string Local = "";
    public const string Project = "project";
    public const string Removed = "removed";
}
