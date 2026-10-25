using Harness.Core.Architecture;
using Harness.Infrastructure.Architecture.Clones;

namespace Harness.Infrastructure.Architecture.Complexity;

/// <summary>
/// Lexical cyclomatic complexity of C# members, counted as Microsoft.CodeAnalysis.Metrics counts it:
/// one plus each if, case label, loop, conditional (?:), conditional access (?. ?[]), ?? and short-circuit
/// operator. Switch-expression arms, catch, exception filters, patterns and ??= add nothing.
/// </summary>
internal static class ComplexityCounter
{
    private const int SignatureReach = 400;

    private sealed record Span(string Name, int Start, int End);

    public static List<ComplexityMember> Members(string path, List<CodeToken> tokens)
    {
        var types = Types(tokens);
        List<(int Name, int Parameters, int Close, int End)> declared = [];
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (Declaration(tokens, index) is { } member)
            {
                declared.Add(member);
            }
        }
        List<ComplexityMember> output = [];
        foreach (var member in declared)
        {
            // A local function belongs to the member around it, as lambdas do.
            if (declared.Any(outer => outer.Close < member.Name && member.End <= outer.End))
            {
                continue;
            }

            var type = types
                .Where(span => span.Start < member.Name && member.Name < span.End)
                .MaxBy(span => span.Start);
            var (complexity, cases) = Count(tokens, member.Close + 1, member.End);
            output.Add(
                new(
                    path,
                    (type is null ? "" : type.Name + ".") + tokens[member.Name].Text,
                    tokens[member.Name].Line,
                    Parameters(tokens, member.Parameters, member.Close),
                    complexity,
                    cases
                )
            );
        }
        return output;
    }

    /// <summary>A method, constructor or local function declared at index: its name, parameter list and body end.</summary>
    private static (int Name, int Parameters, int Close, int End)? Declaration(List<CodeToken> tokens, int index)
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
                    return block < 0 ? null : (index, parameters, end, block);
                case "=>" when depth is 0:
                    var expression = ExpressionEnd(tokens, cursor + 1);
                    return expression < 0 ? null : (index, parameters, end, expression);
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

    private static (int Complexity, int Cases) Count(List<CodeToken> tokens, int start, int end)
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
                    complexity += Interpolated(token.Text);
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

    /// <summary>Branches inside an interpolated string's holes, counted like code.</summary>
    private static int Interpolated(string text)
    {
        var quote = text.IndexOf('"', StringComparison.Ordinal);
        var dollars = quote < 0 ? 0 : text.AsSpan(0, quote).Count('$');
        if (dollars is 0)
        {
            return 0;
        }

        var open = new string('{', dollars);
        var added = 0;
        var index = quote;
        while ((index = text.IndexOf(open, index, StringComparison.Ordinal)) >= 0)
        {
            if (dollars is 1 && index + 1 < text.Length && text[index + 1] is '{')
            {
                index += 2;
                continue;
            }

            var start = index + dollars;
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

            var hole = CloneLexer.Tokenize(text[start..end], "cs");
            added += Count(hole, 0, hole.Count - 1).Complexity - 1;
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

    /// <summary>Class, struct, record and interface bodies, to qualify member names with their type.</summary>
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
