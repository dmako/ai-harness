using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

/// <summary>Source-generated JSON metadata for the API contracts: no reflection, so trimming and AOT keep working.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    GenerationMode = JsonSourceGenerationMode.Metadata
)]
[JsonSerializable(typeof(ProjectList))]
[JsonSerializable(typeof(WorkspaceList))]
[JsonSerializable(typeof(RenameChange))]
[JsonSerializable(typeof(BoardResponse))]
[JsonSerializable(typeof(BoardChange))]
[JsonSerializable(typeof(LiveChange))]
[JsonSerializable(typeof(ApiError))]
internal sealed partial class UiJsonContext : JsonSerializerContext;
