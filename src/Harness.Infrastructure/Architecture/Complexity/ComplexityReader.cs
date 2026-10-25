using System.Globalization;
using Harness.Core.Architecture;
using Harness.Infrastructure.Architecture.Clones;

namespace Harness.Infrastructure.Architecture.Complexity;

/// <summary>Cyclomatic complexity findings from the members the clone extractor already tokenized.</summary>
public sealed class ComplexityReader(CloneExtractor extractor)
{
    /// <summary>The source tool of findings whose value Microsoft.CodeAnalysis.Metrics measured.</summary>
    public const string Measured = "Microsoft.CodeAnalysis.Metrics";

    /// <summary>
    /// Findings for the enabled languages (csharp, go, typescript), each with its own thresholds. Exact .NET values,
    /// when given (the background run for HEAD), replace the count of the C# member at the same path and line;
    /// members only the run reports (properties, members the count missed) are added.
    /// </summary>
    public IReadOnlyList<ArchitectureFinding> Read(
        IReadOnlyDictionary<string, ComplexityThresholds> languages,
        IReadOnlyList<MeasuredMember>? exact = null
    )
    {
        ArgumentNullException.ThrowIfNull(languages);
        var counted = extractor.Complexity().Where(member => languages.ContainsKey(member.Language)).ToArray();
        // Overloads share a name: their parameter count tells them apart, whichever of them is reported.
        var overloaded = counted
            .GroupBy(member => member.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var members =
            languages.ContainsKey("csharp") && exact is { Count: > 0 }
                ? WithExact(counted, exact)
                : counted.Select(member => (Member: member, Measured: (MeasuredMember?)null)).ToArray();
        return members
            .Select(pair =>
                (pair.Member, pair.Measured, Level: languages[pair.Member.Language].Level(pair.Member.Complexity))
            )
            .Where(item => item.Level is not null)
            .OrderByDescending(item => item.Member.Complexity)
            .ThenBy(item => item.Member.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Member.Line)
            .Select(item => Finding(item.Member, item.Level!, overloaded.Contains(item.Member.Id), item.Measured))
            .ToArray();
    }

    private static (ComplexityMember Member, MeasuredMember? Measured)[] WithExact(
        IReadOnlyList<ComplexityMember> counted,
        IReadOnlyList<MeasuredMember> exact
    )
    {
        var byPlace = exact
            .GroupBy(member => (member.Path, member.Line))
            .ToDictionary(group => group.Key, group => group.MaxBy(member => member.Complexity)!);
        HashSet<(string, int)> used = [];
        List<(ComplexityMember, MeasuredMember?)> output = [];
        foreach (var member in counted)
        {
            if (member.Language is "csharp" && byPlace.TryGetValue((member.Path, member.Line), out var measured))
            {
                used.Add((member.Path, member.Line));
                output.Add((member with { Complexity = measured.Complexity }, measured));
            }
            else
            {
                output.Add((member, null));
            }
        }
        // Generated, test and vendored files stay out, as they do for the count from source.
        foreach (
            var (place, measured) in byPlace.Where(pair =>
                !used.Contains(pair.Key) && CloneExtractor.Reads(pair.Key.Path)
            )
        )
        {
            output.Add((new(place.Path, measured.Name, place.Line, 0, measured.Complexity, 0, "csharp"), measured));
        }
        return output.ToArray();
    }

    private static ArchitectureFinding Finding(
        ComplexityMember member,
        string level,
        bool overloaded,
        MeasuredMember? measured
    )
    {
        var complexity = member.Complexity.ToString(CultureInfo.InvariantCulture);
        var id = overloaded ? member.Id + "/" + member.Parameters.ToString(CultureInfo.InvariantCulture) : member.Id;
        var risk = level switch
        {
            "problem" => "too many to test: a hotspot that needs breaking down",
            "warn" => "hard to test exhaustively: a strong refactoring candidate",
            _ => "still manageable, but worth a second look in review",
        };
        var how = measured is null
            ? Counted(member.Language)
            : "Measured by Microsoft.CodeAnalysis.Metrics (maintainability index "
                + measured.Maintainability.ToString(CultureInfo.InvariantCulture)
                + ").";
        return new()
        {
            Kind = "complexity",
            Level = level,
            Key = ArchitectureAnalyzer.Key("complexity", id),
            Measure = member.Complexity,
            Rank = member.Complexity,
            Title = $"{member.Name} has cyclomatic complexity {complexity}",
            Why = $"{complexity} independent paths run through it, {risk}. " + how,
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
            Source = ArchitectureAnalyzer.Harness(measured is null ? "internal/complexity" : Measured),
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
