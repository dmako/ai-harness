using System.Globalization;

namespace Harness.Scm.Forgejo;

/// <summary>Forgejo (and Gitea-compatible servers), REST API v1, authenticated with an access token.</summary>
/// <param name="handler">The HTTP handler; the process-wide shared one when null (tests pass a fake).</param>
public sealed class ForgejoProvider(HttpMessageHandler? handler = null, ScmLimits? limits = null) : IScmProvider
{
    public ScmKind Kind => ScmKind.Forgejo;

    public IScmClient Connect(ScmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Endpoint.Kind is ScmKind.Forgejo
            ? new ForgejoClient(connection, handler ?? ScmHttpClient.SharedHandler, limits ?? ScmLimits.Default)
            : throw new ArgumentException("The connection is not a Forgejo one.", nameof(connection));
    }
}

/// <summary>
/// One Forgejo server and token. Projects are repositories named owner/repo; merge requests are pull requests by
/// index. Approvals and diff comments are pull request reviews.
/// </summary>
internal sealed class ForgejoClient(ScmConnection connection, HttpMessageHandler handler, ScmLimits limits) : IScmClient
{
    private const int MaxDiffText = 20 * 1024 * 1024;

    private readonly ScmHttpClient _http = new(
        connection,
        handler,
        limits,
        headers => headers.TryAddWithoutValidation("Authorization", "token " + connection.Token)
    );

    private static ForgejoJsonContext Json => ForgejoJsonContext.Default;

    public ScmConnection Connection => _http.Connection;

    public async Task<ScmConnectionTest> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = (
                await _http.GetAsync("api/v1/version", Json.FjVersion, cancellationToken).ConfigureAwait(false)
            ).Value;
            if (string.IsNullOrWhiteSpace(version.Version))
            {
                throw new ScmException(
                    ScmErrorKind.WrongServer,
                    $"{Connection.Endpoint.BaseUrl.Host} answered /api/v1/version without a Forgejo version."
                );
            }

