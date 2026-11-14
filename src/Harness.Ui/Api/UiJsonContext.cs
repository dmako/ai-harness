using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

/// <summary>
/// Source-generated JSON metadata for the API contracts (one file per area: Projects, Workspaces, Tasks, Work, Overview, Live, Docs, Health):
/// no reflection, so trimming and AOT keep working. Strict: a required member missing from an answer, or null where
/// the contract says it cannot be, fails deserialisation.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    RespectNullableAnnotations = true,
    GenerationMode = JsonSourceGenerationMode.Metadata
)]
[JsonSerializable(typeof(ProjectList))]
[JsonSerializable(typeof(WorkspaceList))]
[JsonSerializable(typeof(RenameChange))]
[JsonSerializable(typeof(BoardResponse))]
[JsonSerializable(typeof(BoardChange))]
[JsonSerializable(typeof(LiveChange))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(DocList))]
[JsonSerializable(typeof(HealthReport))]
[JsonSerializable(typeof(FixRequest))]
[JsonSerializable(typeof(FixResult))]
[JsonSerializable(typeof(TaskList))]
[JsonSerializable(typeof(ProjectReport))]
[JsonSerializable(typeof(ProjectTrends))]
[JsonSerializable(typeof(ActivityFeed))]
[JsonSerializable(typeof(BlueprintList))]
[JsonSerializable(typeof(NextId))]
[JsonSerializable(typeof(TaskEvents))]
[JsonSerializable(typeof(TaskSessions))]
[JsonSerializable(typeof(TaskProposalList))]
[JsonSerializable(typeof(BlueprintProposalList))]
[JsonSerializable(typeof(ArchProposalList))]
[JsonSerializable(typeof(TaskForm))]
[JsonSerializable(typeof(BlueprintForm))]
[JsonSerializable(typeof(BlueprintRemoval))]
[JsonSerializable(typeof(BlueprintOrder))]
[JsonSerializable(typeof(TaskMove))]
[JsonSerializable(typeof(TaskApproval))]
[JsonSerializable(typeof(BlueprintApproval))]
[JsonSerializable(typeof(ArchApproval))]
[JsonSerializable(typeof(Rejection))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class UiJsonContext : JsonSerializerContext;
