using Harness.Core.Architecture;
using Harness.Infrastructure.Architecture.Clones;

namespace Harness.Infrastructure.Architecture.Complexity;

/// <summary>
/// TypeScript and JavaScript, counted as ESLint's complexity rule counts it: one plus each if, for/for-in/for-of,
/// while and do-while, case, catch, ?:, &amp;&amp;, ||, ??, ?. and logical assignment (&amp;&amp;=, ||=, ??=). Named functions,
/// methods and functions assigned to a name are members; arrows and function expressions inside them count
/// toward them.
/// </summary>
internal static partial class ComplexityCounter
{
    private static readonly HashSet<string> ScriptModifiers = CloneLexer.Words(
        "async static public private protected get set override readonly abstract export default declare"
    );
    private static readonly HashSet<string> ScriptStatements = CloneLexer.Words(
        "const let var function export import class return if for while switch type interface"
    );

    public static List<ComplexityMember> Script(string path, List<CodeToken> tokens) =>
        Members(path, tokens, "typescript", ScriptDeclaration);

    private static (int Complexity, int Cases) CountScript(List<CodeToken> tokens, int start, int end)
    {
        var complexity = 1;
        var cases = 0;
        for (var index = start; index <= end && index < tokens.Count; index++)
        {
            var token = tokens[index];
            switch (token.Kind, token.Text)
            {
                // After . a keyword names a property: promise.catch(...) is a call, not a catch clause.
                case ('i', _) when Previous(tokens, index) is "." or "?.":
                    break;
                // A do-while counts once, by its while.
                case ('i', "if" or "for" or "while" or "catch"):
                case ('p', "&&" or "||" or "?."):
                    complexity++;
                    break;
                case ('i', "case"):
                    complexity++;
                    cases++;
                    break;
                case ('s', _):
                    complexity += Interpolated(token.Text, "typescript");
                    break;
                case ('p', "?") when Previous(tokens, index) is "?":
                    break;
                case ('p', "?") when Next(tokens, index) is "?":
                    // ?? and ??= both count.
                    complexity++;
                    break;
                case ('p', "?") when Next(tokens, index) is not (":" or ")" or "," or "=" or ";"):
                    // x?: T and (x?) mark optional members and parameters.
                    complexity++;
                    break;
            }
        }
        return (complexity, cases);
    }

    private static Declared? ScriptDeclaration(List<CodeToken> tokens, int index)
    {
        var token = tokens[index];
        if (token.Kind is not 'i')
        {
            return null;
        }

        if (token.Text is "function")
        {
            // function name(...) and function* name(...); an anonymous function belongs to its function.
            var name = Next(tokens, index) is "*" ? index + 2 : index + 1;
            return name < tokens.Count && tokens[name].Kind is 'i'
                ? ScriptFunction(tokens, name, name + 1, arrow: false, expression: false)
                : null;
        }

        if (CloneLexer.Control.Contains(token.Text) || CloneLexer.Keywords.Contains(token.Text))
        {
            return null;
        }

        var before = Previous(tokens, index);
        switch (Next(tokens, index))
        {
            case "("
            or "<" when index is 0 || before is "{" or "}" or ";" or "," or "*" || ScriptModifiers.Contains(before):
                // A class or object method: name(...) { ... }
                return ScriptFunction(tokens, index, index + 1, arrow: false, expression: false);
            case "=" or ":" when before is not "type":
                // name = (...) => ..., name = function (...) { ... }, and name: (...) => { ... } in an object.
                var expression = Next(tokens, index) is "=";
                var at = index + 2;
                if (at < tokens.Count && tokens[at].Text is "async")
                {
                    at++;
                }

                if (at >= tokens.Count || tokens[at].Text is not "function")
                {
                    return ScriptFunction(tokens, index, at, arrow: true, expression);
                }

                at += Next(tokens, at) is "*" ? 2 : 1;
                if (at < tokens.Count && tokens[at].Kind is 'i')
                {
                    at++;
                }

                return ScriptFunction(tokens, index, at, arrow: false, expression: false);
            default:
                return null;
        }
    }

