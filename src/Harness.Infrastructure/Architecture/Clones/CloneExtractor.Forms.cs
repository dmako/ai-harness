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
        var locals = ParameterNames(tokens, parameters);
        AddDeclaredLocals(tokens, body, end, locals);
        AddPatternLocals(tokens, body, end, locals);
        var writer = new FormWriter(tokens, locals, captured);
        for (var index = body; index <= end; index++)
        {
            writer.Write(index, lines && index > body);
        }
        return writer.Finish();
    }

    /// <summary>The names in the parameter list at <paramref name="parameters"/>, or the single bare parameter.</summary>
    private static HashSet<string> ParameterNames(List<CodeToken> tokens, int parameters)
    {
        HashSet<string> locals = new(StringComparer.Ordinal);
        if (tokens[parameters].Kind is 'i')
        {
            locals.Add(tokens[parameters].Text);
        }

        if (tokens[parameters].Text is not "(")
        {
            return locals;
        }

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
        return locals;
    }

    /// <summary>Names declared by var/let/const, a type before them, or an arrow after them.</summary>
    private static void AddDeclaredLocals(List<CodeToken> tokens, int body, int end, HashSet<string> locals)
    {
        CodeToken none = new('p', "", 0);
        for (var index = body; index <= end; index++)
        {
            var current = tokens[index];
            var previous = index > 0 ? tokens[index - 1] : none;
            var next = index + 1 < tokens.Count ? tokens[index + 1] : none;
            if (
                current.Kind is 'i'
                && !CloneLexer.Keywords.Contains(current.Text)
                && previous.Text is not ("." or "?.")
                && DeclaresLocal(previous, next)
            )
            {
                locals.Add(current.Text);
            }
        }
    }

    private static bool DeclaresLocal(CodeToken previous, CodeToken next) =>
        previous.Kind is 'i' && previous.Text is "var" or "let" or "const"
        || CloneLexer.TypeLike(previous)
            && !CloneLexer.Keywords.Contains(previous.Text)
            && next.Text is "=" or ";" or "in" or "of" or ")" or ","
        || next.Text is "=>";

    /// <summary>Names bound by destructuring declarations and by parenthesized arrow parameters.</summary>
    private static void AddPatternLocals(List<CodeToken> tokens, int body, int end, HashSet<string> locals)
    {
        for (var index = body; index < end; index++)
        {
            if (
                tokens[index].Kind is 'i'
                && tokens[index].Text is "const" or "let" or "var"
                && tokens[index + 1].Text is "{" or "["
            )
            {
                AddDestructured(tokens, index + 1, locals);
            }

            if (tokens[index].Text is "=>" && index > 0 && tokens[index - 1].Text is ")")
            {
                AddArrowParameters(tokens, body, index - 1, locals);
            }
        }
    }

    private static void AddDestructured(List<CodeToken> tokens, int open, HashSet<string> locals)
    {
        var close = CloneLexer.Match(tokens, open);
        if (close > 0)
        {
            locals.UnionWith(
                tokens.Skip(open + 1).Take(close - open - 1).Where(token => token.Kind is 'i').Select(token => token.Text)
            );
        }
    }

    /// <summary>The identifiers directly inside the parentheses closed at <paramref name="close"/>.</summary>
    private static void AddArrowParameters(List<CodeToken> tokens, int body, int close, HashSet<string> locals)
    {
        var depth = 0;
        for (var cursor = close; cursor > body; cursor--)
        {
            if (tokens[cursor].Text is ")")
            {
                depth++;
            }
            else if (tokens[cursor].Text is "(")
            {
                if (--depth is 0)
                {
                    return;
                }
            }
            else if (tokens[cursor].Kind is 'i' && depth is 1)
            {
                locals.Add(tokens[cursor].Text);
            }
        }
    }

    /// <summary>
    /// Builds a body's canonical and shape tokens and splits it into statements, tracking bracket
    /// nesting so that only top-level ';' and line breaks end a statement.
    /// </summary>
    private sealed class FormWriter(
        List<CodeToken> tokens,
        HashSet<string> locals,
        Dictionary<string, string>? captured
    )
    {
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly List<string> _canonical = [];
        private readonly List<string> _shape = [];
        private readonly StatementSplitter _splitter = new();
        private readonly Stack<int> _outer = [];
        private int _open;

        /// <param name="lines">Whether a line break may end the statement before this token.</param>
        public void Write(int index, bool lines)
        {
            var token = tokens[index];
            if (lines && LineEndsStatement(index))
            {
                _splitter.End();
            }

            Nest(token.Text);
            switch (token.Kind)
            {
                case 'i':
                    Identifier(index, token);
                    break;
                case 's':
                    Add(token.Text, "\"", token, false);
                    break;
                case 'n':
                    Add("0", "0", token, false);
                    break;
                default:
                    Punctuation(token);
                    break;
            }
        }

        public BodyForms Finish()
        {
            _splitter.End();
            return new(_canonical, _shape, locals, _splitter.Statements);
        }

        /// <summary>
        /// A new line at the top level ends the statement unless the previous line ends with an operator
        /// or this one continues with a member access or operator.
        /// </summary>
        private bool LineEndsStatement(int index)
        {
            var token = tokens[index];
            return token.Line > tokens[index - 1].Line
                && _open is 0
                && !",(=+-*/&|?:<>!".Contains(tokens[index - 1].Text[^1], StringComparison.Ordinal)
                && !token.Text.StartsWith('.')
                && !token.Text.StartsWith('?')
                && token.Text is not ("=>" or "&&" or "||");
        }

        /// <summary>Counts open brackets; a brace starts a fresh count and restores the outer one when closed.</summary>
        private void Nest(string text)
        {
            switch (text)
            {
                case "(" or "[":
                    _open++;
                    break;
                case ")" or "]":
                    _open = Math.Max(0, _open - 1);
                    break;
                case "{":
                    _outer.Push(_open);
                    _open = 0;
                    break;
                case "}" when _outer.TryPop(out var previous):
                    _open = previous;
                    break;
            }
        }

        private void Identifier(int index, CodeToken token)
        {
            if (CloneLexer.Keywords.Contains(token.Text))
            {
                Add(token.Text, token.Text, token, false);
                return;
            }

            if (
                captured is not null
                && captured.TryGetValue(token.Text, out var qualifier)
                && !locals.Contains(token.Text)
                && !MemberAt(tokens, index)
            )
            {
                Add(qualifier + token.Text, "$", token, false);
                return;
            }

            var name = LocalName(_names, locals, token.Text);
            Add(name, "$", token, name != token.Text);
        }

        private void Punctuation(CodeToken token)
        {
            if (token.Text is not ";")
            {
                Add(token.Text, token.Text, token, false);
            }
            else if (_open is 0)
            {
                _splitter.End();
            }
        }

        private void Add(string form, string mark, CodeToken token, bool own)
        {
            _canonical.Add(form);
            _shape.Add(mark);
            _splitter.Token(form, token.Text, own, token.Line);
        }
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
