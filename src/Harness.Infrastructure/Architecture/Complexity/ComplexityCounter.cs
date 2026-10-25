using Harness.Core.Architecture;
using Harness.Infrastructure.Architecture.Clones;

namespace Harness.Infrastructure.Architecture.Complexity;

/// <summary>
/// Lexical cyclomatic complexity per member, counted from the clone extractor's tokens. C# follows
/// Microsoft.CodeAnalysis.Metrics: one plus each if, case label, loop, conditional (?:), conditional access
/// (?. ?[]), ?? and short-circuit operator; switch-expression arms, catch, exception filters, patterns and ??=
/// add nothing. Go follows gocyclo and TypeScript/JavaScript ESLint's complexity rule (see their partial files).
/// Lambdas, closures and nested functions count toward the member around them.
/// </summary>
internal static partial class ComplexityCounter
{
    private const int SignatureReach = 400;

    private sealed record Span(string Name, int Start, int End);

    /// <summary>A declared member: its name token, parameter list, the range counted, and an owner if not its type.</summary>
    private sealed record Declared(int Name, int Open, int Close, int End, string? Owner = null);

    public static List<ComplexityMember> CSharp(string path, List<CodeToken> tokens) =>
        Members(path, tokens, "csharp", CSharpDeclaration);

    private static List<ComplexityMember> Members(
        string path,
        List<CodeToken> tokens,
        string language,
        Func<List<CodeToken>, int, Declared?> declaration
    )
    {
        var types = Types(tokens);
        List<Declared> declared = [];
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (declaration(tokens, index) is { } member)
            {
                declared.Add(member);
            }
        }
        List<ComplexityMember> output = [];
        foreach (var member in declared)
        {
            // A nested function belongs to the member around it, as lambdas and closures do.
            if (declared.Any(outer => outer.Open < member.Name && member.End <= outer.End && outer != member))
            {
                continue;
            }

            var owner =
                member.Owner
                ?? types
                    .Where(span => span.Start < member.Name && member.Name < span.End)
                    .MaxBy(span => span.Start)
                    ?.Name;
            var (complexity, cases) = Count(tokens, member.Close + 1, member.End, language);
            output.Add(
                new(
                    path,
                    (owner is { Length: > 0 } ? owner + "." : "") + tokens[member.Name].Text,
                    tokens[member.Name].Line,
                    Parameters(tokens, member.Open, member.Close),
                    complexity,
                    cases,
                    language
                )
            );
        }
        return output;
    }

    private static (int Complexity, int Cases) Count(List<CodeToken> tokens, int start, int end, string language) =>
        language switch
        {
            "go" => CountGo(tokens, start, end),
            "typescript" => CountScript(tokens, start, end),
            _ => CountCSharp(tokens, start, end),
        };

    /// <summary>A method, constructor or local function declared at index: its name, parameter list and body end.</summary>
    private static Declared? CSharpDeclaration(List<CodeToken> tokens, int index)
    {
        var token = tokens[index];
        if (
            token.Kind is not 'i'
            || CloneLexer.Control.Contains(token.Text)
            || CloneLexer.Keywords.Contains(token.Text)
            || index is 0
            || !CloneLexer.TypeLike(tokens[index - 1]) && !TupleType(tokens, index - 1)
            || tokens[index - 1].Text is "new"
            || CloneLexer.Declares.Contains(tokens[index - 1].Text)
        )
        {
            return null;
        }

        var parameters = tokens[index + 1].Text switch
        {
            "(" => index + 1,
            "<" => GenericEnd(tokens, index + 1) is var close and > 0
            && close + 1 < tokens.Count
            && tokens[close + 1].Text is "("
                ? close + 1
                : -1,
            _ => -1,
        };
        if (parameters < 0 || CloneLexer.Match(tokens, parameters) is not (> 0 and var end))
        {
            return null;
        }

        var depth = 0;
        for (var cursor = end + 1; cursor < tokens.Count && cursor <= end + SignatureReach; cursor++)
        {
            var text = tokens[cursor].Kind is 'p' ? tokens[cursor].Text : "";
            switch (text)
            {
                case "(" or "[":
                    depth++;
                    break;
                case ")" or "]":
                    depth--;
                    break;
                case "{" when depth is 0:
                    var block = CloneLexer.Match(tokens, cursor);
                    return block < 0 ? null : new(index, parameters, end, block);
                case "=>" when depth is 0:
                    var expression = ExpressionEnd(tokens, cursor + 1);
                    return expression < 0 ? null : new(index, parameters, end, expression);
                case ";" or "}" or "=" or "," when depth is 0:
                    return null;
            }
            if (depth < 0)
            {
                return null;
            }
        }
        return null;
    }

    private static (int Complexity, int Cases) CountCSharp(List<CodeToken> tokens, int start, int end)
    {
        var complexity = 1;
        var cases = 0;
        for (var index = start; index <= end && index < tokens.Count; index++)
        {
            var token = tokens[index];
            switch (token.Kind, token.Text)
            {
                case ('i', "if" or "while" or "for" or "foreach"):
                case ('p', "&&" or "||" or "?."):
                    complexity++;
                    break;
                case ('i', "case") when Previous(tokens, index) is not "goto":
                    complexity++;
                    cases++;
                    break;
                case ('s', _):
                    complexity += Interpolated(token.Text, "csharp");
                    break;
                case ('p', "?") when Previous(tokens, index) is "?":
                    break;
                case ('p', "?") when Next(tokens, index) is "?":
                    // ?? branches; ??= only assigns.
                    complexity += Next(tokens, index + 1) is "=" ? 0 : 1;
                    break;
                case ('p', "?") when !Nullable(tokens, index):
                    complexity++;
                    break;
            }
        }
        return (complexity, cases);
    }

    /// <summary>Whether ? at index marks a nullable type rather than a conditional or a conditional index.</summary>
    private static bool Nullable(List<CodeToken> tokens, int index)
    {
        var next = Next(tokens, index);
        if (next is "[")
        {
            return Next(tokens, index + 1) is "]";
        }

        if (next is ">" or "," or ")" or "]" or ";" or "=" or "{" or "}" or "")
        {
            return true;
        }

        if (tokens[index + 1].Kind is not 'i' || CloneLexer.Keywords.Contains(next))
        {
            return false;
        }

        // int? count = ..., (string? name, ...), foreach (int? item in ...), string? Local(...) { ... }
        return Next(tokens, index + 1) switch
        {
            "=" or ";" or "," or ")" or "{" or "=>" or "in" => true,
            "(" => CloneLexer.Match(tokens, index + 2) is > 0 and var close && Next(tokens, close) is "{" or "=>",
            _ => false,
        };
    }

    private static string Next(List<CodeToken> tokens, int index) =>
        index + 1 < tokens.Count ? tokens[index + 1].Text : "";

    private static string Previous(List<CodeToken> tokens, int index) => index > 0 ? tokens[index - 1].Text : "";

    /// <summary>Whether the ) at close ends a tuple return type: (string Name, int Line) Find(...).</summary>
    private static bool TupleType(List<CodeToken> tokens, int close)
    {
        if (tokens[close].Text is not ")")
        {
            return false;
        }

        var depth = 0;
        var comma = false;
        for (var index = close; index >= 0; index--)
        {
            switch (tokens[index].Text)
            {
                case ")":
                    depth++;
                    break;
                case "," when depth is 1:
                    comma = true;
                    break;
                case "(" when --depth is 0:
                    // Not if (...) Call(...) or a cast: a modifier, attribute or statement boundary precedes it.
                    var before = index > 0 ? tokens[index - 1] : null;
                    return comma
                        && (
                            before is null
                            || before.Kind is 'i' && !CloneLexer.Control.Contains(before.Text)
                            || before.Text is "{" or ";" or "}" or "]"
                        );
            }
        }
        return false;
    }

    /// <summary>Branches inside the holes of a C# interpolated string or a JavaScript template, counted like code.</summary>
    private static int Interpolated(string text, string language)
    {
        string open;
        int index;
        if (language is "typescript")
        {
            if (!text.StartsWith('`'))
            {
                return 0;
            }

            (open, index) = ("${", 1);
        }
        else
        {
            var quote = text.IndexOf('"', StringComparison.Ordinal);
            var dollars = quote < 0 ? 0 : text.AsSpan(0, quote).Count('$');
            if (dollars is 0)
            {
                return 0;
            }

            (open, index) = (new string('{', dollars), quote);
        }

        var added = 0;
        while ((index = text.IndexOf(open, index, StringComparison.Ordinal)) >= 0)
        {
            var escaped = language is "typescript"
                ? index > 0 && text[index - 1] is '\\'
                : open.Length is 1 && index + 1 < text.Length && text[index + 1] is '{';
            if (escaped)
            {
                index += 2;
                continue;
            }

            var start = index + open.Length;
            var end = start;
            for (var depth = 0; end < text.Length; end++)
            {
                if (text[end] is '{')
                {
                    depth++;
                }
                else if (text[end] is '}' && depth-- is 0)
                {
                    break;
                }
            }
            if (end >= text.Length)
            {
                break;
            }

            var hole = CloneLexer.Tokenize(text[start..end], language is "typescript" ? "ts" : "cs");
            added += Count(hole, 0, hole.Count - 1, language).Complexity - 1;
            index = end + 1;
        }
        return added;
    }

    private static int GenericEnd(List<CodeToken> tokens, int open)
    {
        var depth = 0;
        for (var index = open; index < tokens.Count && index < open + 64; index++)
        {
            switch (tokens[index].Text)
            {
                case "<":
                    depth++;
                    break;
                case ">" when --depth is 0:
                    return index;
                case ">":
                    break;
                case "," or "." or "?" or "[" or "]" or "(" or ")":
                    break;
                default:
                    if (tokens[index].Kind is not 'i')
                    {
                        return -1;
                    }

                    break;
            }
        }
        return -1;
    }

    /// <summary>The semicolon closing an expression body that starts at index.</summary>
    private static int ExpressionEnd(List<CodeToken> tokens, int index)
    {
        var depth = 0;
        for (; index < tokens.Count; index++)
        {
            switch (tokens[index].Text)
            {
                case "(" or "[" or "{":
                    depth++;
                    break;
                case ")" or "]" or "}" when --depth < 0:
                    return -1;
                case ";" when depth is 0:
                    return index;
            }
        }
        return -1;
    }

    private static int Parameters(List<CodeToken> tokens, int open, int close)
    {
        if (close == open + 1)
        {
            return 0;
        }

        var count = 1;
        var depth = 0;
        for (var index = open + 1; index < close; index++)
        {
            switch (tokens[index].Text)
            {
                case "(" or "[" or "{" or "<":
                    depth++;
                    break;
                case ")" or "]" or "}" or ">":
                    depth--;
                    break;
                case "," when depth is 0:
                    count++;
                    break;
            }
        }
        return count;
    }

    /// <summary>Class, struct, record and interface bodies (C# and TypeScript), to qualify member names.</summary>
    private static List<Span> Types(List<CodeToken> tokens)
    {
        List<Span> output = [];
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (
                !CloneLexer.Declares.Contains(tokens[index].Text)
                || tokens[index + 1].Kind is not 'i'
                || CloneLexer.Declares.Contains(tokens[index + 1].Text)
            )
            {
                continue;
            }

            var depth = 0;
            for (var cursor = index + 2; cursor < tokens.Count; cursor++)
            {
                var text = tokens[cursor].Text;
                depth += text switch
                {
                    "(" => 1,
                    ")" => -1,
                    _ => 0,
                };
                if (depth is 0 && text is ";")
                {
                    break;
                }

                if (depth is 0 && text is "{")
                {
                    var end = CloneLexer.Match(tokens, cursor);
                    if (end > 0)
                    {
                        output.Add(new(tokens[index + 1].Text, cursor, end));
                    }

                    break;
                }
            }
        }
        return output;
    }
}