            var user = (
                await _http.GetAsync("api/v1/user", Json.FjUser, cancellationToken).ConfigureAwait(false)
            ).Value;
            return ScmConnectionTest.Success(version.Version, User(user));
        }
        catch (ScmException exception)
        {
            return ScmConnectionTest.Failed(
                exception.Kind is ScmErrorKind.NotFound
                    ? new ScmException(
                        ScmErrorKind.WrongServer,
                        $"No Forgejo API v1 at {Connection.Endpoint.BaseUrl}: {exception.Message}",
                        exception.Status,
                        exception
                    )
                    : exception
            );
        }
    }

    public async Task<IReadOnlyList<ScmProject>> ListProjectsAsync(
        string? search,
        ScmVisibility visibility,
        CancellationToken cancellationToken
    )
    {
        var query = string.IsNullOrWhiteSpace(search) ? "" : Uri.EscapeDataString(search.Trim());
        var found = await _http
            .GetPagesAsync(
                (page, size) =>
                    $"api/v1/repos/search?q={query}&private=true&sort=updated&order=desc&limit={size}&page={page}",
                Json.FjSearch,
                result => result.Data,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (query.Length is 0)
        {
            // the user's own and member repositories, which a search ranks below popular public ones
            found.AddRange(
                await _http
                    .GetPagesAsync(
                        (page, size) => $"api/v1/user/repos?limit={size}&page={page}",
                        Json.ListFjRepo,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            );
        }

        return found
            .DistinctBy(repo => repo.Id)
            .Select(Project)
            .Where(project => project.Visibility == visibility)
            .ToList();
    }

    public async Task<ScmProject> GetProjectAsync(string project, CancellationToken cancellationToken) =>
        Project((await _http.GetAsync(RepoPath(project), Json.FjRepo, cancellationToken).ConfigureAwait(false)).Value);

    public async Task<IReadOnlyList<MergeRequest>> ListMergeRequestsAsync(
        string project,
        MergeRequestState state,
        CancellationToken cancellationToken
    )
    {
        var forgejoState = state is MergeRequestState.Opened ? "open" : "closed";
        var pulls = await _http
            .GetPagesAsync(
                (page, size) =>
                    $"{RepoPath(project)}/pulls?state={forgejoState}&sort=recentupdate&limit={size}&page={page}",
                Json.ListFjPull,
                cancellationToken
            )
            .ConfigureAwait(false);
        return pulls.Select(pull => Request(pull, null)).Where(request => request.State == state).ToList();
    }

    public async Task<MergeRequest> GetMergeRequestAsync(
        string project,
        long number,
        CancellationToken cancellationToken
    )
    {
        var pull = await GetPullAsync(project, number, cancellationToken).ConfigureAwait(false);
        var reviews = await ReviewsAsync(project, number, cancellationToken).ConfigureAwait(false);
        return Request(pull, reviews);
    }

    public async Task<IReadOnlyList<ScmComment>> ListCommentsAsync(
        string project,
        long number,
        CancellationToken cancellationToken
    )
    {
        var issueComments = (
            await _http
                .GetAsync(IssuePath(project, number) + "/comments", Json.ListFjComment, cancellationToken)
                .ConfigureAwait(false)
        ).Value;
        List<ScmComment> comments = issueComments.Select(Comment).ToList();
        foreach (var review in await ReviewsAsync(project, number, cancellationToken).ConfigureAwait(false))
        {
            var thread = review.Id.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(review.Body))
            {
                comments.Add(
                    new ScmComment(
                        "review-" + thread,
                        $"[{review.State}] {review.Body}",
                        User(review.User),
                        review.SubmittedAt,
                        false,
                        null,
                        WebUrl(review.HtmlUrl),
                        thread
                    )
                );
            }

            if (review.CommentsCount > 0)
            {
                var inline = (
                    await _http
                        .GetAsync(
                            ReviewPath(project, number, review.Id) + "/comments",
                            Json.ListFjReviewComment,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                ).Value;
                comments.AddRange(inline.Select(comment => DiffComment(comment, thread)));
            }
        }

        return comments.OrderBy(comment => comment.CreatedAt).ToList();
    }

    public async Task<ScmComment> AddCommentAsync(
        string project,
        long number,
        string body,
        CancellationToken cancellationToken
    ) =>
        Comment(
            await _http
                .SendAsync(
                    HttpMethod.Post,
                    IssuePath(project, number) + "/comments",
                    new FjCommentBody(body),
                    Json.FjCommentBody,
                    Json.FjComment,
                    cancellationToken
                )
                .ConfigureAwait(false)
        );

    public async Task<ScmComment> AddDiffCommentAsync(
        string project,
        long number,
        ScmDiffComment comment,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(comment);
        var pull = await GetPullAsync(project, number, cancellationToken).ConfigureAwait(false);
        var files = await FilesAsync(project, number, cancellationToken).ConfigureAwait(false);
        var file =
            files.FirstOrDefault(diff => diff.NewPath == comment.FilePath)
            ?? files.FirstOrDefault(diff => diff.OldPath == comment.FilePath)
            ?? throw new ScmException(
                ScmErrorKind.Invalid,
                $"'{comment.FilePath}' is not changed by pull request #{number}; comment on a file in its diff."
            );
        var line =
            UnifiedDiff.Locate(file.Diff, comment.Line, comment.LineType)
            ?? throw new ScmException(
                ScmErrorKind.Invalid,
                $"Line {comment.Line} ({ScmNames.Name(comment.LineType)}) of '{comment.FilePath}' is not in pull request "
                    + $"#{number}'s diff; comment on a changed line or a context line shown with it."
            );
        var path = comment.LineType is DiffLineType.Old ? file.OldPath ?? file.NewPath : file.NewPath;
        var review = await _http
            .SendAsync(
                HttpMethod.Post,
                RequestPath(project, number) + "/reviews",
                new FjReviewBody(
                    "COMMENT",
                    "",
                    pull.Head?.Sha,
                    [
                        new FjReviewCommentBody(
                            path,
                            comment.Body,
                            comment.LineType is DiffLineType.New ? line.NewLine : null,
                            comment.LineType is DiffLineType.Old ? line.OldLine : null
                        ),
                    ]
                ),
                Json.FjReviewBody,
                Json.FjReview,
                cancellationToken
            )
            .ConfigureAwait(false);
        var created = (
            await _http
                .GetAsync(
                    ReviewPath(project, number, review.Id) + "/comments",
                    Json.ListFjReviewComment,
                    cancellationToken
                )
                .ConfigureAwait(false)
        ).Value;
        var thread = review.Id.ToString(CultureInfo.InvariantCulture);
        return created.Count > 0
            ? DiffComment(created[^1], thread)
            : new ScmComment(
                "review-" + thread,
                comment.Body,
                User(review.User),
                review.SubmittedAt,
                false,
                new ScmDiffPosition(file.NewPath, file.OldPath, line.NewLine, line.OldLine),
                WebUrl(review.HtmlUrl),
                thread
            );
    }

    public async Task<ScmMergeRequestDiff> GetDiffAsync(
        string project,
        long number,
        int maxBytes,
        CancellationToken cancellationToken
    ) => ScmDiffBudget.Apply(await FilesAsync(project, number, cancellationToken).ConfigureAwait(false), maxBytes);

    public Task<MergeRequest> SetTitleAsync(
        string project,
        long number,
        string title,
        CancellationToken cancellationToken
    ) => EditAsync(project, number, new FjPullEdit(title, null), cancellationToken);

    public Task<MergeRequest> SetDescriptionAsync(
        string project,
        long number,
        string description,
        CancellationToken cancellationToken
    ) => EditAsync(project, number, new FjPullEdit(null, description), cancellationToken);

    public async Task<ScmApproval> ApproveAsync(string project, long number, CancellationToken cancellationToken)
    {
        var pull = await GetPullAsync(project, number, cancellationToken).ConfigureAwait(false);
        await _http
            .SendAsync(
                HttpMethod.Post,
                RequestPath(project, number) + "/reviews",
                new FjReviewBody("APPROVED", "", pull.Head?.Sha, null),
                Json.FjReviewBody,
                Json.FjReview,
                cancellationToken
            )
            .ConfigureAwait(false);
        var reviews = await ReviewsAsync(project, number, cancellationToken).ConfigureAwait(false);
        return new ScmApproval(null, ApprovedBy(reviews));
    }

    public void Dispose() => _http.Dispose();

    private async Task<MergeRequest> EditAsync(
        string project,
        long number,
        FjPullEdit edit,
        CancellationToken cancellationToken
    ) =>
        Request(
            await _http
                .SendAsync(
                    HttpMethod.Patch,
                    RequestPath(project, number),
                    edit,
                    Json.FjPullEdit,
                    Json.FjPull,
                    cancellationToken
                )
                .ConfigureAwait(false),
            null
        );

    private async Task<FjPull> GetPullAsync(string project, long number, CancellationToken cancellationToken) =>
        (
            await _http.GetAsync(RequestPath(project, number), Json.FjPull, cancellationToken).ConfigureAwait(false)
        ).Value;

    private Task<List<FjReview>> ReviewsAsync(string project, long number, CancellationToken cancellationToken) =>
        _http.GetPagesAsync(
            (page, size) => $"{RequestPath(project, number)}/reviews?limit={size}&page={page}",
            Json.ListFjReview,
            cancellationToken
        );

    /// <summary>The pull request's changed files, from its whole <c>.diff</c> split per file.</summary>
    private async Task<List<ScmFileDiff>> FilesAsync(string project, long number, CancellationToken cancellationToken)
    {
        var patch = await _http
            .GetTextAsync(RequestPath(project, number) + ".diff", cancellationToken)
            .ConfigureAwait(false);
        if (patch.Length > MaxDiffText)
        {
            patch = patch[..MaxDiffText];
        }

        return UnifiedDiff.SplitFiles(patch.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static (string Owner, string Repo) Split(string project)
    {
        var parts = (project ?? "").Trim().Trim('/').Split('/');
        return parts is [{ Length: > 0 } owner, { Length: > 0 } repo]
            ? (owner, repo)
            : throw new ScmException(ScmErrorKind.Invalid, $"A Forgejo project is owner/repo; got '{project}'.");
    }

    private static string RepoPath(string project)
    {
        var (owner, repo) = Split(project);
        return $"api/v1/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
    }

    private static string RequestPath(string project, long number) =>
        RepoPath(project) + "/pulls/" + number.ToString(CultureInfo.InvariantCulture);

    private static string IssuePath(string project, long number) =>
        RepoPath(project) + "/issues/" + number.ToString(CultureInfo.InvariantCulture);

    private static string ReviewPath(string project, long number, long review) =>
        RequestPath(project, number) + "/reviews/" + review.ToString(CultureInfo.InvariantCulture);

    private static ScmUser? User(FjUser? user) =>
        user switch
        {
            null => null,
            { FullName: null or "" } => new ScmUser(user.Login ?? ""),
            _ => new ScmUser(user.Login ?? "", user.FullName),
        };

    private static Uri? WebUrl(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : null;

    private static ScmProject Project(FjRepo repo) =>
        new(
            repo.Id.ToString(CultureInfo.InvariantCulture),
            repo.FullName ?? "",
            repo.Name ?? "",
            string.IsNullOrEmpty(repo.Description) ? null : repo.Description,
            (repo.Private, repo.Internal) switch
            {
                (true, _) => ScmVisibility.Private,
                (_, true) => ScmVisibility.Internal,
                _ => ScmVisibility.Public,
            },
            WebUrl(repo.HtmlUrl),
            repo.DefaultBranch
        );

    /// <summary>Users whose latest review approves and was neither dismissed nor made stale by new commits.</summary>
    private static List<ScmUser> ApprovedBy(IEnumerable<FjReview> reviews) =>
        reviews
            .Where(review => review.User is not null && review.State is "APPROVED" or "REQUEST_CHANGES")
            .GroupBy(review => review.User!.Id)
            .Select(group => group.OrderBy(review => review.SubmittedAt).Last())
            .Where(review => review.State is "APPROVED" && !review.Dismissed && !review.Stale)
            .Select(review => User(review.User)!)
            .ToList();

    private static MergeRequest Request(FjPull pull, IReadOnlyList<FjReview>? reviews) =>
        new(
            pull.Id,
            pull.Number,
            pull.Title ?? "",
            pull.Body ?? "",
            (pull.State, pull.Merged) switch
            {
                (_, true) => MergeRequestState.Merged,
                ("closed", _) => MergeRequestState.Closed,
                _ => MergeRequestState.Opened,
            },
            User(pull.User),
            pull.Head?.Ref ?? "",
            pull.Base?.Ref ?? "",
            pull.Head?.Sha,
            pull.MergeBase ?? pull.Base?.Sha,
            null,
            WebUrl(pull.HtmlUrl),
            pull.Draft
                || (pull.Title ?? "").StartsWith("WIP:", StringComparison.OrdinalIgnoreCase)
                || (pull.Title ?? "").StartsWith("[WIP]", StringComparison.OrdinalIgnoreCase),
            pull.CreatedAt,
            pull.UpdatedAt,
            pull.MergedAt,
            null,
            reviews is null ? [] : ApprovedBy(reviews),
            (pull.RequestedReviewers ?? []).Select(User).OfType<ScmUser>().ToList(),
            (pull.Labels ?? []).Select(label => label.Name ?? "").Where(name => name.Length > 0).ToList(),
            pull.Mergeable switch
            {
                true => "mergeable",
                false => "conflict",
                null => null,
            }
        );

    private static ScmComment Comment(FjComment comment) =>
        new(
            comment.Id.ToString(CultureInfo.InvariantCulture),
            comment.Body ?? "",
            User(comment.User),
            comment.CreatedAt,
            false,
            null,
            WebUrl(comment.HtmlUrl),
            null
        );

    private static ScmComment DiffComment(FjReviewComment comment, string thread)
    {
        // a comment on the new side has a position; one on a removed line only an original position
        int? newLine = comment.Position > 0 ? (int)comment.Position : null;
        int? oldLine = newLine is null && comment.OriginalPosition > 0 ? (int)comment.OriginalPosition : null;
        return new(
            comment.Id.ToString(CultureInfo.InvariantCulture),
            comment.Body ?? "",
            User(comment.User),
            comment.CreatedAt,
            false,
            new ScmDiffPosition(comment.Path, comment.Path, newLine, oldLine),
            WebUrl(comment.HtmlUrl),
            thread
        );
    }
}
