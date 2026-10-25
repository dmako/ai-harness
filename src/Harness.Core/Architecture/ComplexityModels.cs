namespace Harness.Core.Architecture;

/// <summary>One member's lexical cyclomatic complexity; lambdas and local functions count toward it.</summary>
public sealed record ComplexityMember(string Path, string Name, int Line, int Parameters, int Complexity, int Cases)
{
    public string Id => Path + "#" + Name;
}

/// <summary>The lowest complexity of each level; members below <see cref="Note"/> are not reported.</summary>
public sealed record ComplexityThresholds(int Note = 11, int Warn = 21, int Problem = 51)
{
    public string? Level(int complexity) =>
        complexity switch
        {
            _ when complexity >= Problem => "problem",
            _ when complexity >= Warn => "warn",
            _ when complexity >= Note => "info",
            _ => null,
        };
}
