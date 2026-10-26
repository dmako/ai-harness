using Harness.Core.Architecture;
using Harness.Infrastructure.Architecture.Clones;

namespace Harness.Infrastructure.Architecture.Complexity;

/// <summary>Go, counted as gocyclo counts it: one plus each if, for, case clause (switch and select), &amp;&amp; and ||.</summary>
internal static partial class ComplexityCounter
{
    public static List<ComplexityMember> Go(string path, List<CodeToken> tokens) =>
        Members(path, tokens, "go", GoDeclaration);

    private static (int Complexity, int Cases) CountGo(List<CodeToken> tokens, int start, int end)
    {
        var complexity = 1;
        var cases = 0;
        for (var index = start; index <= end && index < tokens.Count; index++)
        {
            switch (tokens[index].Kind, tokens[index].Text)
            {
                case ('i', "if" or "for"):
                case ('p', "&&" or "||"):
                    complexity++;
                    break;
                case ('i', "case"):
                    complexity++;
                    cases++;
                    break;
            }
        }
        return (complexity, cases);
    }

    /// <summary>func Name(...) or func (receiver) Name(...) with a body; a func literal is part of its function.</summary>
    private static Declared? GoDeclaration(List<CodeToken> tokens, int index)
    {
        if (tokens[index].Kind is not 'i' || tokens[index].Text is not "func")
        {
            return null;
        }

        if (
            GoReceiver(tokens, index + 1) is not { } receiver
            || GoSignature(tokens, receiver.Cursor) is not { } signature
        )
        {
            return null;
        }

        return GoBody(tokens, signature.Close) is { } body
            ? new(signature.Name, signature.Parameters, signature.Close, body, receiver.Owner)
            : null;
    }

    /// <summary>
    /// The receiver's type name ("" for a plain func) and the token after the receiver; null when the
    /// receiver is not closed.
    /// </summary>
    private static (string Owner, int Cursor)? GoReceiver(List<CodeToken> tokens, int cursor)
    {
        if (tokens[cursor].Text is not "(")
        {
            return ("", cursor);
        }

        var receiver = CloneLexer.Match(tokens, cursor);
        if (receiver < 0)
        {
            return null;
        }

        // (s *Server[T]) is a method of Server.
        var owner = "";
        for (var at = cursor + 1; at < receiver && tokens[at].Text is not "["; at++)
        {
            if (tokens[at].Kind is 'i')
            {
                owner = tokens[at].Text;
            }
        }
        return (owner, receiver + 1);
    }

    /// <summary>The func's name and its parameter list (after any type parameters).</summary>
    private static (int Name, int Parameters, int Close)? GoSignature(List<CodeToken> tokens, int cursor)
    {
        if (cursor >= tokens.Count || tokens[cursor].Kind is not 'i')
        {
            return null;
        }

        var name = cursor++;
        if (cursor < tokens.Count && tokens[cursor].Text is "[")
        {
            cursor = CloneLexer.Match(tokens, cursor) + 1;
        }

        if (cursor <= 0 || cursor >= tokens.Count || tokens[cursor].Text is not "(")
        {
            return null;
        }

        var close = CloneLexer.Match(tokens, cursor);
        return close < 0 ? null : (name, cursor, close);
    }

    /// <summary>
    /// The end of the body opening on the signature's last line; results may hold struct{}, interface{}
    /// or func types.
    /// </summary>
    private static int? GoBody(List<CodeToken> tokens, int close)
    {
        var depth = 0;
        var cursor = close + 1;
        while (cursor < tokens.Count && cursor <= close + SignatureReach)
        {
            var text = Punctuation(tokens[cursor]);
            if (text is "{" && Previous(tokens, cursor) is "struct" or "interface")
            {
                cursor = CloneLexer.Match(tokens, cursor);
                if (cursor < 0)
                {
                    return null;
                }
            }
            else if (text is "{" && depth is 0)
            {
                var body = CloneLexer.Match(tokens, cursor);
                return body < 0 ? null : body;
            }
            else
            {
                depth += Nesting(text);
            }

            if (SignatureEnded(tokens, close, cursor, depth))
            {
                return null;
            }

            cursor++;
        }
        return null;
    }

    private static string Punctuation(CodeToken token) => token.Kind is 'p' ? token.Text : "";

    private static int Nesting(string text) =>
        text switch
        {
            "(" or "[" => 1,
            ")" or "]" => -1,
            _ => 0,
        };

    /// <summary>A bracket closed too often, or a line more than three below the parameters outside brackets.</summary>
    private static bool SignatureEnded(List<CodeToken> tokens, int close, int cursor, int depth) =>
        depth < 0 || depth is 0 && tokens[cursor].Line > tokens[close].Line + 3;
}
