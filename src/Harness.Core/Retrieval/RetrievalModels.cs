using System.Globalization;
using System.Text;
using Harness.Core.Memory;
using Harness.Core.Providers;

namespace Harness.Core.Retrieval;

public sealed record Provenance(string Provider, string Tool, string Capability, int Rank = 0);

public sealed record Candidate
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string Role { get; init; } = "";
    public string Name { get; init; } = "";
    public string QualifiedName { get; init; } = "";
    public string Path { get; init; } = "";
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string Text { get; init; } = "";
    public long Size { get; init; }
    public bool SizeEstimated { get; init; }
    public int Depth { get; init; }
    public Dictionary<string, double> Scores { get; init; } = new(StringComparer.Ordinal);
    public string Evidence { get; init; } = "";
    public string Freshness { get; init; } = "";
    public MemoryTimestamp? When { get; init; }
    public IReadOnlyList<Provenance> Provenance { get; init; } = [];
    public string MemoryScope { get; init; } = "";
    public IReadOnlyList<string> Overrides { get; init; } = [];
    public int Sources => Provenance.Select(source => source.Provider).Distinct(StringComparer.Ordinal).Count();

    public static Candidate FromItem(CapabilityInfo info, int rank, ProviderItem item)
    {
        var role = info.Capability switch
        {
            Capabilities.SymbolSearch => "definition",
            Capabilities.TextSearch => "match",
            Capabilities.FileSearch => "file",
            Capabilities.Callers => "caller",
            Capabilities.References => "reference",
            Capabilities.Impact => "impact",
            Capabilities.Flow => "flow",
            Capabilities.Context => "context",
            _ => info.Capability,
        };
        var text = item.Snippet ?? "";
        var estimated = text.Length is 0 && item.StartLine > 0 && item.EndLine >= item.StartLine;
        return new()
        {
            Id = CodeId(item, info.Capability),
            Source = "code",
            Role = role,
            Name = item.Name ?? "",
            QualifiedName = item.QualifiedName ?? "",
            Path = item.Path ?? "",
            StartLine = item.StartLine,
            EndLine = item.EndLine,
            Text = text,
            Depth = item.Depth,
            Size = estimated ? ((long)item.EndLine - item.StartLine + 1) * 45 : Encoding.UTF8.GetByteCount(text),
            SizeEstimated = estimated,
            Scores = new(StringComparer.Ordinal) { ["rank"] = 1d / (1 + rank) },
            Evidence = info.EvidenceOf(),
            Freshness = info.Freshness.Length is 0 ? "live" : info.Freshness,
            Provenance = [new(item.Provider, item.Tool, info.Capability, rank)],
        };
    }

    public static Candidate FromMemory(MemoryRecord memory, double scopeScore) => new()
    {
        Id = "memory:" + memory.Id,
        Source = "memory",
        Role = memory.Type,
        Text = memory.Content,
        Size = Encoding.UTF8.GetByteCount(memory.Content),
        Scores = new(StringComparer.Ordinal) { ["scope"] = scopeScore },
        Evidence = "exact",
        Freshness = "live",
        When = memory.CreatedAt,
        Path = memory.Scope.Kind is "path" ? memory.Scope.Value : "",
        MemoryScope = memory.Scope.Wider ? memory.Scope.ToString() : "",
        Overrides = memory.Overrides.ToArray(),
        Provenance = [new("harness", "memory", "memory")],
    };

    public static Candidate FromCommit(RetrievalCommit commit, string path, double recency) => new()
    {
        Id = "git:" + commit.Id + ":" + path,
        Source = "git",
        Role = "change",
        Name = commit.Id[..Math.Min(10, commit.Id.Length)],
        Path = path,
        Text = commit.Subject,
        Size = Encoding.UTF8.GetByteCount(commit.Subject),
        Scores = new(StringComparer.Ordinal) { ["recency"] = recency },
        Evidence = "exact",
        Freshness = "live",
        When = commit.When,
        Provenance = [new("harness", "git", "git")],
    };

    private static string CodeId(ProviderItem item, string capability)
    {
        var name = item.Name ?? "";
        var split = name.LastIndexOfAny(['.', ':']);
        if (split >= 0)
            name = name[(split + 1)..];
        return (capability, item.Path ?? "", name) switch
        {
            (Capabilities.Callers or Capabilities.Impact or Capabilities.Flow, { Length: > 0 } path, { Length: > 0 }) => "code:" + path + "#" + name,
            (_, "", _) => "code:" + item.QualifiedName + "#" + name,
            (_, var path, _) when item.StartLine > 0 => "code:" + path + ":" + item.StartLine.ToString(CultureInfo.InvariantCulture),
            (_, var path, _) => "code:" + path + "#" + name,
        };
    }
}

public sealed record RetrievalCommit(string Id, string Subject, MemoryTimestamp When, IReadOnlyList<string> Paths);
public sealed record RetrievalTask(string Text, string Path = "", int Limit = 10, TimeSpan CodeTimeout = default);
public sealed record RetrievalSources(IProviderRegistry? Registry = null,
    Func<MemoryQuery, CancellationToken, Task<RetrievalMemories>>? Memory = null,
    Func<IReadOnlyList<string>, int, CancellationToken, Task<IReadOnlyList<RetrievalCommit>>>? RecentChanges = null, string Branch = "");
public sealed record RetrievalMemories(IReadOnlyList<MemoryRecord> Memories)
{
    public IReadOnlyList<string> Errors { get; init; } = [];
}
public sealed record RetrievalCall(string Provider, string Capability, string Query, int Items, long LatencyMs, int Bytes, string Error = "");
public sealed record Gathered(IReadOnlyList<Candidate> Candidates, IReadOnlyList<string> Symbols, IReadOnlyList<string> Phrases,
    IReadOnlyList<string> Errors, IReadOnlyList<string> Notes, IReadOnlyList<RetrievalCall> Calls);
public sealed record Folded(Candidate Candidate, string Into, string Reason);
public sealed record Merged(IReadOnlyList<Candidate> Candidates, IReadOnlyList<Folded> Folded);
public sealed record Ranked(Candidate Candidate, int Tier, double Score, IReadOnlyList<string> Why)
{
    public string Explain() => "tier " + Tier.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", Why);
}
