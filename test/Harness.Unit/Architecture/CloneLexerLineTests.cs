using AwesomeAssertions;
using Harness.Infrastructure.Architecture.Clones;
using Xunit;

namespace Harness.Unit.Architecture;

/// <summary>Token lines after literals and comments, read back through the members' lines (TASK-0042).</summary>
public sealed class CloneLexerLineTests
{
    [Theory]
    [InlineData("src/A.cs", "class C\n{\n    string x = \"abc\n    ;\n    void Y() { }\n}\n", " Y(")]
    [InlineData("src/A.cs", "class C\n{\n    char c = 'a\n    ;\n    void Y() { }\n}\n", " Y(")]
    [InlineData(
        "src/A.cs",
        "class C\n{\n    string x = \"abc\n    , y = \"def\n    ;\n    void Y() { }\n}\n",
        " Y("
    )]
    [InlineData("src/a.ts", "const x = \"abc\nfunction y() { return 1; }\n", "function y(")]
    [InlineData("src/a.ts", "const x = 'abc\nconst z = 'def\nfunction y() { return 1; }\n", "function y(")]
    [InlineData("src/a.go", "package p\n\nvar x = \"abc\nfunc y() int { return 1 }\n", "func y(")]
    public void UnterminatedStringsEndAtTheLineBreakAndCountItOnce(string path, string code, string declaration)
    {
        LineOf(path, code).Should().Be(ExpectedLine(code, declaration));
    }

    [Theory]
    [InlineData("src/A.cs", "class C\n{\n    string x = \"abc\";\n    void Y() { }\n}\n", " Y(")]
    [InlineData("src/A.cs", "class C\n{\n    string x = @\"a\nb\n\"\"c\"\"\";\n    void Y() { }\n}\n", " Y(")]
    [InlineData("src/A.cs", "class C\n{\n    string x = $@\"a\n{1}\nb\";\n    void Y() { }\n}\n", " Y(")]
    [InlineData("src/A.cs", "class C\n{\n    string x = \"\"\"\n        a \"q\"\n        \"\"\";\n    void Y() { }\n}\n", " Y(")]
    [InlineData("src/A.cs", "class C\n{\n    /* one\n       two\n    */\n    void Y() { }\n}\n", " Y(")]
    [InlineData("src/A.cs", "class C\n{\n    string x = \"a\\\"b\";\n    void Y() { }\n}\n", " Y(")]
    [InlineData("src/a.ts", "const x = `a\nb\n${1}`;\nfunction y() { return 1; }\n", "function y(")]
    [InlineData("src/a.ts", "/* one\n two */\nfunction y() { return 1; }\n", "function y(")]
    [InlineData("src/a.go", "package p\n\nvar x = `a\nb`\nfunc y() int { return 1 }\n", "func y(")]
    [InlineData("src/a.go", "package p\n\n/* one\ntwo */\nfunc y() int { return 1 }\n", "func y(")]
    public void TerminatedLiteralsAndCommentsKeepTheirLines(string path, string code, string declaration)
    {
        LineOf(path, code).Should().Be(ExpectedLine(code, declaration));
    }

    [Theory]
    [InlineData("const x = \"a\\\nb\";\nfunction y() { return 1; }\n")]
    [InlineData("const x = 'a\\\nb';\nfunction y() { return 1; }\n")]
    [InlineData("const x = \"a\\\nb\\\nc\";\nfunction y() { return 1; }\n")]
    [InlineData("const x = \"a\\\r\nb\";\r\nfunction y() { return 1; }\r\n")]
    [InlineData("const x = 'a\\\r\nb';\r\nfunction y() { return 1; }\r\n")]
    public void LineContinuationsInStringsCountTheirLineBreak(string code)
    {
        LineOf("src/a.ts", code).Should().Be(ExpectedLine(code, "function y("));
    }

    private static int LineOf(string path, string code)
    {
        using var tree = new ReaderTree(new() { [path] = code });
        return new CloneExtractor(tree.Repository())
            .Complexity()
            .Should()
            .ContainSingle(member => member.Name == "y" || member.Name.EndsWith(".Y", StringComparison.Ordinal))
            .Which.Line;
    }

    private static int ExpectedLine(string code, string declaration) =>
        code.Split('\n').ToList().FindIndex(line => line.Contains(declaration, StringComparison.Ordinal)) + 1;
}