    /// <summary>The parameters at cursor (after any type parameters), a return type, and a block or arrow body.</summary>
    private static Declared? ScriptFunction(List<CodeToken> tokens, int name, int cursor, bool arrow, bool expression)
    {
        if (ScriptParameters(tokens, cursor, arrow) is not var (parameters, close))
        {
            return null;
        }

        var at = ScriptBodyStart(tokens, close, arrow);
        if (at < 0)
        {
            return null;
        }

        if (tokens[at].Text is "{")
        {
            var block = CloneLexer.Match(tokens, at);
            return block < 0 ? null : new(name, parameters, close, block);
        }

        return arrow && expression ? new(name, parameters, close, ScriptExpressionEnd(tokens, at)) : null;
    }

    /// <summary>
    /// The parameter list at cursor, after any type parameters: its ( and matching ), or an arrow's single
    /// parameter as both; null when there is none.
    /// </summary>
    private static (int Open, int Close)? ScriptParameters(List<CodeToken> tokens, int cursor, bool arrow)
    {
        if (cursor < tokens.Count && tokens[cursor].Text is "<")
        {
            cursor = GenericEnd(tokens, cursor) + 1;
        }

        if (cursor <= 0 || cursor >= tokens.Count)
        {
            return null;
        }

        if (tokens[cursor].Text is "(")
        {
            return (cursor, CloneLexer.Match(tokens, cursor));
        }

        return arrow && tokens[cursor].Kind is 'i' && Next(tokens, cursor) is "=>" ? (cursor, cursor) : null;
    }

    /// <summary>The body's first token after the parameters' close, a return type and an arrow's =>; or -1.</summary>
    private static int ScriptBodyStart(List<CodeToken> tokens, int close, bool arrow)
    {
        var at = close + 1;
        if (close > 0 && at < tokens.Count && tokens[at].Text is ":")
        {
            at = ScriptTypeEnd(tokens, at + 1);
        }

        if (close < 0 || at <= 0 || at >= tokens.Count)
        {
            return -1;
        }

        return arrow && (tokens[at].Text is not "=>" || ++at >= tokens.Count) ? -1 : at;
    }

    /// <summary>The body ({ or =>) after a return type annotation that starts at index, or -1.</summary>
    private static int ScriptTypeEnd(List<CodeToken> tokens, int index)
    {
        var depth = 0;
        for (var cursor = index; cursor < tokens.Count && cursor < index + 60; cursor++)
        {
            var token = tokens[cursor];
            switch (token.Text)
            {
                case "{" or "=>" when depth is 0:
                    return cursor;
                case "(" or "[" or "<" or "{":
                    depth++;
                    break;
                case ")" or "]" or ">" or "}":
                    depth--;
                    break;
                case ";" or "=" when depth is 0:
                    return -1;
            }
            if (depth < 0 || token.Kind is 'i' && depth is 0 && CloneLexer.Control.Contains(token.Text))
            {
                return -1;
            }
        }
        return -1;
    }

    /// <summary>The last token of an arrow's expression body: before ; , or a closer, or the next statement.</summary>
    private static int ScriptExpressionEnd(List<CodeToken> tokens, int start)
    {
        var depth = 0;
        for (var index = start; index < tokens.Count; index++)
        {
            var token = tokens[index];
            switch (token.Kind, token.Text)
            {
                case ('p', "(" or "[" or "{"):
                    depth++;
                    break;
                case ('p', ")" or "]" or "}") when --depth < 0:
                    return index - 1;
                case ('p', ";" or ",") when depth is 0:
                    return index - 1;
                case ('i', _)
                    when depth is 0
                        && index > start
                        && token.Line > tokens[index - 1].Line
                        && ScriptStatements.Contains(token.Text):
                    // A line without its semicolon.
                    return index - 1;
            }
        }
        return tokens.Count - 1;
    }
}
