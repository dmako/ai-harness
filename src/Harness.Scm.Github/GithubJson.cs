using System.Text.Json.Serialization;

namespace Harness.Scm.Github;

// GitHub REST API (v3, 2022-11-28) shapes, only the fields harness reads; names follow the API's snake_case.

/// <summary>GET /meta: github.com leaves installed_version out, GitHub Enterprise Server names its version.</summary>
internal sealed record GhMeta(bool? VerifiablePasswordAuthentication, string? InstalledVersion);

internal sealed record GhUser(long Id, string? Login, string? Name);

internal sealed record GhRepo(
    long Id,
    string? FullName,
    string? Name,
    string? Description,
    bool Private,
    string? Visibility,
    string? HtmlUrl,
    string? DefaultBranch
);

internal sealed record GhSearch(long TotalCount, List<GhRepo>? Items);

internal sealed record GhBranch(string? Ref, string? Sha);

internal sealed record GhLabel(string? Name);

internal sealed record GhPull(
    long Id,
    long Number,
    string? Title,
    string? Body,
    string? State,
    GhUser? User,
    GhBranch? Head,
    GhBranch? Base,
    string? HtmlUrl,
    bool Draft,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? MergedAt,
    List<GhLabel>? Labels,
    List<GhUser>? RequestedReviewers,
    bool? Mergeable
);

internal sealed record GhComment(long Id, string? Body, GhUser? User, DateTimeOffset? CreatedAt, string? HtmlUrl);

internal sealed record GhReview(
    long Id,
    GhUser? User,
    string? State,
    string? Body,
    DateTimeOffset? SubmittedAt,
    string? HtmlUrl
);

/// <summary>
/// A review comment on a diff line: <c>line</c> and <c>side</c> (RIGHT new file, LEFT old file) where it sits now,
/// <c>original_line</c> where it was made (all that is left once the line is outdated).
/// </summary>
internal sealed record GhReviewComment(
    long Id,
    string? Body,
    GhUser? User,
    string? Path,
    int? Line,
    int? OriginalLine,
    string? Side,
    DateTimeOffset? CreatedAt,
    string? HtmlUrl,
    long? PullRequestReviewId,
    long? InReplyToId
);

internal sealed record GhCommentBody(string Body);

internal sealed record GhReviewCommentBody(string Body, string? CommitId, string Path, int Line, string Side);

internal sealed record GhReviewBody(string Event, string? CommitId, string? Body);

internal sealed record GhPullEdit(string? Title, string? Body);

/// <summary>A new pull request from <c>head</c> (a branch, or owner:branch of a fork) into <c>base</c>.</summary>
internal sealed record GhPullCreate(string Head, string Base, string Title, string Body);

internal sealed record GhAssigneesBody(List<string> Assignees);

internal sealed record GhReviewersBody(List<string> Reviewers);

/// <summary>The issue behind a pull request, as POST .../issues/{number}/assignees answers it.</summary>
internal sealed record GhIssue(long Number, List<GhUser>? Assignees);

internal sealed record GhBranchInfo(string? Name);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString
)]
[JsonSerializable(typeof(GhMeta))]
[JsonSerializable(typeof(GhUser))]
[JsonSerializable(typeof(GhRepo))]
[JsonSerializable(typeof(List<GhRepo>))]
[JsonSerializable(typeof(GhSearch))]
[JsonSerializable(typeof(GhPull))]
[JsonSerializable(typeof(List<GhPull>))]
[JsonSerializable(typeof(GhComment))]
[JsonSerializable(typeof(List<GhComment>))]
[JsonSerializable(typeof(GhReview))]
[JsonSerializable(typeof(List<GhReview>))]
[JsonSerializable(typeof(GhReviewComment))]
[JsonSerializable(typeof(List<GhReviewComment>))]
[JsonSerializable(typeof(GhCommentBody))]
[JsonSerializable(typeof(GhReviewCommentBody))]
[JsonSerializable(typeof(GhReviewBody))]
[JsonSerializable(typeof(GhPullEdit))]
[JsonSerializable(typeof(GhPullCreate))]
[JsonSerializable(typeof(GhAssigneesBody))]
[JsonSerializable(typeof(GhReviewersBody))]
[JsonSerializable(typeof(GhIssue))]
[JsonSerializable(typeof(GhBranchInfo))]
internal sealed partial class GithubJsonContext : JsonSerializerContext;
