using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

/// <summary>
/// Source-generated JSON metadata for the API contracts (one file per area: Projects, Workspaces, Tasks, Live, Docs):
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
internal sealed partial class UiJsonContext : JsonSerializerContext;
