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

        var cursor = index + 1;
        var owner = "";
        if (tokens[cursor].Text is "(")
        {
            var receiver = CloneLexer.Match(tokens, cursor);
            if (receiver < 0)
            {
                return null;
            }

            // (s *Server[T]) is a method of Server.
            for (var at = cursor + 1; at < receiver && tokens[at].Text is not "["; at++)
            {
                if (tokens[at].Kind is 'i')
                {
                    owner = tokens[at].Text;
                }
            }
            cursor = receiver + 1;
        }

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

        var parameters = cursor;
        var close = CloneLexer.Match(tokens, parameters);
        if (close < 0)
        {
            return null;
        }

        // The body opens on the signature's last line; results may hold struct{}, interface{} or func types.
        var depth = 0;
        cursor = close + 1;
        while (cursor < tokens.Count && cursor <= close + SignatureReach)
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
                case "{" when Previous(tokens, cursor) is "struct" or "interface":
                    cursor = CloneLexer.Match(tokens, cursor);
                    if (cursor < 0)
                    {
                        return null;
                    }

                    break;
                case "{" when depth is 0:
                    var body = CloneLexer.Match(tokens, cursor);
                    return body < 0 ? null : new(name, parameters, close, body, owner);
            }
            if (depth < 0 || depth is 0 && tokens[cursor].Line > tokens[close].Line + 3)
            {
                return null;
            }

            cursor++;
        }
        return null;
    }
}
