using AwesomeAssertions;
using Harness.Core.Architecture;
using Harness.Infrastructure.Architecture.Clones;
using Harness.Infrastructure.Architecture.Complexity;
using Harness.Infrastructure.Findings;
using Xunit;

namespace Harness.Unit.Architecture;

public sealed class ComplexityReaderTests
{
    // Expected values are what Microsoft.CodeAnalysis.Metrics 5.6.0 reports for the same members (TASK-0040).
    private const string Samples = """
        using System;
        using System.Linq;

        namespace Example;

        public class Samples
        {
            public void One() { Console.WriteLine("x"); }

            public void Two(bool a) { if (a) { Console.WriteLine("x"); } }

            public void Three(bool a, bool b) { if (a || b) { Console.WriteLine("x"); } }

            public void Eight(DayOfWeek day)
            {
                switch (day)
                {
                    case DayOfWeek.Monday: break;
                    case DayOfWeek.Tuesday: break;
                    case DayOfWeek.Wednesday: break;
                    case DayOfWeek.Thursday: break;
                    case DayOfWeek.Friday: break;
                    case DayOfWeek.Saturday: goto case DayOfWeek.Friday;
                    case DayOfWeek.Sunday: break;
                }
            }

            public int Lambda(int[] xs) => xs.Count(x => x > 0 && x < 10);

            public int Local(int x)
            {
                return Inner(x);
                static int Inner(int y)
                {
                    if (y > 0) { return 1; }
                    if (y < 0) { return -1; }
                    return 0;
                }
            }

            public string SwitchExpr(int x) => x switch { 0 => "a", 1 => "b", _ => "c" };

            public int? Nulls(string? s, int? d) => s?.Length ?? d;

            public void Loops(int[] xs)
            {
                foreach (var x in xs) { }
                for (var i = 0; i < 3; i++) { }
                while (false) { }
                do { } while (false);
            }

            public void Catch()
            {
                try { Console.WriteLine(); }
                catch (InvalidOperationException e) when (e.Message.Length > 0) { }
                catch (Exception) { }
            }

            public bool Pattern(object o) => o is int or long and not 0;

            public int Ternary(bool a) => a ? 1 : 2;

            public void Coalesce(string? s) { s ??= "x"; }

            public T Generic<T>(T? value) where T : class => value ?? throw new ArgumentNullException(nameof(value));

            public (string Name, int Count) Tuple(int[]? xs) => (xs?[0] > 0 ? "some" : "none", xs?.Length ?? 0);

            public string Interpolated(bool a, string? b) => $"{(a ? 1 : 0)} {b?.Length ?? 0} {{literal}}";

            public int Nullable(int? a, string?[] names)
            {
                int? copy = a;
                string? Name(int? i) { return i is null ? null : names[i.Value]; }
                return copy ?? Name(a)?.Length ?? 0;
            }

            public Samples(bool a) : this(a ? 1 : 2) { }

            private Samples(int x) { }
        }
        """;

    [Theory]
    [InlineData("Samples.One", 1)]
    [InlineData("Samples.Two", 2)]
    [InlineData("Samples.Three", 3)]
    [InlineData("Samples.Eight", 8)]
    [InlineData("Samples.Lambda", 2)]
    [InlineData("Samples.Local", 3)]
    [InlineData("Samples.SwitchExpr", 1)]
    [InlineData("Samples.Nulls", 3)]
    [InlineData("Samples.Loops", 5)]
    [InlineData("Samples.Catch", 1)]
    [InlineData("Samples.Pattern", 1)]
    [InlineData("Samples.Ternary", 2)]
    [InlineData("Samples.Coalesce", 1)]
    [InlineData("Samples.Generic", 2)]
    [InlineData("Samples.Tuple", 5)]
    [InlineData("Samples.Interpolated", 4)]
    [InlineData("Samples.Nullable", 5)]
    public void MembersCountBranchesAsCodeMetricsDo(string name, int complexity)
    {
        using var tree = new ReaderTree(new() { ["src/Samples.cs"] = Samples });
        var members = new CloneExtractor(tree.Repository()).Complexity();
        members.Should().ContainSingle(member => member.Name == name).Which.Complexity.Should().Be(complexity);
    }

