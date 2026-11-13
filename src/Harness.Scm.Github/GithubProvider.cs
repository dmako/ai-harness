using System.Globalization;

namespace Harness.Scm.Github;

/// <summary>GitHub (github.com or GitHub Enterprise Server), REST API v3, authenticated with an access token.</summary>
/// <param name="handler">The HTTP handler; the process-wide shared one when null (tests pass a fake).</param>
public sealed class GithubProvider(HttpMessageHandler? handler = null, ScmLimits? limits = null) : IScmProvider
{
    private const string DotCom = "github.com";
    private const string Api = "api.";
    private const string EnterprisePath = "/api/v3/";

    public ScmKind Kind => ScmKind.Github;

    public IScmClient Connect(ScmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Endpoint.Kind is ScmKind.Github
            ? new GithubClient(connection, handler ?? ScmHttpClient.SharedHandler, limits ?? ScmLimits.Default)
            : throw new ArgumentException("The connection is not a GitHub one.", nameof(connection));
    }

    /// <summary>
    /// Where the REST API of a GitHub base URL lives: https://api.github.com for github.com, https://api.&lt;host&gt;
    /// for GitHub Enterprise Cloud with data residency (*.ghe.com), else &lt;base&gt;/api/v3 (Enterprise Server).
    /// </summary>
    public static Uri ApiRoot(ScmEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var host = endpoint.BaseUrl.Host;
        if (
            string.Equals(host, DotCom, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "www." + DotCom, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, Api + DotCom, StringComparison.OrdinalIgnoreCase)
        )
        {
            return Https(Api + DotCom);
        }

        if (host.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase))
        {
            return Https(host.StartsWith(Api, StringComparison.OrdinalIgnoreCase) ? host : Api + host);
        }

        return new UriBuilder(endpoint.BaseUrl)
        {
            Path = endpoint.BaseUrl.AbsolutePath.TrimEnd('/') + EnterprisePath,
        }.Uri;
    }

    private static Uri Https(string host) => new UriBuilder(Uri.UriSchemeHttps, host).Uri;
}

/// <summary>
/// One GitHub server and token. Projects are repositories named owner/repo; merge requests are pull requests by
/// number. Approvals are pull request reviews; diff comments are review comments.
/// </summary>
internal sealed class GithubClient(ScmConnection connection, HttpMessageHandler handler, ScmLimits limits) : IScmClient
{
    private const int MaxDiffText = 20 * 1024 * 1024;
    private const string DiffMediaType = "application/vnd.github.diff";

    private readonly ScmHttpClient _http = new(
        connection,
        handler,
        limits,
        headers =>
        {
            headers.TryAddWithoutValidation("Authorization", "Bearer " + connection.Token);
            headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        },
        GithubProvider.ApiRoot(connection.Endpoint)
    );

    private static GithubJsonContext Json => GithubJsonContext.Default;

    public ScmConnection Connection => _http.Connection;

    public async Task<ScmConnectionTest> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var meta = (await _http.GetAsync("meta", Json.GhMeta, cancellationToken).ConfigureAwait(false)).Value;
            if (meta is { VerifiablePasswordAuthentication: null, InstalledVersion: null })
            {
                throw new ScmException(
                    ScmErrorKind.WrongServer,
                    $"{Connection.Endpoint.BaseUrl.Host} answered /meta without GitHub's metadata."
                );
            }

