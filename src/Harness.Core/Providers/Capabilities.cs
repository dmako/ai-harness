namespace Harness.Core.Providers;

/// <summary>Stable, extensible wire names shared by all provider adapters.</summary>
public static class Capabilities
{
    public const string SymbolSearch = "code.symbol_search";
    public const string TextSearch = "code.text_search";
    public const string FileSearch = "code.file_search";
    public const string Overview = "project.overview";
    public const string Callers = "code.callers";
    public const string References = "code.references";
    public const string Context = "code.context";
    public const string Impact = "code.impact";
    public const string Flow = "code.flow";
    public const string PackageSearch = "package.search";
    public const string PackageInfo = "package.info";
    public const string PackageTypes = "package.types";
    public const string PackageTypeDefinition = "package.type_definition";
    public const string ArchDependencies = "arch.dependencies";
    public const string ArchCycles = "arch.cycles";
    public const string ArchChangeCoupling = "arch.change_coupling";
    public const string ArchDuplicates = "arch.duplicates";
    public const string ArchDeadCode = "arch.dead_code";

    public static IReadOnlyList<string> Architecture { get; } =
        Array.AsReadOnly(new[] { ArchDependencies, ArchCycles, ArchChangeCoupling, ArchDuplicates, ArchDeadCode });

    public static bool IsPackage(string capability) => capability.StartsWith("package.", StringComparison.Ordinal);

    public static bool IsArchitecture(string capability) => capability.StartsWith("arch.", StringComparison.Ordinal);

    public static string DefaultEvidence(string capability) =>
        capability switch
        {
            TextSearch or FileSearch or ArchChangeCoupling => "exact",
            _ => "inferred",
        };
}
