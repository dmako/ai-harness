using System.Text.Json.Serialization;

namespace Harness.Core.Workspaces;

public sealed record WorkspaceCopy(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("lines")] int Lines,
    [property: JsonPropertyName("line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Line = 0,
    [property: JsonPropertyName("extends"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool Extends = false
);

public sealed record SharedCodeHome(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("member_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? MemberId = null,
    [property: JsonPropertyName("member"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Member = null,
    [property: JsonPropertyName("knowledge"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Knowledge = null
);

public sealed record WorkspaceDuplicateGroup(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind,
    [property: JsonPropertyName("copies")] IReadOnlyList<WorkspaceCopy> Copies,
    [property: JsonPropertyName("similarity")] double Similarity,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("names"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Names = null,
    [property: JsonPropertyName("home"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SharedCodeHome? Home = null
)
{
    [JsonIgnore]
    public bool Copied => Verdict is "identical" or "near copy" or "similar";
}

public sealed record WorkspaceDuplicates(
    [property: JsonPropertyName("groups")] IReadOnlyList<WorkspaceDuplicateGroup> Groups,
    [property: JsonPropertyName("notes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Notes = null
);
