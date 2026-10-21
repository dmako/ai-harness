using System.Text.Json.Serialization;

namespace Harness.Core.Workspaces;

public sealed record DeclaredType(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("bases"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Bases = null);
public sealed record ManifestReference(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Line = 0,
    [property: JsonPropertyName("matched"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Matched = null);
public sealed record LibraryConsumer(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("packages"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ManifestReference>? Packages,
    [property: JsonPropertyName("namespaces"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Namespaces,
    [property: JsonPropertyName("import_files")] int ImportFiles,
    [property: JsonPropertyName("imports"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ManifestReference>? Imports);
public sealed record PossibleLocalImplementation(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("declared")] DeclaredType Declared,
    [property: JsonPropertyName("library")] DeclaredType Library);
public sealed record LibraryAdoption(
    [property: JsonPropertyName("library")] string Library,
    [property: JsonPropertyName("packages")] IReadOnlyList<string> Packages,
    [property: JsonPropertyName("namespaces")] IReadOnlyList<string>? Namespaces,
    [property: JsonPropertyName("public_types")] int PublicTypes,
    [property: JsonPropertyName("consumers")] IReadOnlyList<LibraryConsumer> Consumers,
    [property: JsonPropertyName("non_consumers")] IReadOnlyList<string> NonConsumers,
    [property: JsonPropertyName("possible_local_implementations")] IReadOnlyList<PossibleLocalImplementation> PossibleLocalImplementations,
    [property: JsonPropertyName("notes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Notes);
public sealed record WorkspaceDependencyNode(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("packages")] IReadOnlyList<string> Packages,
    [property: JsonPropertyName("library"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Library = false);
public sealed record WorkspaceDependencyEdge(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("packages")] IReadOnlyList<string> Packages,
    [property: JsonPropertyName("refs")] int Refs,
    [property: JsonPropertyName("via")] IReadOnlyList<ManifestReference> Via);
public sealed record WorkspaceDependencyGraph(
    [property: JsonPropertyName("nodes")] IReadOnlyList<WorkspaceDependencyNode> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<WorkspaceDependencyEdge> Edges);
public sealed record ConcernCode(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Line,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("matched_words")] int MatchedWords);
public sealed record ConcernDependency(
    [property: JsonPropertyName("package")] string Package,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Line,
    [property: JsonPropertyName("provider")] string Provider = "manifest");
public sealed record ConcernMember(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("code")] IReadOnlyList<ConcernCode> Code,
    [property: JsonPropertyName("decisions")] IReadOnlyList<Dictionary<string, object?>> Decisions,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<ConcernDependency> Dependencies);
public sealed record SimilarConcerns(
    [property: JsonPropertyName("concern")] string Concern,
    [property: JsonPropertyName("words")] IReadOnlyList<string>? Words,
    [property: JsonPropertyName("members")] IReadOnlyList<ConcernMember> Members,
    [property: JsonPropertyName("notes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Notes);

public interface IWorkspaceEvidenceSession
{
    Task<LibraryAdoption> AdoptionAsync(string library, CancellationToken cancellationToken);
    Task<SimilarConcerns> ConcernAsync(string concern, bool deep, CancellationToken cancellationToken);
}
