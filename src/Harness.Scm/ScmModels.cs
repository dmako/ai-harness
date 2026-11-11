namespace Harness.Scm;

/// <summary>Project visibility as the SCM tools name it; providers map it to their own terms.</summary>
public enum ScmVisibility
{
    Private,
    Internal,
    Public,
}

/// <summary>Merge request state filter and value.</summary>
public enum MergeRequestState
{
    Opened,
    Merged,
    Closed,
}

/// <summary>Which side of a diff a line number refers to: the changed file (new) or the original (old).</summary>
public enum DiffLineType
{
    New,
    Old,
}

/// <summary>Lower-case names for the SCM enums, as used in schemas and JSON.</summary>
public static class ScmNames
{
    public static string Name(ScmVisibility visibility) =>
        visibility switch
        {
            ScmVisibility.Private => "private",
            ScmVisibility.Internal => "internal",
            ScmVisibility.Public => "public",
            _ => throw new ArgumentOutOfRangeException(nameof(visibility), visibility, "Unknown visibility."),
        };

    public static string Name(MergeRequestState state) =>
        state switch
        {
            MergeRequestState.Opened => "opened",
            MergeRequestState.Merged => "merged",
            MergeRequestState.Closed => "closed",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown state."),
        };

    public static string Name(DiffLineType type) =>
        type switch
        {
            DiffLineType.New => "new",
            DiffLineType.Old => "old",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown line type."),
        };

    public static bool TryParseVisibility(string? text, out ScmVisibility visibility)
    {
        (var ok, visibility) = text?.Trim().ToUpperInvariant() switch
        {
            "PRIVATE" => (true, ScmVisibility.Private),
            "INTERNAL" => (true, ScmVisibility.Internal),
            "PUBLIC" => (true, ScmVisibility.Public),
            _ => (false, ScmVisibility.Private),
        };
        return ok;
    }

    public static bool TryParseState(string? text, out MergeRequestState state)
    {
        (var ok, state) = text?.Trim().ToUpperInvariant() switch
        {
            "OPENED" or "OPEN" => (true, MergeRequestState.Opened),
            "MERGED" => (true, MergeRequestState.Merged),
            "CLOSED" => (true, MergeRequestState.Closed),
            _ => (false, MergeRequestState.Opened),
        };
        return ok;
    }

    public static bool TryParseLineType(string? text, out DiffLineType type)
    {
        (var ok, type) = text?.Trim().ToUpperInvariant() switch
        {
            "NEW" => (true, DiffLineType.New),
            "OLD" => (true, DiffLineType.Old),
            _ => (false, DiffLineType.New),
        };
        return ok;
    }
}

/// <summary>A user as an SCM reports them.</summary>
public sealed record ScmUser(string Username, string? Name = null);

/// <summary>An SCM project (GitLab project, Forgejo repository).</summary>
/// <param name="Id">The server's numeric id, as text.</param>
/// <param name="Path">The full path the API identifies it by: group/sub/repo (GitLab) or owner/repo (Forgejo).</param>
public sealed record ScmProject(
    string Id,
    string Path,
    string Name,
    string? Description,
    ScmVisibility Visibility,
    Uri? WebUrl,
    string? DefaultBranch
);

/// <summary>A merge request (GitLab) or pull request (Forgejo).</summary>
/// <param name="Id">The server-wide id.</param>
/// <param name="Number">The project-scoped number shown in the SCM's UI (GitLab iid, Forgejo index).</param>
/// <param name="Approved">Whether the approval rules are met, when the server says; null when unknown.</param>
public sealed record MergeRequest(
    long Id,
    long Number,
    string Title,
    string Description,
    MergeRequestState State,
    ScmUser? Author,
    string SourceBranch,
    string TargetBranch,
    string? HeadSha,
    string? BaseSha,
    string? StartSha,
    Uri? WebUrl,
    bool Draft,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? MergedAt,
    bool? Approved,
    IReadOnlyList<ScmUser> ApprovedBy,
    IReadOnlyList<ScmUser> Reviewers,
    IReadOnlyList<string> Labels,
    string? MergeStatus
);

/// <summary>Where a diff comment sits: a path and line on the new and/or old side.</summary>
public sealed record ScmDiffPosition(string? NewPath, string? OldPath, int? NewLine, int? OldLine);

/// <summary>A comment (note) on a merge request: general when <see cref="Position" /> is null, else on a diff line.</summary>
/// <param name="System">A server-generated note (pushed commits, status changes), not written by a person.</param>
/// <param name="ThreadId">The discussion or review the comment belongs to, when the server groups them.</param>
public sealed record ScmComment(
    string Id,
    string Body,
    ScmUser? Author,
    DateTimeOffset? CreatedAt,
    bool System,
    ScmDiffPosition? Position,
    Uri? WebUrl,
    string? ThreadId
);

/// <summary>A new comment on one diff line.</summary>
/// <param name="Line">1-based line number in the file on the side <paramref name="LineType" /> names.</param>
public sealed record ScmDiffComment(string Body, string FilePath, int Line, DiffLineType LineType);

/// <summary>One changed file of a merge request.</summary>
/// <param name="Diff">The unified diff hunks of the file, possibly cut short (see <see cref="Truncated" />).</param>
public sealed record ScmFileDiff(
    string? OldPath,
    string NewPath,
    bool NewFile,
    bool DeletedFile,
    bool RenamedFile,
    bool Binary,
    string Diff,
    bool Truncated
);

/// <summary>A merge request's changes; <see cref="Truncated" /> when files or hunks were left out by the size budget.</summary>
public sealed record ScmMergeRequestDiff(IReadOnlyList<ScmFileDiff> Files, bool Truncated, int OmittedFiles);

/// <summary>A merge request's approval state after an approval.</summary>
public sealed record ScmApproval(bool? Approved, IReadOnlyList<ScmUser> ApprovedBy);

/// <summary>What a connection test found: the server's version and the token's user, or why it failed.</summary>
public sealed record ScmConnectionTest(
    bool Ok,
    string? Version,
    ScmUser? User,
    ScmErrorKind? Failure,
    string? Message,
    string? Hint
)
{
    public static ScmConnectionTest Success(string? version, ScmUser? user) =>
        new(true, version, user, null, null, null);

    public static ScmConnectionTest Failed(ScmException exception) =>
        new(false, null, null, exception.Kind, exception.Message, ScmErrors.Hint(exception.Kind));
}
