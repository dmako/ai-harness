namespace Harness.Core.Architecture;

public sealed record EvaluatedPackage(string Name, string Version, string At, IReadOnlyList<string> Frameworks, bool Conditional = false, bool Unresolved = false);
public sealed record EvaluatedReference(string Path, string At, IReadOnlyList<string> Frameworks, bool Conditional = false);
public sealed record EvaluatedProject(IReadOnlyList<string> Frameworks, IReadOnlyDictionary<string, string> Properties,
    IReadOnlyDictionary<string, string> PropertyLocations, IReadOnlyList<EvaluatedPackage> Packages, IReadOnlyList<EvaluatedReference> References);
public sealed record ManifestDependency(string Name, string Version, string Path, int Line, bool Unresolved = false);
public sealed record OfferedPackage(string Name, string Ecosystem, bool Published = true);
