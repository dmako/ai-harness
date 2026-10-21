using System.Text.RegularExpressions;

namespace Harness.Core.Architecture;

public sealed record BoundaryRule(string Kind, IReadOnlyList<string[]> Sides, RuleReference Reference);
public sealed record BoundaryGroup(string Name, string[] Patterns, RuleReference Reference);
public sealed record BoundaryRuleError(RuleReference Reference, string Error);

/// <summary>Recorded groups and rules, collected from narrow to wide scope. Prose is ignored.</summary>
public sealed class BoundaryRules
{
    private static readonly Regex Start = Rx(@"(?i)^(group\s|layers\s*:|forbid\s*:|only\s*:)");
    private static readonly Regex GroupLine = Rx(@"(?i)^group\s+([A-Za-z][\w-]*)\s*=\s*(.+)$");
    private static readonly Regex Identifier = Rx(@"^[A-Za-z][\w-]*$");
    private readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);
    public List<BoundaryGroup> Groups { get; } = [];
    public List<BoundaryRule> Rules { get; } = [];
    public List<BoundaryRuleError> Errors { get; } = [];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Rule kinds use the frozen lowercase wire names.")]
    public void Add(string text, RuleReference reference)
    {
        foreach (var raw in text.Replace(". ", "\n", StringComparison.Ordinal).Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim().TrimStart('-', '*', '•').Trim().Trim('`');
            if (!Start.IsMatch(line))
                continue;
            var source = reference with { Line = line };
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var kind = (colon < 0 ? line : line[..colon]).Trim().ToLowerInvariant();
            var rest = colon < 0 ? "" : line[(colon + 1)..];
            switch (kind)
            {
                case "layers":
                    var sides = rest.Split('>').Select(List).ToArray();
                    if (sides.Length < 2 || sides.Any(side => side.Length is 0))
                        Errors.Add(new(source, "layers needs two or more layers, top first, separated by >"));
                    else
                        Rules.Add(new(kind, sides, source));
                    break;
                case "forbid":
                case "only":
                    var arrow = rest.IndexOf("->", StringComparison.Ordinal);
                    var from = List(arrow < 0 ? rest : rest[..arrow]);
                    var to = List(arrow < 0 ? "" : rest[(arrow + 2)..]);
                    if (arrow < 0 || from.Length is 0 || to.Length is 0)
                        Errors.Add(new(source, $"{kind} needs the form: {kind} A -> B"));
                    else
                        Rules.Add(new(kind, [from, to], source));
                    break;
                default:
                    var match = GroupLine.Match(line);
                    if (!match.Success || List(match.Groups[2].Value).Length is 0)
                        Errors.Add(new(source, "a group needs the form: group Name = pattern, pattern"));
                    else if (!Groups.Any(group => group.Name.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)))
                        Groups.Add(new(match.Groups[1].Value, List(match.Groups[2].Value), source));
                    break;
            }
        }
    }
    public bool Overrides(IReadOnlyList<BuildUnit> units) => Rules.Any(rule => rule.Kind is "layers" && units.Any(unit => Layer(unit, rule) >= 0));
    public IReadOnlyList<ArchitectureFinding> Apply(ProjectArchitecture architecture)
    {
        var units = architecture.Projects.Cast<BuildUnit>().ToArray();
        return architecture.Findings.Where(finding => finding.Kind is not "layering" || !Overrides(units)).Concat(Check(units).Select(finding => ArchitectureAnalyzer.Complete(finding))).ToArray();
    }
    public IReadOnlyList<ArchitectureFinding> Check(IReadOnlyList<BuildUnit> units)
    {
        var byPath = units.ToDictionary(unit => unit.Path, StringComparer.Ordinal);
        List<ArchitectureFinding> found = [];
        foreach (var unit in units)
            foreach (var target in unit.References)
            {
                if (!byPath.TryGetValue(target, out var other) || unit.Path == target)
                    continue;
                foreach (var rule in Rules)
                {
                    var why = Violation(unit, other, rule);
                    if (why.Length is 0)
                        continue;
                    found.Add(new()
                    {
                        Kind = "boundary",
                        Level = rule.Reference.Type is "constraint" ? "problem" : "warn",
                        Title = unit.Name + " depends on " + other.Name + ", against " + rule.Reference.Line,
                        Why = why + ". The rule is recorded knowledge (" + (rule.Reference.Type ?? "rule") + " " + rule.Reference.Memory + ").",
                        Fix = "Move what " + unit.Name + " needs to where the rule allows, or turn the dependency around (an interface the allowed side owns); if the rule no longer holds, change it.",
                        Project = unit.Path,
                        Evidence = ArchitectureAnalyzer.At(unit, other.Path),
                        Key = ArchitectureAnalyzer.Key("boundary", unit.Path, other.Path, rule.Reference.Line),
                        Basis = "fact",
                        Source = ArchitectureAnalyzer.Harness("boundary rules"),
                        Rule = rule.Reference
                    });
                }
            }
        found.AddRange(Errors.Select(error => new ArchitectureFinding { Kind = "rule-unclear", Level = "info", Title = "A boundary rule is not understood: " + error.Reference.Line, Why = error.Error + "; it is not checked until it parses.", Fix = "Correct the " + (error.Reference.Type ?? "memory") + " " + error.Reference.Memory + " (Knowledge).", Key = ArchitectureAnalyzer.Key("rule-unclear", error.Reference.Memory, error.Reference.Line), Basis = "fact", Source = ArchitectureAnalyzer.Harness("boundary rules"), Rule = error.Reference }));
        return found;
    }
    private string Violation(BuildUnit unit, BuildUnit other, BoundaryRule rule)
    {
        var from = string.Join(", ", rule.Sides[0]);
        var to = string.Join(", ", rule.Sides[1]);
        return rule.Kind switch
        {
            "layers" when Layer(unit, rule) is var lower && lower >= 0 && Layer(other, rule) is var upper && upper >= 0 && lower > upper => $"{unit.Name} is in {string.Join(", ", rule.Sides[lower])}, below {other.Name}'s {string.Join(", ", rule.Sides[upper])}: a layer may depend only on those below it",
            "forbid" when Any(unit, rule.Sides[0]) && Any(other, rule.Sides[1]) && !Any(unit, rule.Sides[1]) => $"{unit.Name} ({from}) may not depend on {other.Name} ({to})",
            "only" when Any(unit, rule.Sides[0]) && !Any(other, rule.Sides[0]) && !Any(other, rule.Sides[1]) => $"{unit.Name} ({from}) may depend only on {to}, and {other.Name} is not",
            _ => "",
        };
    }
    private int Layer(BuildUnit unit, BoundaryRule rule)
    {
        for (var index = 0; index < rule.Sides.Count; index++)
            if (Any(unit, rule.Sides[index]))
                return index;
        return -1;
    }
    private bool Any(BuildUnit unit, string[] names) => names.Any(name => In(unit, name));
    private bool In(BuildUnit unit, string name)
    {
        var patterns = Groups.FirstOrDefault(group => group.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Patterns ?? (Identifier.IsMatch(name) ? [name, "*." + name, "*/" + name] : new[] { name });
        foreach (var pattern in patterns)
        {
            if (!_patterns.TryGetValue(pattern, out var regex))
            {
                regex = Rx("(?i)^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$");
                _patterns.Add(pattern, regex);
            }
            if (regex.IsMatch(unit.Name) || regex.IsMatch(unit.Path) || regex.IsMatch(Path.GetFileNameWithoutExtension(unit.Path)))
                return true;
        }
        return false;
    }
    private static string[] List(string text) => text.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(word => word.Trim('`')).Where(word => word.Length > 0).ToArray();
    private static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.ECMAScript, TimeSpan.FromSeconds(1));
}
