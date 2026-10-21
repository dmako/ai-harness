using System.Text.Json.Serialization;

namespace Harness.Core.Architecture;

public sealed record CloneFunction
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("line")] public int Line { get; init; }
    [JsonPropertyName("end")] public int End { get; init; }
    [JsonPropertyName("lang")] public string Language { get; init; } = "";
    [JsonPropertyName("override"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Override { get; init; }
    [JsonPropertyName("wraps"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Wraps { get; init; }
    [JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Data { get; init; }
    [JsonIgnore] public IReadOnlyList<string> Canonical { get; init; } = [];
    [JsonIgnore] public IReadOnlyList<string> Shape { get; init; } = [];
    [JsonIgnore] public IReadOnlyList<string> Signature { get; init; } = [];
    [JsonIgnore] public IReadOnlyList<CloneStatement> Statements { get; init; } = [];
    [JsonIgnore] public int Tokens => Canonical.Count;
    [JsonIgnore] public string Id => Path + "#" + Name;
}
public sealed record CloneStatement(string Key, IReadOnlyList<string> Own, IReadOnlyList<string> Raw, int Line, int Size, bool Idiom, bool Header, bool Work, string Callee, bool Data);
public sealed record CloneOptions(int MinIdentical = 12, int MinNear = 30, double Near = 0.85, double Shape = 0.85, int Repeated = 3, int LongPair = 100);
public sealed record CloneCopy(
    [property: JsonPropertyName("funcs")] IReadOnlyList<CloneFunction> Functions,
    [property: JsonPropertyName("similarity")] double Similarity);
public sealed record CloneTemplate(
    [property: JsonPropertyName("funcs")] IReadOnlyList<CloneFunction> Functions,
    [property: JsonPropertyName("differs")] IReadOnlyDictionary<string, IReadOnlyList<string>?> Differs,
    [property: JsonPropertyName("similarity")] double Similarity,
    [property: JsonPropertyName("tokens")] int Tokens);
public sealed record ClonePlace(
    [property: JsonPropertyName("func")] CloneFunction Function,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("differs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Differs = null);
public sealed record CloneSequence(
    [property: JsonPropertyName("places")] IReadOnlyList<ClonePlace> Places,
    [property: JsonPropertyName("statements")] int Statements,
    [property: JsonPropertyName("tokens")] int Tokens,
    [property: JsonPropertyName("text")] IReadOnlyList<string> Text,
    [property: JsonPropertyName("data")] bool Data,
    [property: JsonPropertyName("holder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CloneFunction? Holder,
    [property: JsonIgnore] string Key);
public sealed record CloneBranch(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("words")] IReadOnlyList<string> Words,
    [property: JsonPropertyName("choices")] IReadOnlyList<IReadOnlyList<string>> Choices);
public sealed record CloneOperation(
    [property: JsonPropertyName("stem")] string Stem,
    [property: JsonPropertyName("funcs")] IReadOnlyList<CloneFunction> Functions,
    [property: JsonPropertyName("variant")] IReadOnlyDictionary<string, string> Variant);
public sealed record CloneContract(
    [property: JsonPropertyName("variants")] IReadOnlyList<string> Variants,
    [property: JsonPropertyName("operations")] IReadOnlyList<CloneOperation> Operations,
    [property: JsonPropertyName("branches"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CloneBranch>? Branches);
