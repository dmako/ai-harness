namespace Harness.Scm;

/// <summary>An SCM implementation (GitLab, Forgejo, GitHub): creates clients bound to one connection.</summary>
public interface IScmProvider
{
    ScmKind Kind { get; }

    /// <summary>A client calling the connection's server with its token. Dispose it when done.</summary>
    IScmClient Connect(ScmConnection connection);
}

/// <summary>
/// Calls one SCM server with one token. <c>project</c> is the full path the API identifies a project by (group/sub/repo on
/// GitLab, owner/repo on Forgejo) and <c>number</c> a merge request's project-scoped number (GitLab iid, Forgejo index).
/// Failures throw <see cref="ScmException" />, whose message never contains the token.
/// </summary>
public interface IScmClient : IDisposable
{
    ScmConnection Connection { get; }

    /// <summary>Checks the server is the configured kind and accepts the token; never throws for a failed check.</summary>
    Task<ScmConnectionTest> TestConnectionAsync(CancellationToken cancellationToken);

    /// <summary>Projects the token's user can access, optionally filtered by name.</summary>
    Task<IReadOnlyList<ScmProject>> ListProjectsAsync(
        string? search,
        ScmVisibility visibility,
        CancellationToken cancellationToken
    );

    Task<ScmProject> GetProjectAsync(string project, CancellationToken cancellationToken);

    Task<IReadOnlyList<MergeRequest>> ListMergeRequestsAsync(
        string project,
        MergeRequestState state,
        CancellationToken cancellationToken
    );

    Task<MergeRequest> GetMergeRequestAsync(string project, long number, CancellationToken cancellationToken);

    /// <summary>General and diff comments, oldest first; server-generated notes are marked <see cref="ScmComment.System" />.</summary>
    Task<IReadOnlyList<ScmComment>> ListCommentsAsync(string project, long number, CancellationToken cancellationToken);

    Task<ScmComment> AddCommentAsync(string project, long number, string body, CancellationToken cancellationToken);

    Task<ScmComment> AddDiffCommentAsync(
        string project,
        long number,
        ScmDiffComment comment,
        CancellationToken cancellationToken
    );

    /// <summary>The merge request's changes, within <paramref name="maxBytes" /> of diff text.</summary>
    Task<ScmMergeRequestDiff> GetDiffAsync(
        string project,
        long number,
        int maxBytes,
        CancellationToken cancellationToken
    );

    Task<MergeRequest> SetTitleAsync(string project, long number, string title, CancellationToken cancellationToken);

    Task<MergeRequest> SetDescriptionAsync(
        string project,
        long number,
        string description,
        CancellationToken cancellationToken
    );

    Task<ScmApproval> ApproveAsync(string project, long number, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a merge request, then assigns it and requests reviews as asked. Unknown usernames, a missing branch, a
    /// merge request into itself and an already open merge request for the same branches are refused as
    /// <see cref="ScmErrorKind.Invalid" /> (nothing is created); an assignee or reviewer the server refuses after the
    /// merge request was created becomes a warning of the result.
    /// </summary>
    Task<ScmCreatedMergeRequest> CreateMergeRequestAsync(
        string project,
        ScmNewMergeRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>Bounds every provider applies to its calls.</summary>
/// <param name="Timeout">Per-request timeout.</param>
/// <param name="MaxPages">Most pages a list call follows.</param>
/// <param name="PageSize">Items per page requested.</param>
/// <param name="MaxRetryWait">Longest Retry-After harness waits once before a rate-limited call fails.</param>
public sealed record ScmLimits(TimeSpan Timeout, int MaxPages, int PageSize, TimeSpan MaxRetryWait)
{
    public static ScmLimits Default { get; } = new(TimeSpan.FromSeconds(30), 10, 100, TimeSpan.FromSeconds(10));
}

/// <summary>The providers harness knows, keyed by kind.</summary>
public sealed class ScmProviderRegistry
{
    private readonly Dictionary<ScmKind, IScmProvider> _providers;

    public ScmProviderRegistry(IEnumerable<IScmProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = [];
        var duplicate = providers.FirstOrDefault(provider => !_providers.TryAdd(provider.Kind, provider));
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Two providers for {ScmKinds.Name(duplicate.Kind)} were registered.",
                nameof(providers)
            );
        }
    }

    public IReadOnlyCollection<ScmKind> Kinds => _providers.Keys;

    public IScmProvider For(ScmKind kind) =>
        _providers.TryGetValue(kind, out var provider)
            ? provider
            : throw new InvalidOperationException($"No SCM provider for {ScmKinds.Name(kind)} is available.");

    public IScmClient Connect(ScmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return For(connection.Endpoint.Kind).Connect(connection);
    }
}
