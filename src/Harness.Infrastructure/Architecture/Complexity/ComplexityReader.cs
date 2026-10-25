using System.Globalization;
using Harness.Core.Architecture;
using Harness.Infrastructure.Architecture.Clones;

namespace Harness.Infrastructure.Architecture.Complexity;

/// <summary>Cyclomatic complexity findings from the members the clone extractor already tokenized.</summary>
public sealed class ComplexityReader(CloneExtractor extractor)
{
    /// <summary>Findings for the enabled languages (csharp, go, typescript), each with its own thresholds.</summary>
    public IReadOnlyList<ArchitectureFinding> Read(IReadOnlyDictionary<string, ComplexityThresholds> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var members = extractor.Complexity().Where(member => languages.ContainsKey(member.Language)).ToArray();
        // Overloads share a name: their parameter count tells them apart, whichever of them is reported.
        var overloaded = members
            .GroupBy(member => member.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return members
            .Select(member => (Member: member, Level: languages[member.Language].Level(member.Complexity)))
            .Where(pair => pair.Level is not null)
            .OrderByDescending(pair => pair.Member.Complexity)
            .ThenBy(pair => pair.Member.Path, StringComparer.Ordinal)
            .ThenBy(pair => pair.Member.Line)
            .Select(pair => Finding(pair.Member, pair.Level!, overloaded.Contains(pair.Member.Id)))
            .ToArray();
    }

    private static ArchitectureFinding Finding(ComplexityMember member, string level, bool overloaded)
    {
        var complexity = member.Complexity.ToString(CultureInfo.InvariantCulture);
        var id = overloaded ? member.Id + "/" + member.Parameters.ToString(CultureInfo.InvariantCulture) : member.Id;
        var risk = level switch
        {
            "problem" => "too many to test: a hotspot that needs breaking down",
            "warn" => "hard to test exhaustively: a strong refactoring candidate",
            _ => "still manageable, but worth a second look in review",
        };
        return new()
        {
            Kind = "complexity",
            Level = level,
            Key = ArchitectureAnalyzer.Key("complexity", id),
            Measure = member.Complexity,
            Rank = member.Complexity,
            Title = $"{member.Name} has cyclomatic complexity {complexity}",
            Why = $"{complexity} independent paths run through it, {risk}. " + Counted(member.Language),
            Fix =
                2 * member.Cases >= member.Complexity
                    ? "Most paths are switch cases: a lookup table or one type per case (polymorphism) keeps each"
                        + " case small. A flat switch that only maps values can stay: mark it by design."
                    : "Extract its separate steps into methods of their own, and return early with guard clauses"
                        + " instead of nesting conditions.",
            Project = Path.GetDirectoryName(member.Path)?.Replace('\\', '/') is { Length: > 0 } dir ? dir : ".",
            Evidence = [$"{member.Path}:{member.Line.ToString(CultureInfo.InvariantCulture)} ({member.Name})"],
            Language = member.Language,
            Basis = "fact",
            Source = ArchitectureAnalyzer.Harness("internal/complexity"),
        };
    }

    private static string Counted(string language) =>
        language switch
        {
            "go" => "Counted from its source as gocyclo counts it (if, for, case clauses and &&/||, with its"
                + " closures).",
            "typescript" => "Counted from its source as ESLint's complexity rule counts it (branches, loops, case,"
                + " catch, ?:, ?., ?? and &&/||, with the arrows and functions inside it).",
            _ => "Counted from its source as Microsoft's code metrics count it (branches, loops, case labels, ?:,"
                + " ?., ?? and &&/||, with its lambdas and local functions); the compiler's value can differ by one"
                + " or two.",
        };
}
