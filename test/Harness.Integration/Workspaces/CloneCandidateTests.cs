using AwesomeAssertions;
using Harness.Core.Architecture;
using Harness.Infrastructure.Architecture.Clones;
using Xunit;

namespace Harness.Integration.Workspaces;

public sealed class CloneCandidateTests
{
    private static string[] Run(string prefix, int count) =>
        Enumerable.Range(0, count).Select(index => prefix + index).ToArray();

    private static CloneFunction Function(string name, IReadOnlyList<string> tokens) =>
        new()
        {
            Path = name + ".cs",
            Name = name,
            Line = 1,
            End = 10,
            Canonical = tokens,
            Shape = tokens,
        };

    [Fact]
    public void CandidatePairsShareEnoughWindowsOfSimilarLength()
    {
        var a = Run("a", 40);
        var c = Run("c", 40);
        CloneFunction[] input =
        [
            Function("A", a),
            Function("B", [.. a[..20], "changed", .. a[21..]]),
            Function("C", c),
            Function("D", a[..20]),
            Function("E", [.. c[..10], "x", .. c[11..30], "y", .. c[31..]]),
            Function("F", [.. a[..20], .. c[20..]]),
            Function("G", ["é", "ü", "名", .. a[3..]]),
        ];

        // B differs from A in one token, E from C in two; D is too short beside A, F shares too few windows with
        // either half, and G shares A's tail but not its non-ASCII head.
        CloneComparison
            .Candidates(input, _ => true, function => function.Canonical, 4, 100)
            .Should()
            .Equal((0, 1), (0, 6), (1, 6), (2, 4));

        // Functions left out are neither paired nor counted; windows shared by more than 'common' say nothing.
        CloneComparison
            .Candidates(input, index => index is not 6, function => function.Canonical, 4, 100)
            .Should()
            .Equal((0, 1), (2, 4));
        CloneComparison.Candidates(input, _ => true, function => function.Canonical, 4, 1).Should().BeEmpty();
        CloneComparison.Candidates(input, _ => true, function => function.Canonical, 41, 100).Should().BeEmpty();
    }
}
