using System.Globalization;
using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Clones;

public sealed partial class CloneExtractor
{
    internal sealed record BodyForms(
        List<string> Canonical,
        List<string> Shape,
        HashSet<string> Locals,
        List<CloneStatement> Statements
    );

    private static bool MemberAt(List<CodeToken> tokens, int index) =>
        index > 0
        && (
            tokens[index - 1].Text is "." or "?."
            || index + 1 < tokens.Count && tokens[index + 1].Text is ":" && tokens[index - 1].Text is "{" or ","
        );

    private static BodyForms Forms(
        List<CodeToken> tokens,
        int parameters,
        int body,
        int end,
        Dictionary<string, string>? captured,
        bool lines
    )
    {
        HashSet<string> locals = new(StringComparer.Ordinal);
        if (tokens[parameters].Text is "(")
        {
            var close = CloneLexer.Match(tokens, parameters);
            for (var index = parameters + 1; index < close; index++)
            {
                if (
                    tokens[index].Kind is 'i'
                    && !CloneLexer.Keywords.Contains(tokens[index].Text)
                    && tokens[index + 1].Text is "," or ")" or "=" or ":" or "?" or "}"
                )
                {
                    locals.Add(tokens[index].Text);
                }
            }
        }
        else if (tokens[parameters].Kind is 'i')
        {
            locals.Add(tokens[parameters].Text);
        }

        for (var index = body; index <= end; index++)
        {
            var current = tokens[index];
            var previous = index > 0 ? tokens[index - 1] : new('p', "", 0);
            var next = index + 1 < tokens.Count ? tokens[index + 1] : new('p', "", 0);
            if (current.Kind is not 'i' || CloneLexer.Keywords.Contains(current.Text) || previous.Text is "." or "?.")
            {
                continue;
            }

            if (
                previous.Kind is 'i' && previous.Text is "var" or "let" or "const"
                || CloneLexer.TypeLike(previous)
                    && !CloneLexer.Keywords.Contains(previous.Text)
                    && next.Text is "=" or ";" or "in" or "of" or ")" or ","
                || next.Text is "=>"
            )
            {
                locals.Add(current.Text);
            }
        }
        for (var index = body; index < end; index++)
        {
            if (
                tokens[index].Kind is 'i'
                && tokens[index].Text is "const" or "let" or "var"
                && tokens[index + 1].Text is "{" or "["
            )
            {
                var close = CloneLexer.Match(tokens, index + 1);
                if (close > 0)
                {
                    locals.UnionWith(
                        tokens
                            .Skip(index + 2)
                            .Take(close - index - 2)
                            .Where(token => token.Kind is 'i')
                            .Select(token => token.Text)
                    );
                }
            }
            if (tokens[index].Text is not "=>" || index is 0 || tokens[index - 1].Text is not ")")
            {
                continue;
            }

            var depth = 0;
            for (var cursor = index - 1; cursor > body; cursor--)
            {
                if (tokens[cursor].Text is ")")
                {
                    depth++;
                }
                else if (tokens[cursor].Text is "(")
                {
                    if (--depth is 0)
                    {
                        break;
                    }
                }
                else if (tokens[cursor].Kind is 'i' && depth is 1)
                {
                    locals.Add(tokens[cursor].Text);
                }
            }
        }
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        List<string> canonical = [],
            shape = [];
        var splitter = new StatementSplitter();
        var open = 0;
        Stack<int> outer = [];
        for (var index = body; index <= end; index++)
        {
            var token = tokens[index];
            if (
                lines
                && index > body
                && token.Line > tokens[index - 1].Line
                && open is 0
                && !",(=+-*/&|?:<>!".Contains(tokens[index - 1].Text[^1], StringComparison.Ordinal)
                && !token.Text.StartsWith('.')
                && !token.Text.StartsWith('?')
                && token.Text is not ("=>" or "&&" or "||")
            )
            {
                splitter.End();
            }

            switch (token.Text)
            {
                case "(" or "[":
                    open++;
                    break;
                case ")" or "]":
                    open = Math.Max(0, open - 1);
                    break;
                case "{":
                    outer.Push(open);
                    open = 0;
                    break;
                case "}":
                    if (outer.TryPop(out var previous))
                    {
                        open = previous;
                    }
                    break;
            }
            switch (token.Kind)
            {
                case 'i':
                    if (CloneLexer.Keywords.Contains(token.Text))
                    {
                        canonical.Add(token.Text);
                        shape.Add(token.Text);
                        splitter.Token(token.Text, token.Text, false, token.Line);
                        break;
                    }
                    if (
                        captured is not null
                        && captured.TryGetValue(token.Text, out var qualifier)
                        && !locals.Contains(token.Text)
                        && !MemberAt(tokens, index)
                    )
                    {
                        canonical.Add(qualifier + token.Text);
                        shape.Add("$");
                        splitter.Token(qualifier + token.Text, token.Text, false, token.Line);
                        break;
                    }
                    var name = LocalName(names, locals, token.Text);
                    canonical.Add(name);
                    shape.Add("$");
                    splitter.Token(name, token.Text, name != token.Text, token.Line);
                    break;
                case 's':
                    canonical.Add(token.Text);
                    shape.Add("\"");
                    splitter.Token(token.Text, token.Text, false, token.Line);
                    break;
                case 'n':
                    canonical.Add("0");
                    shape.Add("0");
                    splitter.Token("0", token.Text, false, token.Line);
                    break;
                default:
                    if (token.Text is ";")
                    {
                        if (open is 0)
                        {
                            splitter.End();
                        }
                        break;
                    }
                    canonical.Add(token.Text);
                    shape.Add(token.Text);
                    splitter.Token(token.Text, token.Text, false, token.Line);
                    break;
            }
        }
        splitter.End();
        return new(canonical, shape, locals, splitter.Statements);
    }

    private static string LocalName(Dictionary<string, string> names, HashSet<string> locals, string name)
    {
        if (!locals.Contains(name) || name is "_")
        {
            return name;
        }

        if (!names.TryGetValue(name, out var local))
        {
            local = "L" + names.Count.ToString(CultureInfo.InvariantCulture);
            names.Add(name, local);
        }
        return local;
    }
}
