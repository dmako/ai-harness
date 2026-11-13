using System.Globalization;
using System.Text.Json.Serialization.Metadata;

namespace Harness.Scm.Gitlab;

/// <summary>GitLab, REST API v4, authenticated with a personal, group or project access token (PRIVATE-TOKEN).</summary>
/// <param name="handler">The HTTP handler; the process-wide shared one when null (tests pass a fake).</param>
public sealed class GitlabProvider(HttpMessageHandler? handler = null, ScmLimits? limits = null) : IScmProvider
{
    public ScmKind Kind => ScmKind.Gitlab;

    public IScmClient Connect(ScmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Endpoint.Kind is not ScmKind.Gitlab)
        {
            throw new ArgumentException("The connection is not a GitLab one.", nameof(connection));
        }

        return new GitlabClient(connection, handler ?? ScmHttpClient.SharedHandler, limits ?? ScmLimits.Default);
    }
}

/// <summary>One GitLab server and token. Projects are named by full path, merge requests by iid.</summary>
internal sealed class GitlabClient(ScmConnection connection, HttpMessageHandler handler, ScmLimits limits) : IScmClient
{
    private readonly ScmHttpClient _http = new(
        connection,
        handler,
        limits,
        headers => headers.Add("PRIVATE-TOKEN", connection.Token)
    );

    private static GitlabJsonContext Json => GitlabJsonContext.Default;

    public ScmConnection Connection => _http.Connection;

    public async Task<ScmConnectionTest> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = (
                await _http.GetAsync("api/v4/version", Json.GlVersion, cancellationToken).ConfigureAwait(false)
            ).Value;
            if (string.IsNullOrWhiteSpace(version.Version))
            {
                throw new ScmException(
                    ScmErrorKind.WrongServer,
                    $"{Connection.Endpoint.BaseUrl.Host} answered /api/v4/version without a GitLab version."
                );
            }

