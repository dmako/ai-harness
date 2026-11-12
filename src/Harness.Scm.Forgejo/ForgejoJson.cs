using System.Text.Json.Serialization;

namespace Harness.Scm.Forgejo;

// Forgejo (Gitea-compatible) REST API v1 shapes, only the fields harness reads; names follow the API's snake_case.

internal sealed record FjVersion(string? Version);

internal sealed record FjUser(long Id, string? Login, string? FullName);

internal sealed record FjRepo(
    long Id,
    string? FullName,
    string? Name,
    string? Description,
    bool Private,
    bool Internal,
    string? HtmlUrl,
    string? DefaultBranch
);

internal sealed record FjSearch(bool Ok, List<FjRepo>? Data);

internal sealed record FjBranch(string? Ref, string? Sha);

internal sealed record FjLabel(string? Name);

internal sealed record FjPull(
    long Id,
    long Number,
    string? Title,
    string? Body,
    string? State,
    FjUser? User,
    FjBranch? Head,
    FjBranch? Base,
    string? MergeBase,
    string? HtmlUrl,
    bool Draft,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? MergedAt,
    bool Merged,
    List<FjLabel>? Labels,
    List<FjUser>? RequestedReviewers,
    bool? Mergeable
);

internal sealed record FjComment(long Id, string? Body, FjUser? User, DateTimeOffset? CreatedAt, string? HtmlUrl);

internal sealed record FjReview(
    long Id,
    FjUser? User,
    string? State,
    string? Body,
    DateTimeOffset? SubmittedAt,
    string? HtmlUrl,
    bool Stale,
    bool Dismissed,
    long CommentsCount
);

/// <summary>A review comment: <c>position</c> is the new file's line, <c>original_position</c> the old one's.</summary>
internal sealed record FjReviewComment(
    long Id,
    string? Body,
    FjUser? User,
    string? Path,
    long Position,
    long OriginalPosition,
    DateTimeOffset? CreatedAt,
    string? HtmlUrl,
    long PullRequestReviewId
);

internal sealed record FjCommentBody(string Body);

internal sealed record FjReviewCommentBody(string Path, string Body, long? NewPosition, long? OldPosition);

internal sealed record FjReviewBody(string Event, string Body, string? CommitId, List<FjReviewCommentBody>? Comments);

internal sealed record FjPullEdit(string? Title, string? Body);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString
)]
[JsonSerializable(typeof(FjVersion))]
[JsonSerializable(typeof(FjUser))]
[JsonSerializable(typeof(FjRepo))]
[JsonSerializable(typeof(List<FjRepo>))]
[JsonSerializable(typeof(FjSearch))]
[JsonSerializable(typeof(FjPull))]
[JsonSerializable(typeof(List<FjPull>))]
[JsonSerializable(typeof(FjComment))]
[JsonSerializable(typeof(List<FjComment>))]
[JsonSerializable(typeof(FjReview))]
[JsonSerializable(typeof(List<FjReview>))]
[JsonSerializable(typeof(List<FjReviewComment>))]
[JsonSerializable(typeof(FjCommentBody))]
[JsonSerializable(typeof(FjReviewBody))]
[JsonSerializable(typeof(FjPullEdit))]
internal sealed partial class ForgejoJsonContext : JsonSerializerContext;
