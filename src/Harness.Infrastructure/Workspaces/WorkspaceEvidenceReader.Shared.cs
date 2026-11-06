using System.Collections.Concurrent;
using Harness.Core.Architecture;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Architecture;

namespace Harness.Infrastructure.Workspaces;

/// <summary>What <see cref="WorkspaceEvidenceReader.WarmAsync"/> reads of each member beside its file list.</summary>
[Flags]
public enum MemberReads
{
    None = 0,

    /// <summary>Its build: projects, references and packages (project evaluation).</summary>
    Builds = 1,

    /// <summary>Its stack (language, runtime, frameworks) and build.</summary>
    Stacks = 2,

    /// <summary>Its code files and declared types.</summary>
    Sources = 4,
}

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

    /// <summary>How many member file lists and builds this reader (and those made <see cref="For"/> it) read.</summary>
    internal ReadCounts Counts { get; private init; } = new();

    internal sealed class ReadCounts
    {
        private int _listed;
        private int _built;
        public int Listed => _listed;
        public int Built => _built;

        public void List() => Interlocked.Increment(ref _listed);

        public void Build() => Interlocked.Increment(ref _built);
    }

    /// <summary>
    /// A reader of <paramref name="subset"/> that starts from what this one read of each member (file lists, text,
    /// builds, declarations, stacks): a sync's workspace findings reuse its project read (TASK-0108). What depends on
    /// the member set (duplicates, dependency graph, stack summary) is read anew.
    /// </summary>
    public WorkspaceEvidenceReader For(IReadOnlyList<WorkspaceMember> subset)
    {
        var reader = new WorkspaceEvidenceReader(subset, Shared) { Counts = Counts };
        foreach (var pair in _repositories)
        {
            reader._repositories.TryAdd(pair.Key, pair.Value);
        }

        foreach (var pair in _builds)
        {
            reader._builds.TryAdd(pair.Key, pair.Value);
        }

        foreach (var pair in _declarations)
        {
            reader._declarations.TryAdd(pair.Key, pair.Value);
        }

        foreach (var pair in _stacks)
        {
            reader._stacks.TryAdd(pair.Key, pair.Value);
        }

        return reader;
    }

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
    /// Reads every member side by side (at most four at once): its file list and what <paramref name="reads"/>
    /// names; with <paramref name="focus"/>, only that member reads all of it and the others their builds (a
    /// project's own view needs no more of its peers). Each member's reads stay on one thread, so its repository
    /// is never used by two at once; a member that cannot be read is left for the views to report as they do.
    /// </summary>
    public Task WarmAsync(MemberReads reads, CancellationToken token, string? focus = null) =>
        Parallel.ForEachAsync(
            members,
            new ParallelOptions { MaxDegreeOfParallelism = Readers, CancellationToken = token },
            async (member, cancel) =>
            {
                var mine = focus is null || member.Id == focus ? reads : PeerReads(reads);
                try
                {
                    var (repository, _) = await RepositoryAsync(member, cancel).ConfigureAwait(false);
                    if (mine.HasFlag(MemberReads.Stacks))
                    {
                        await StackAsync(member, cancel).ConfigureAwait(false);
                    }
                    else if (mine.HasFlag(MemberReads.Builds))
                    {
                        await BuildAsync(member, cancel).ConfigureAwait(false);
                    }

                    if (mine.HasFlag(MemberReads.Sources))
                    {
                        Declarations(member, repository);
                    }
                }
                catch (Exception error) when (ReadError(error))
                { /* The view that needs this member says what could not be read. */
                }
            }
        );

    /// <summary>What a focused view needs of the other members: their builds, when it reads builds or stacks.</summary>
    private static MemberReads PeerReads(MemberReads reads) =>
        (reads & (MemberReads.Builds | MemberReads.Stacks)) is MemberReads.None
            ? MemberReads.None
            : MemberReads.Builds;

    /// <summary>A member's build model, read once.</summary>
    internal async Task<ProjectBuild?> BuildAsync(WorkspaceMember member, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_builds.TryGetValue(member.Id, out var build))
        {
            return build;
        }

        var (repository, ecosystem) = await RepositoryAsync(member, token).ConfigureAwait(false);
        Counts.Build();
        return _builds.GetOrAdd(member.Id, new BuildReader(repository, ecosystem).Read());
    }

    public async Task<WorkspaceDependencyGraph> DependenciesAsync(CancellationToken token) =>
        _dependencies ??= await ReadDependenciesAsync(token).ConfigureAwait(false);

    public async Task<WorkspaceDependencyGraph> DependencySummaryAsync(CancellationToken token) =>
        _summary ??= await ReadDependencySummaryAsync(token).ConfigureAwait(false);

    public async Task<WorkspaceStackSummary> WorkspaceStackAsync(CancellationToken token) =>
        _workspaceStack ??= await ReadWorkspaceStackAsync(token).ConfigureAwait(false);
}
