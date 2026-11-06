using System.Collections.Concurrent;
using Harness.Core.Architecture;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Architecture;

namespace Harness.Infrastructure.Workspaces;

/// <summary>
/// A reader shared by every request that reads the same members (harness ui keeps one per member set while their
/// HEADs and working trees are unchanged, TASK-0093): what it read once (file lists, text, project evaluations,
/// builds, declarations, the dependency graph, the duplicate index) serves every view after it. Requests take
/// turns through <see cref="ExclusiveAsync{T}"/>, so each piece is computed once however many arrive together;
/// <see cref="WarmAsync"/> reads the members side by side first.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The gate never creates its wait handle (only WaitAsync is used), so it holds nothing to release; a shared reader is dropped by the cache while requests may still hold it."
)]
public sealed partial class WorkspaceEvidenceReader
{
    private const int Readers = 4;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ProjectBuild?> _builds = new(StringComparer.Ordinal);
    private WorkspaceDependencyGraph? _dependencies;
    private WorkspaceDependencyGraph? _summary;
    private WorkspaceStackSummary? _workspaceStack;

    /// <summary>A reader that outlives one request: its repositories ignore the opening request's token.</summary>
    public WorkspaceEvidenceReader(IReadOnlyList<WorkspaceMember> members, bool shared)
        : this(members) => Shared = shared;

    public bool Shared { get; }

    public IReadOnlyList<WorkspaceMember> Members => members;

    /// <summary>The members' duplicate index once read (see <see cref="WorkspaceDuplicateReader.ReadAsync"/>).</summary>
    internal WorkspaceDuplicates? Duplicates { get; set; }

    /// <summary>Runs work while no other request uses this reader.</summary>
    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> work, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads every member side by side (at most four at once): its file list, project evaluations and build,
    /// and with <paramref name="sources"/> its code files and declarations. Each member's reads stay on one
    /// thread, so its repository is never used by two at once; a member that cannot be read is left for the
    /// views to report as they do.
    /// </summary>
    public Task WarmAsync(bool sources, CancellationToken token) =>
        Parallel.ForEachAsync(
            members,
            new ParallelOptions { MaxDegreeOfParallelism = Readers, CancellationToken = token },
            async (member, cancel) =>
            {
                try
                {
                    var (repository, _) = await RepositoryAsync(member, cancel).ConfigureAwait(false);
                    await StackAsync(member, cancel).ConfigureAwait(false);
                    if (sources)
                    {
                        Declarations(member, repository);
                    }
                }
                catch (Exception error) when (ReadError(error))
                { /* The view that needs this member says what could not be read. */
                }
            }
        );

    /// <summary>A member's build model, read once.</summary>
    internal async Task<ProjectBuild?> BuildAsync(WorkspaceMember member, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_builds.TryGetValue(member.Id, out var build))
        {
            return build;
        }

        var (repository, ecosystem) = await RepositoryAsync(member, token).ConfigureAwait(false);
        return _builds.GetOrAdd(member.Id, new BuildReader(repository, ecosystem).Read());
    }

    public async Task<WorkspaceDependencyGraph> DependenciesAsync(CancellationToken token) =>
        _dependencies ??= await ReadDependenciesAsync(token).ConfigureAwait(false);

    public async Task<WorkspaceDependencyGraph> DependencySummaryAsync(CancellationToken token) =>
        _summary ??= await ReadDependencySummaryAsync(token).ConfigureAwait(false);

    public async Task<WorkspaceStackSummary> WorkspaceStackAsync(CancellationToken token) =>
        _workspaceStack ??= await ReadWorkspaceStackAsync(token).ConfigureAwait(false);
}
