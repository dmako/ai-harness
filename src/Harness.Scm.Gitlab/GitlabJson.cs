using System.Text.Json.Serialization;

namespace Harness.Scm.Gitlab;

// GitLab REST API v4 shapes, only the fields harness reads; names follow the API's snake_case.

internal sealed record GlVersion(string? Version, string? Revision);

internal sealed record GlUser(long Id, string? Username, string? Name);

internal sealed record GlProject(
    long Id,
    string? PathWithNamespace,
    string? Name,
    string? Description,
    string? Visibility,
    string? WebUrl,
    string? DefaultBranch
);

internal sealed record GlDiffRefs(string? BaseSha, string? HeadSha, string? StartSha);

internal sealed record GlMergeRequest(
    long Id,
    long Iid,
    string? Title,
    string? Description,
    string? State,
    GlUser? Author,
    string? SourceBranch,
    string? TargetBranch,
    string? Sha,
    GlDiffRefs? DiffRefs,
    string? WebUrl,
    bool Draft,
    bool WorkInProgress,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? MergedAt,
    List<string>? Labels,
    List<GlUser>? Reviewers,
    string? DetailedMergeStatus,
    string? MergeStatus,
    List<GlUser>? Assignees = null
);

internal sealed record GlPosition(string? NewPath, string? OldPath, int? NewLine, int? OldLine);

internal sealed record GlNote(
    long Id,
    string? Body,
    GlUser? Author,
    DateTimeOffset? CreatedAt,
    bool System,
    GlPosition? Position,
    string? Type
);

internal sealed record GlDiscussion(string? Id, bool IndividualNote, List<GlNote>? Notes);

internal sealed record GlDiff(
    string? OldPath,
    string? NewPath,
    bool NewFile,
    bool RenamedFile,
    bool DeletedFile,
    string? Diff,
    bool? TooLarge,
    bool? Collapsed
);

internal sealed record GlChanges(List<GlDiff>? Changes, bool? Overflow);

internal sealed record GlApprovedBy(GlUser? User);

internal sealed record GlApprovals(bool? Approved, List<GlApprovedBy>? ApprovedBy);

internal sealed record GlNoteBody(string Body);

internal sealed record GlPositionBody(
    string PositionType,
    string? BaseSha,
    string? StartSha,
    string? HeadSha,
    string? OldPath,
    string? NewPath,
    int? OldLine,
    int? NewLine
);

internal sealed record GlDiscussionBody(string Body, GlPositionBody Position);

internal sealed record GlUpdateBody(string? Title, string? Description);

internal sealed record GlBranch(string? Name);

/// <summary>A new merge request; assignee and reviewer ids are left out when null.</summary>
internal sealed record GlCreateBody(
    string SourceBranch,
    string TargetBranch,
    string Title,
    string? Description,
    List<long>? AssigneeIds,
    List<long>? ReviewerIds
);

/// <summary>Approve, optionally only while the merge request's HEAD is this SHA; null sends an empty object.</summary>
internal sealed record GlApproveBody(string? Sha);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString
)]
[JsonSerializable(typeof(GlVersion))]
[JsonSerializable(typeof(GlUser))]
[JsonSerializable(typeof(List<GlUser>))]
[JsonSerializable(typeof(GlBranch))]
[JsonSerializable(typeof(GlCreateBody))]
[JsonSerializable(typeof(GlProject))]
[JsonSerializable(typeof(List<GlProject>))]
[JsonSerializable(typeof(GlMergeRequest))]
[JsonSerializable(typeof(List<GlMergeRequest>))]
[JsonSerializable(typeof(GlNote))]
[JsonSerializable(typeof(List<GlDiscussion>))]
[JsonSerializable(typeof(GlDiscussion))]
[JsonSerializable(typeof(List<GlDiff>))]
[JsonSerializable(typeof(GlChanges))]
[JsonSerializable(typeof(GlApprovals))]
[JsonSerializable(typeof(GlNoteBody))]
[JsonSerializable(typeof(GlDiscussionBody))]
[JsonSerializable(typeof(GlUpdateBody))]
[JsonSerializable(typeof(GlApproveBody))]
internal sealed partial class GitlabJsonContext : JsonSerializerContext;
