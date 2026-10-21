using System.Text.Json.Serialization;

namespace Harness.Core.Architecture;

public sealed record StackPart(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null,
    [property: JsonPropertyName("major"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Major = null,
    [property: JsonPropertyName("at"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? At = null);
public sealed record ProjectStack(
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("runtime"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StackPart? Runtime,
    [property: JsonPropertyName("frameworks")] IReadOnlyList<StackPart> Frameworks,
    [property: JsonPropertyName("badge")] string Badge);

public sealed record MemberPackageUse(
    [property: JsonPropertyName("package")] string Package,
    [property: JsonPropertyName("member")] string Member,
    [property: JsonPropertyName("member_id")] string MemberId,
    [property: JsonPropertyName("versions")] IReadOnlyList<string> Versions,
    [property: JsonPropertyName("declared"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Declared);
public sealed record ProjectStackSummary(
    [property: JsonPropertyName("stack")] ProjectStack? Stack,
    [property: JsonPropertyName("uses")] IReadOnlyList<MemberPackageUse> Uses);
public sealed record StackVersionRow(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("versions")] IReadOnlyDictionary<string, IReadOnlyList<string>> Versions,
    [property: JsonPropertyName("owner"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Owner,
    [property: JsonPropertyName("published"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Published,
    [property: JsonPropertyName("differ")] bool Differ);
public sealed record WorkspaceStackSummary(
    [property: JsonPropertyName("stacks")] IReadOnlyDictionary<string, ProjectStack> Stacks,
    [property: JsonPropertyName("rows")] IReadOnlyList<StackVersionRow> Rows);