            var user = (
                await _http.GetAsync("api/v4/user", Json.GlUser, cancellationToken).ConfigureAwait(false)
            ).Value;
            return ScmConnectionTest.Success(version.Version, User(user));
        }
        catch (ScmException exception)
        {
            return ScmConnectionTest.Failed(
                exception.Kind is ScmErrorKind.NotFound
                    ? new ScmException(
                        ScmErrorKind.WrongServer,
                        $"No GitLab API v4 at {Connection.Endpoint.BaseUrl}: {exception.Message}",
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
        var filter = string.IsNullOrWhiteSpace(search) ? "" : "&search=" + Uri.EscapeDataString(search.Trim());
        var projects = await _http
            .GetPagesAsync(
                (page, size) =>
                    $"api/v4/projects?membership=true&order_by=last_activity_at&visibility={ScmNames.Name(visibility)}{filter}&per_page={size}&page={page}",
                Json.ListGlProject,
                cancellationToken
            )
            .ConfigureAwait(false);
        return projects.Select(Project).ToList();
    }

    public async Task<ScmProject> GetProjectAsync(string project, CancellationToken cancellationToken) =>
        Project(
            (await _http.GetAsync(ProjectPath(project), Json.GlProject, cancellationToken).ConfigureAwait(false)).Value
        );

    public async Task<IReadOnlyList<MergeRequest>> ListMergeRequestsAsync(
        string project,
        MergeRequestState state,
        CancellationToken cancellationToken
    )
    {
        var requests = await _http
            .GetPagesAsync(
                (page, size) =>
                    $"{ProjectPath(project)}/merge_requests?state={ScmNames.Name(state)}&order_by=updated_at&sort=desc&per_page={size}&page={page}",
                Json.ListGlMergeRequest,
                cancellationToken
            )
            .ConfigureAwait(false);
        return requests.Select(request => Request(request, null)).ToList();
    }

    public async Task<MergeRequest> GetMergeRequestAsync(
        string project,
        long number,
        CancellationToken cancellationToken
    )
    {
        var request = await GetRawAsync(project, number, cancellationToken).ConfigureAwait(false);
        GlApprovals? approvals;
        try
        {
            approvals = (
                await _http
                    .GetAsync(RequestPath(project, number) + "/approvals", Json.GlApprovals, cancellationToken)
                    .ConfigureAwait(false)
            ).Value;
        }
        catch (ScmException exception) when (exception.Kind is ScmErrorKind.NotFound or ScmErrorKind.Forbidden)
        {
            approvals = null;
        }

        return Request(request, approvals);
    }

    public async Task<IReadOnlyList<ScmComment>> ListCommentsAsync(
        string project,
        long number,
        CancellationToken cancellationToken
    )
    {
        var discussions = await _http
            .GetPagesAsync(
                (page, size) => $"{RequestPath(project, number)}/discussions?per_page={size}&page={page}",
                Json.ListGlDiscussion,
                cancellationToken
            )
            .ConfigureAwait(false);
        return discussions
            .SelectMany(discussion =>
                (discussion.Notes ?? []).Select(note => Comment(note, discussion.IndividualNote ? null : discussion.Id))
            )
            .OrderBy(comment => comment.CreatedAt)
            .ToList();
    }

    public async Task<ScmComment> AddCommentAsync(
        string project,
        long number,
        string body,
        CancellationToken cancellationToken
    )
    {
        var note = await _http
            .SendAsync(
                HttpMethod.Post,
                RequestPath(project, number) + "/notes",
                new GlNoteBody(body),
                Json.GlNoteBody,
                Json.GlNote,
                cancellationToken
            )
            .ConfigureAwait(false);
        return Comment(note, null);
    }

    public async Task<ScmComment> AddDiffCommentAsync(
        string project,
        long number,
        ScmDiffComment comment,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(comment);
        var request = await GetRawAsync(project, number, cancellationToken).ConfigureAwait(false);
        var files = await RawDiffsAsync(project, number, cancellationToken).ConfigureAwait(false);
        var file =
            files.FirstOrDefault(diff => diff.NewPath == comment.FilePath)
            ?? files.FirstOrDefault(diff => diff.OldPath == comment.FilePath)
            ?? throw new ScmException(
                ScmErrorKind.Invalid,
                $"'{comment.FilePath}' is not changed by merge request !{number}; comment on a file in its diff."
            );
        var line =
            UnifiedDiff.Locate(file.Diff ?? "", comment.Line, comment.LineType)
            ?? throw new ScmException(
                ScmErrorKind.Invalid,
                $"Line {comment.Line} ({ScmNames.Name(comment.LineType)}) of '{comment.FilePath}' is not in merge request "
                    + $"!{number}'s diff; comment on a changed line or a context line shown with it."
            );
        var refs = request.DiffRefs;
        var discussion = await _http
            .SendAsync(
                HttpMethod.Post,
                RequestPath(project, number) + "/discussions",
                new GlDiscussionBody(
                    comment.Body,
                    new GlPositionBody(
                        "text",
                        refs?.BaseSha,
                        refs?.StartSha,
                        refs?.HeadSha ?? request.Sha,
                        file.OldPath ?? file.NewPath,
                        file.NewPath ?? file.OldPath,
                        line.OldLine,
                        line.NewLine
                    )
                ),
                Json.GlDiscussionBody,
                Json.GlDiscussion,
                cancellationToken
            )
            .ConfigureAwait(false);
        var note =
            discussion.Notes?.FirstOrDefault()
            ?? throw new ScmException(ScmErrorKind.Server, "GitLab created the discussion without a note.");
        return Comment(note, discussion.Id);
    }

    public async Task<ScmMergeRequestDiff> GetDiffAsync(
        string project,
        long number,
        int maxBytes,
        CancellationToken cancellationToken
    )
    {
        var files = await RawDiffsAsync(project, number, cancellationToken).ConfigureAwait(false);
        return ScmDiffBudget.Apply(files.Select(FileDiff), maxBytes);
    }

    public Task<MergeRequest> SetTitleAsync(
        string project,
        long number,
        string title,
        CancellationToken cancellationToken
    ) => UpdateAsync(project, number, new GlUpdateBody(title, null), cancellationToken);

    public Task<MergeRequest> SetDescriptionAsync(
        string project,
        long number,
        string description,
        CancellationToken cancellationToken
    ) => UpdateAsync(project, number, new GlUpdateBody(null, description), cancellationToken);

    public async Task<ScmApproval> ApproveAsync(string project, long number, CancellationToken cancellationToken)
    {
        var approvals = await _http
            .SendAsync(
                HttpMethod.Post,
                RequestPath(project, number) + "/approve",
                new GlApproveBody(null),
                Json.GlApproveBody,
                Json.GlApprovals,
                cancellationToken
            )
            .ConfigureAwait(false);
        return new ScmApproval(approvals.Approved, ApprovedBy(approvals));
    }

    public async Task<ScmCreatedMergeRequest> CreateMergeRequestAsync(
        string project,
        ScmNewMergeRequest request,
        CancellationToken cancellationToken
    )
    {
        request = ScmMergeRequestCreation.Normalize(request);
        var ids = await UserIdsAsync(request.Assignees.Concat(request.Reviewers), cancellationToken)
            .ConfigureAwait(false);
        GlMergeRequest created;
        try
        {
            created = await _http
                .SendAsync(
                    HttpMethod.Post,
                    ProjectPath(project) + "/merge_requests",
                    new GlCreateBody(
                        request.SourceBranch,
                        request.TargetBranch,
                        request.Title,
                        request.Description.Length > 0 ? request.Description : null,
                        request.Assignees.Count > 0 ? request.Assignees.Select(name => ids[name]).ToList() : null,
                        request.Reviewers.Count > 0 ? request.Reviewers.Select(name => ids[name]).ToList() : null
                    ),
                    Json.GlCreateBody,
                    Json.GlMergeRequest,
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
                    "merge request !",
                    token => OpenRequestAsync(project, request, token),
                    (branch, token) => BranchExistsAsync(project, branch, token),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        // GitLab leaves out, without an error, users it cannot assign or ask for a review
        List<string> warnings = [];
        var assignees = ScmMergeRequestCreation.Missing(
            request.Assignees,
            (created.Assignees ?? []).Select(user => user.Username ?? "")
        );
        if (assignees.Count > 0)
        {
            warnings.Add(
                ScmMergeRequestCreation.Warning(
                    "assignee",
                    assignees,
                    "GitLab did not assign them (they may not be project members, or the GitLab tier allows only one "
                        + "assignee)."
                )
            );
        }

        var reviewers = ScmMergeRequestCreation.Missing(
            request.Reviewers,
            (created.Reviewers ?? []).Select(user => user.Username ?? "")
        );
        if (reviewers.Count > 0)
        {
            warnings.Add(
                ScmMergeRequestCreation.Warning(
                    "reviewer",
                    reviewers,
                    "GitLab did not request their review (they may not be project members)."
                )
            );
        }

        return new ScmCreatedMergeRequest(Request(created, null), warnings);
    }

    public void Dispose() => _http.Dispose();

    private async Task<MergeRequest> UpdateAsync(
        string project,
        long number,
        GlUpdateBody body,
        CancellationToken cancellationToken
    ) =>
        Request(
            await _http
                .SendAsync(
                    HttpMethod.Put,
                    RequestPath(project, number),
                    body,
                    Json.GlUpdateBody,
                    Json.GlMergeRequest,
                    cancellationToken
                )
                .ConfigureAwait(false),
            null
        );

    /// <summary>The ids of usernames (GET /users?username=); throws naming every username GitLab does not know.</summary>
    private async Task<Dictionary<string, long>> UserIdsAsync(
        IEnumerable<string> usernames,
        CancellationToken cancellationToken
    )
    {
        Dictionary<string, long> ids = new(StringComparer.OrdinalIgnoreCase);
        List<string> unknown = [];
        foreach (var name in usernames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = (
                await _http
                    .GetAsync("api/v4/users?username=" + Uri.EscapeDataString(name), Json.ListGlUser, cancellationToken)
                    .ConfigureAwait(false)
            ).Value;
            if (
                found.FirstOrDefault(user => string.Equals(user.Username, name, StringComparison.OrdinalIgnoreCase)) is
                { } user
            )
            {
                ids[name] = user.Id;
            }
            else
            {
                unknown.Add(name);
            }
        }

        return unknown.Count > 0 ? throw ScmMergeRequestCreation.UnknownUsers(unknown) : ids;
    }

    private async Task<MergeRequest?> OpenRequestAsync(
        string project,
        ScmNewMergeRequest request,
        CancellationToken cancellationToken
    )
    {
        var open = await _http
            .GetAsync(
                $"{ProjectPath(project)}/merge_requests?state=opened"
                    + $"&source_branch={Uri.EscapeDataString(request.SourceBranch)}"
                    + $"&target_branch={Uri.EscapeDataString(request.TargetBranch)}",
                Json.ListGlMergeRequest,
                cancellationToken
            )
            .ConfigureAwait(false);
        return open.Value.FirstOrDefault() is { } found ? Request(found, null) : null;
    }

    private async Task<bool?> BranchExistsAsync(string project, string branch, CancellationToken cancellationToken)
    {
        try
        {
            await _http
                .GetAsync(
                    ProjectPath(project) + "/repository/branches/" + Uri.EscapeDataString(branch),
                    Json.GlBranch,
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

    private async Task<GlMergeRequest> GetRawAsync(string project, long number, CancellationToken cancellationToken) =>
        (
            await _http
                .GetAsync(RequestPath(project, number), Json.GlMergeRequest, cancellationToken)
                .ConfigureAwait(false)
        ).Value;

    /// <summary>The changed files: <c>/diffs</c> (GitLab 15.7+), else the older <c>/changes</c>.</summary>
    private async Task<List<GlDiff>> RawDiffsAsync(string project, long number, CancellationToken cancellationToken)
    {
        try
        {
            return await _http
                .GetPagesAsync(
                    (page, size) => $"{RequestPath(project, number)}/diffs?per_page={size}&page={page}",
                    Json.ListGlDiff,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (ScmException exception) when (exception.Kind is ScmErrorKind.NotFound)
        {
            var changes = await _http
                .GetAsync(RequestPath(project, number) + "/changes", Json.GlChanges, cancellationToken)
                .ConfigureAwait(false);
            return changes.Value.Changes ?? [];
        }
    }

    private static string ProjectPath(string project) =>
        "api/v4/projects/" + Uri.EscapeDataString(ProjectName(project));

    private static string RequestPath(string project, long number) =>
        ProjectPath(project) + "/merge_requests/" + number.ToString(CultureInfo.InvariantCulture);

    private static string ProjectName(string project) =>
        string.IsNullOrWhiteSpace(project)
            ? throw new ScmException(ScmErrorKind.Invalid, "No GitLab project was given.")
            : project.Trim().Trim('/');

    private static ScmUser? User(GlUser? user) => user is null ? null : new ScmUser(user.Username ?? "", user.Name);

    private static Uri? WebUrl(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var url) ? url : null;

    private static ScmProject Project(GlProject project) =>
        new(
            project.Id.ToString(CultureInfo.InvariantCulture),
            project.PathWithNamespace ?? "",
            project.Name ?? "",
            project.Description,
            project.Visibility switch
            {
                "public" => ScmVisibility.Public,
                "internal" => ScmVisibility.Internal,
                _ => ScmVisibility.Private,
            },
            WebUrl(project.WebUrl),
            project.DefaultBranch
        );

    private static List<ScmUser> ApprovedBy(GlApprovals? approvals) =>
        (approvals?.ApprovedBy ?? []).Select(entry => User(entry.User)).OfType<ScmUser>().ToList();

    private static MergeRequest Request(GlMergeRequest request, GlApprovals? approvals) =>
        new(
            request.Id,
            request.Iid,
            request.Title ?? "",
            request.Description ?? "",
            request.State switch
            {
                "merged" => MergeRequestState.Merged,
                "closed" or "locked" => MergeRequestState.Closed,
                _ => MergeRequestState.Opened,
            },
            User(request.Author),
            request.SourceBranch ?? "",
            request.TargetBranch ?? "",
            request.DiffRefs?.HeadSha ?? request.Sha,
            request.DiffRefs?.BaseSha,
            request.DiffRefs?.StartSha,
            WebUrl(request.WebUrl),
            request.Draft || request.WorkInProgress,
            request.CreatedAt,
            request.UpdatedAt,
            request.MergedAt,
            approvals?.Approved,
            ApprovedBy(approvals),
            (request.Reviewers ?? []).Select(User).OfType<ScmUser>().ToList(),
            request.Labels ?? [],
            request.DetailedMergeStatus ?? request.MergeStatus
        );

    private static ScmComment Comment(GlNote note, string? thread) =>
        new(
            note.Id.ToString(CultureInfo.InvariantCulture),
            note.Body ?? "",
            User(note.Author),
            note.CreatedAt,
            note.System,
            note.Position is { } position
                ? new ScmDiffPosition(position.NewPath, position.OldPath, position.NewLine, position.OldLine)
                : null,
            null,
            thread
        );

    private static ScmFileDiff FileDiff(GlDiff diff)
    {
        var text = diff.Diff ?? "";
        return new ScmFileDiff(
            diff.OldPath,
            diff.NewPath ?? diff.OldPath ?? "",
            diff.NewFile,
            diff.DeletedFile,
            diff.RenamedFile,
            text.StartsWith("Binary files", StringComparison.Ordinal),
            text,
            diff.TooLarge is true || (diff.Collapsed is true && text.Length is 0)
        );
    }
}
