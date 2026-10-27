using AwesomeAssertions;
using Harness.Infrastructure.Architecture.Clones;
using Xunit;

namespace Harness.Unit.Architecture;

/// <summary>
/// Token lines after literals and comments, read back through the members' lines (TASK-0042, TASK-0043), and
/// continued strings as one token (TASK-0044).
/// </summary>
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

    [Theory]
    [InlineData("const x = \"a\\\r\nb\";\r\nlet z = 1;\r\n", "\"a\\\r\nb\"")]
    [InlineData("const x = 'a\\\r\nb';\r\nlet z = 1;\r\n", "'a\\\r\nb'")]
    [InlineData("const x = 'a\\\nb';\nlet z = 1;\n", "'a\\\nb'")]
    public void LineContinuationsStayOneStringToken(string code, string literal)
    {
        Tokens(code, "ts")
            .Should()
            .Equal(
                ('i', "const", 1),
                ('i', "x", 1),
                ('p', "=", 1),
                ('s', literal, 1),
                ('p', ";", 2),
                ('i', "let", 3),
                ('i', "z", 3),
                ('p', "=", 3),
                ('n', "1", 3),
                ('p', ";", 3)
            );
    }

    /// <summary>The lexer's tokens, read by reflection: CloneLexer is internal to the infrastructure.</summary>
    private static List<(char Kind, string Text, int Line)> Tokens(string source, string language)
    {
        var lexer = typeof(CloneExtractor).Assembly.GetType(
            "Harness.Infrastructure.Architecture.Clones.CloneLexer",
            throwOnError: true
        )!;
        var tokenize = lexer.GetMethod(
            "Tokenize",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        )!;
        return ((System.Collections.IEnumerable)tokenize.Invoke(null, [source, language])!)
            .Cast<object>()
            .Select(token =>
                (
                    (char)token.GetType().GetProperty("Kind")!.GetValue(token)!,
                    (string)token.GetType().GetProperty("Text")!.GetValue(token)!,
                    (int)token.GetType().GetProperty("Line")!.GetValue(token)!
                )
            )
            .ToList();
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