    [Fact]
    public void LocalFunctionsFoldIntoTheirMemberAndOverloadsKeepTheirLines()
    {
        using var tree = new ReaderTree(new() { ["src/Samples.cs"] = Samples });
        var members = new CloneExtractor(tree.Repository()).Complexity();
        members.Should().NotContain(member => member.Name.EndsWith(".Inner") || member.Name.EndsWith(".Name"));
        members
            .Where(member => member.Name == "Samples.Samples")
            .Select(member => (member.Parameters, member.Complexity))
            .Should()
            .BeEquivalentTo(new[] { (1, 2), (1, 1) });
        members.Single(member => member.Name == "Samples.Eight").Cases.Should().Be(7);
    }

    [Fact]
    public void FindingsFollowThresholdsAndKeepTheirKeyAcrossLevels()
    {
        var code =
            "namespace Example;\npublic class Router\n{\n"
            + Method("Small", 4)
            + Method("Medium", 14)
            + Method("Large", 30)
            + Method("Huge", 60)
            + Switch("Map", 24)
            + Method("Twice", 12, "int a")
            + Method("Twice", 2, "int a, int b")
            + "}\n";
        using var tree = new ReaderTree(
            new() { ["src/Router.cs"] = code, ["test/Router.Tests/RouterTests.cs"] = code }
        );
        var reader = new ComplexityReader(new CloneExtractor(tree.Repository()));

        var findings = reader.Read(new());

        findings
            .Select(finding => (finding.Title, finding.Level))
            .Should()
            .Equal(
                ("Router.Huge has cyclomatic complexity 60", "problem"),
                ("Router.Large has cyclomatic complexity 30", "warn"),
                ("Router.Map has cyclomatic complexity 25", "warn"),
                ("Router.Medium has cyclomatic complexity 14", "info"),
                ("Router.Twice has cyclomatic complexity 12", "info")
            );
        var large = findings.Single(finding => finding.Title.StartsWith("Router.Large", StringComparison.Ordinal));
        large.Kind.Should().Be("complexity");
        large.Key.Should().Be("complexity:src/Router.cs#Router.Large");
        large.Measure.Should().Be(30);
        large.Rank.Should().Be(30);
        large.Basis.Should().Be("fact");
        large.Source.Should().Be(new FindingSource("harness", "internal/complexity"));
        large.Evidence.Should().Equal($"src/Router.cs:{LineOf(code, "Large")} (Router.Large)");
        large.Fix.Should().Contain("guard clauses");
        large.Why.Should().Contain("Microsoft's code metrics");
        findings
            .Single(finding => finding.Title.StartsWith("Router.Map", StringComparison.Ordinal))
            .Fix.Should()
            .Contain("lookup table");
        findings
            .Single(finding => finding.Title.StartsWith("Router.Twice", StringComparison.Ordinal))
            .Key.Should()
            .Be("complexity:src/Router.cs#Router.Twice/1");

        var stricter = reader.Read(new(Note: 5, Warn: 10, Problem: 25));
        var moved = stricter.Single(finding => finding.Key == large.Key);
        moved.Level.Should().Be("problem");
        FindingStore.Hash(moved).Should().NotBe(FindingStore.Hash(large));
        stricter.Should().HaveCount(5);
    }

    private static string Method(string name, int complexity, string parameters = "int a") =>
        $"    public int {name}({parameters})\n    {{\n"
        + string.Concat(Enumerable.Repeat("        if (a > 0) { a--; }\n", complexity - 1))
        + "        return a;\n    }\n";

    private static string Switch(string name, int cases) =>
        $"    public int {name}(int a)\n    {{\n        switch (a)\n        {{\n"
        + string.Concat(Enumerable.Range(0, cases).Select(value => $"            case {value}: return {value + 1};\n"))
        + "            default: return 0;\n        }\n    }\n";

    private static int LineOf(string code, string name) =>
        code.Split('\n').ToList().FindIndex(line => line.Contains($" {name}(", StringComparison.Ordinal)) + 1;
}
