namespace Harness.Core.Retrieval;

public sealed record ContextProject(RetrievalSources Sources, ContextBodyReader? ReadBody = null, Func<string, bool>? IsDirectory = null,
    string Stale = "", bool CrossBranch = false, IReadOnlyList<string>? Notes = null)
{
    public Func<CancellationToken, Task<IReadOnlyList<Harness.Core.Findings.FindingContextGroup>>>? ReadFindings { get; init; }
    public IReadOnlyList<Harness.Core.Findings.FindingContextGroup> Findings { get; init; } = [];
}

public interface IContextProjectSession
{
    Task<ContextProject> ReadContextProjectAsync(CancellationToken cancellationToken);
}