            var user = (await _http.GetAsync("user", Json.GhUser, cancellationToken).ConfigureAwait(false)).Value;
            return ScmConnectionTest.Success(meta.InstalledVersion ?? "github.com", User(user));
        }
        catch (ScmException exception)
        {
            return ScmConnectionTest.Failed(
                exception.Kind is ScmErrorKind.NotFound
                    ? new ScmException(
                        ScmErrorKind.WrongServer,
                        $"No GitHub REST API at {GithubProvider.ApiRoot(Connection.Endpoint)}: {exception.Message}",
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
        var repos = string.IsNullOrWhiteSpace(search)
            ? await _http
                .GetPagesAsync(
                    (page, size) =>
                        "user/repos?visibility=all&affiliation=owner,collaborator,organization_member"
                        + $"&sort=updated&per_page={size}&page={page}",
                    Json.ListGhRepo,
                    cancellationToken
                )
                .ConfigureAwait(false)
            : await _http
                .GetPagesAsync(
                    (page, size) =>
                        $"search/repositories?q={Uri.EscapeDataString(search.Trim() + " in:name")}"
                        + $"&sort=updated&per_page={size}&page={page}",
                    Json.GhSearch,
                    result => result.Items,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return repos
            .DistinctBy(repo => repo.Id)
            .Select(Project)
            .Where(project => project.Visibility == visibility)
            .ToList();
    }

    public async Task<ScmProject> GetProjectAsync(string project, CancellationToken cancellationToken) =>
        Project((await _http.GetAsync(RepoPath(project), Json.GhRepo, cancellationToken).ConfigureAwait(false)).Value);

    public async Task<IReadOnlyList<MergeRequest>> ListMergeRequestsAsync(
        string project,
        MergeRequestState state,
        CancellationToken cancellationToken
    )
    {
        var githubState = state is MergeRequestState.Opened ? "open" : "closed";
        var pulls = await _http
            .GetPagesAsync(
                (page, size) =>
                    $"{RepoPath(project)}/pulls?state={githubState}&sort=updated&direction=desc&per_page={size}&page={page}",
                Json.ListGhPull,
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
        var issueComments = await _http
            .GetPagesAsync(
                (page, size) => $"{IssuePath(project, number)}/comments?per_page={size}&page={page}",
                Json.ListGhComment,
                cancellationToken
            )
            .ConfigureAwait(false);
        var reviews = await ReviewsAsync(project, number, cancellationToken).ConfigureAwait(false);
        var reviewComments = await _http
            .GetPagesAsync(
                (page, size) => $"{RequestPath(project, number)}/comments?per_page={size}&page={page}",
                Json.ListGhReviewComment,
                cancellationToken
            )
            .ConfigureAwait(false);

        List<ScmComment> comments = issueComments.Select(Comment).ToList();
        comments.AddRange(
            reviews
                .Where(review => !string.IsNullOrWhiteSpace(review.Body))
                .Select(review => new ScmComment(
                    "review-" + review.Id.ToString(CultureInfo.InvariantCulture),
                    $"[{review.State}] {review.Body}",
                    User(review.User),
                    review.SubmittedAt,
                    false,
                    null,
                    WebUrl(review.HtmlUrl),
                    null
                ))
        );
        comments.AddRange(reviewComments.Select(DiffComment));
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
                    new GhCommentBody(body),
                    Json.GhCommentBody,
                    Json.GhComment,
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
        var (path, lineNumber, side) = comment.LineType switch
        {
            DiffLineType.Old => (file.OldPath ?? file.NewPath, line.OldLine ?? comment.Line, "LEFT"),
            _ => (file.NewPath, line.NewLine ?? comment.Line, "RIGHT"),
        };
        var created = await _http
            .SendAsync(
                HttpMethod.Post,
                RequestPath(project, number) + "/comments",
                new GhReviewCommentBody(comment.Body, pull.Head?.Sha, path, lineNumber, side),
                Json.GhReviewCommentBody,
                Json.GhReviewComment,
                cancellationToken
            )
            .ConfigureAwait(false);
        return DiffComment(created);
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
    ) => EditAsync(project, number, new GhPullEdit(title, null), cancellationToken);

    public Task<MergeRequest> SetDescriptionAsync(
        string project,
        long number,
        string description,
        CancellationToken cancellationToken
    ) => EditAsync(project, number, new GhPullEdit(null, description), cancellationToken);

    public async Task<ScmApproval> ApproveAsync(string project, long number, CancellationToken cancellationToken)
    {
        var pull = await GetPullAsync(project, number, cancellationToken).ConfigureAwait(false);
        await _http
            .SendAsync(
                HttpMethod.Post,
                RequestPath(project, number) + "/reviews",
                new GhReviewBody("APPROVE", pull.Head?.Sha, null),
                Json.GhReviewBody,
                Json.GhReview,
                cancellationToken
            )
            .ConfigureAwait(false);
        var reviews = await ReviewsAsync(project, number, cancellationToken).ConfigureAwait(false);
        return new ScmApproval(null, ApprovedBy(reviews));
    }

    public async Task<ScmCreatedMergeRequest> CreateMergeRequestAsync(
        string project,
        ScmNewMergeRequest request,
        CancellationToken cancellationToken
    )
    {
        request = ScmMergeRequestCreation.Normalize(request);
        await KnownUsersAsync(request.Assignees.Concat(request.Reviewers), cancellationToken).ConfigureAwait(false);
        GhPull pull;
        try
        {
            pull = await _http
                .SendAsync(
                    HttpMethod.Post,
                    RepoPath(project) + "/pulls",
                    new GhPullCreate(request.SourceBranch, request.TargetBranch, request.Title, request.Description),
                    Json.GhPullCreate,
                    Json.GhPull,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (ScmException failure)
        {
            throw await ScmMergeRequestCreation
                .ExplainAsync(
                    failure,
                    request,
                    "pull request #",
                    token => OpenPullAsync(project, request, token),
                    (branch, token) => BranchExistsAsync(project, branch, token),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        List<string> warnings = [];
        if (request.Assignees.Count > 0)
        {
            try
            {
                var issue = await _http
                    .SendAsync(
                        HttpMethod.Post,
                        IssuePath(project, pull.Number) + "/assignees",
                        new GhAssigneesBody(request.Assignees.ToList()),
                        Json.GhAssigneesBody,
                        Json.GhIssue,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                // GitHub leaves out, without an error, users who cannot be assigned
                var unassigned = ScmMergeRequestCreation.Missing(
                    request.Assignees,
                    (issue.Assignees ?? []).Select(user => user.Login ?? "")
                );
                if (unassigned.Count > 0)
                {
                    warnings.Add(
                        ScmMergeRequestCreation.Warning(
                            "assignee",
                            unassigned,
                            "GitHub did not assign them (only users with access to the repository can be assigned)."
                        )
                    );
                }
            }
            catch (ScmException exception)
            {
                warnings.Add(ScmMergeRequestCreation.Warning("assignee", request.Assignees, exception.Message));
            }
        }

        if (request.Reviewers.Count > 0)
        {
            try
            {
                pull = await _http
                    .SendAsync(
                        HttpMethod.Post,
                        RequestPath(project, pull.Number) + "/requested_reviewers",
                        new GhReviewersBody(request.Reviewers.ToList()),
                        Json.GhReviewersBody,
                        Json.GhPull,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (ScmException exception)
            {
                warnings.Add(ScmMergeRequestCreation.Warning("reviewer", request.Reviewers, exception.Message));
            }
        }

        return new ScmCreatedMergeRequest(Request(pull, null), warnings);
    }

    public void Dispose() => _http.Dispose();

    private async Task<MergeRequest> EditAsync(
        string project,
        long number,
        GhPullEdit edit,
        CancellationToken cancellationToken
    ) =>
        Request(
            await _http
                .SendAsync(
                    HttpMethod.Patch,
                    RequestPath(project, number),
                    edit,
                    Json.GhPullEdit,
                    Json.GhPull,
                    cancellationToken
                )
                .ConfigureAwait(false),
            null
        );

    /// <summary>Throws naming every username GitHub does not know (GET /users/{username} answers 404).</summary>
    private async Task KnownUsersAsync(IEnumerable<string> usernames, CancellationToken cancellationToken)
    {
        List<string> unknown = [];
        foreach (var name in usernames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await _http
                    .GetAsync("users/" + Uri.EscapeDataString(name), Json.GhUser, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ScmException exception) when (exception.Kind is ScmErrorKind.NotFound)
            {
                unknown.Add(name);
            }
        }

        if (unknown.Count > 0)
        {
            throw ScmMergeRequestCreation.UnknownUsers(unknown);
        }
    }

    /// <summary>The open pull request from the source branch (owner:branch, as GitHub filters) into the target.</summary>
    private async Task<MergeRequest?> OpenPullAsync(
        string project,
        ScmNewMergeRequest request,
        CancellationToken cancellationToken
    )
    {
        var head = request.SourceBranch.Contains(':', StringComparison.Ordinal)
            ? request.SourceBranch
            : Split(project).Owner + ":" + request.SourceBranch;
        var open = await _http
            .GetAsync(
                $"{RepoPath(project)}/pulls?state=open&head={Uri.EscapeDataString(head)}"
                    + $"&base={Uri.EscapeDataString(request.TargetBranch)}",
                Json.ListGhPull,
                cancellationToken
            )
            .ConfigureAwait(false);
        return open.Value.FirstOrDefault() is { } found ? Request(found, null) : null;
    }

    /// <summary>Whether a branch exists; null for a fork's owner:branch, which this repository cannot tell.</summary>
    private async Task<bool?> BranchExistsAsync(string project, string branch, CancellationToken cancellationToken)
    {
        if (branch.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            await _http
                .GetAsync(
                    RepoPath(project) + "/branches/" + string.Join('/', branch.Split('/').Select(Uri.EscapeDataString)),
                    Json.GhBranchInfo,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return true;
        }
        catch (ScmException exception) when (exception.Kind is ScmErrorKind.NotFound)
        {
            return false;
        }
    }

    private async Task<GhPull> GetPullAsync(string project, long number, CancellationToken cancellationToken) =>
        (
            await _http.GetAsync(RequestPath(project, number), Json.GhPull, cancellationToken).ConfigureAwait(false)
        ).Value;

    private Task<List<GhReview>> ReviewsAsync(string project, long number, CancellationToken cancellationToken) =>
        _http.GetPagesAsync(
            (page, size) => $"{RequestPath(project, number)}/reviews?per_page={size}&page={page}",
            Json.ListGhReview,
            cancellationToken
        );

    /// <summary>The pull request's changed files, from its whole diff (the diff media type) split per file.</summary>
    private async Task<List<ScmFileDiff>> FilesAsync(string project, long number, CancellationToken cancellationToken)
    {
        var patch = await _http
            .GetTextAsync(RequestPath(project, number), DiffMediaType, cancellationToken)
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
            : throw new ScmException(ScmErrorKind.Invalid, $"A GitHub project is owner/repo; got '{project}'.");
    }

    private static string RepoPath(string project)
    {
        var (owner, repo) = Split(project);
        return $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
    }

    private static string RequestPath(string project, long number) =>
        RepoPath(project) + "/pulls/" + number.ToString(CultureInfo.InvariantCulture);

    private static string IssuePath(string project, long number) =>
        RepoPath(project) + "/issues/" + number.ToString(CultureInfo.InvariantCulture);

    private static ScmUser? User(GhUser? user) =>
        user switch
        {
            null => null,
            { Name: null or "" } => new ScmUser(user.Login ?? ""),
            _ => new ScmUser(user.Login ?? "", user.Name),
        };

    private static Uri? WebUrl(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : null;

    private static ScmProject Project(GhRepo repo) =>
        new(
            repo.Id.ToString(CultureInfo.InvariantCulture),
            repo.FullName ?? "",
            repo.Name ?? "",
            string.IsNullOrEmpty(repo.Description) ? null : repo.Description,
            (repo.Visibility?.ToUpperInvariant(), repo.Private) switch
            {
                ("INTERNAL", _) => ScmVisibility.Internal,
                ("PRIVATE", _) or (null, true) => ScmVisibility.Private,
                _ => ScmVisibility.Public,
            },
            WebUrl(repo.HtmlUrl),
            repo.DefaultBranch
        );

    /// <summary>Users whose latest approving or change-requesting review approves and was not dismissed.</summary>
    private static List<ScmUser> ApprovedBy(IEnumerable<GhReview> reviews) =>
        reviews
            .Where(review =>
                review.User is not null && review.State is "APPROVED" or "CHANGES_REQUESTED" or "DISMISSED"
            )
            .GroupBy(review => review.User!.Id)
            .Select(group => group.OrderBy(review => review.SubmittedAt).Last())
            .Where(review => review.State is "APPROVED")
            .Select(review => User(review.User)!)
            .ToList();

    private static MergeRequest Request(GhPull pull, IReadOnlyList<GhReview>? reviews) =>
        new(
            pull.Id,
            pull.Number,
            pull.Title ?? "",
            pull.Body ?? "",
            (pull.State, pull.MergedAt) switch
            {
                (_, not null) => MergeRequestState.Merged,
                ("closed", _) => MergeRequestState.Closed,
                _ => MergeRequestState.Opened,
            },
            User(pull.User),
            pull.Head?.Ref ?? "",
            pull.Base?.Ref ?? "",
            pull.Head?.Sha,
            pull.Base?.Sha,
            null,
            WebUrl(pull.HtmlUrl),
            pull.Draft,
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

    private static ScmComment Comment(GhComment comment) =>
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

    /// <summary>
    /// A review comment: on the new file (side RIGHT) or the old one (LEFT), at its current line or, once outdated,
    /// the line it was made on. Replies share their first comment's id as the thread.
    /// </summary>
    private static ScmComment DiffComment(GhReviewComment comment)
    {
        var line = comment.Line ?? comment.OriginalLine;
        var old = string.Equals(comment.Side, "LEFT", StringComparison.OrdinalIgnoreCase);
        return new(
            comment.Id.ToString(CultureInfo.InvariantCulture),
            comment.Body ?? "",
            User(comment.User),
            comment.CreatedAt,
            false,
            new ScmDiffPosition(comment.Path, comment.Path, old ? null : line, old ? line : null),
            WebUrl(comment.HtmlUrl),
            (comment.InReplyToId ?? comment.Id).ToString(CultureInfo.InvariantCulture)
        );
    }
}
