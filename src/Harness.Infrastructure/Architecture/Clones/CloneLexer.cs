namespace Harness.Infrastructure.Architecture.Clones;

internal sealed record CodeToken(char Kind, string Text, int Line);

internal static class CloneLexer
{
    internal static readonly HashSet<string> Keywords = Words(
        "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach function get goto if implicit in int interface internal is let lock long namespace new null object of operator out override params private protected public readonly ref return sbyte sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while yield record init required when where with nameof undefined export import from extends implements instanceof delete type declare keyof readonly"
    );
    internal static readonly HashSet<string> GoKeywords = Words(
        "break default func interface select case defer go map struct chan else goto package switch const fallthrough if range type continue for import return var"
    );
    internal static readonly HashSet<string> Control = Words(
        "if for foreach while switch catch using lock fixed return new nameof typeof sizeof when await throw else do function checked unchecked default is as not and or"
    );
    internal static readonly HashSet<string> Modifiers = Words(
        "public private protected internal static async virtual override abstract sealed extern unsafe new partial readonly export default function const let var declare get set"
    );
    internal static readonly HashSet<string> Declares = Words("class record struct interface");

    internal static HashSet<string> Words(string text) =>
        new(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    internal static bool IdentifierStart(char character) =>
        character is '_' or '$' or '@' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '\u0080';

    internal static bool Identifier(char character) => IdentifierStart(character) || char.IsAsciiDigit(character);

    internal static bool TypeLike(CodeToken token) =>
        token.Kind is 'i' && !Control.Contains(token.Text) || token.Kind is 'p' && token.Text is ">" or "]" or "?";

    internal static int Match(IReadOnlyList<CodeToken> tokens, int at)
    {
        var open = tokens[at].Text;
        var close = open switch
        {
            "(" => ")",
            "{" => "}",
            "[" => "]",
            _ => "",
        };
        var depth = 0;
        for (var index = at; index < tokens.Count; index++)
        {
            if (tokens[index].Kind is not 'p')
            {
                continue;
            }

            if (tokens[index].Text == open)
            {
                depth++;
            }
            else if (tokens[index].Text == close && --depth is 0)
            {
                return index;
            }
        }
        return -1;
    }

    private static readonly string[] GoOperators =
    [
        "...",
        "<<=",
        ">>=",
        "&^=",
        ":=",
        "++",
        "--",
        "<<",
        ">>",
        "&^",
        "<-",
        "+=",
        "-=",
        "*=",
        "/=",
        "%=",
        "&=",
        "|=",
        "^=",
        "==",
        "!=",
        "<=",
        ">=",
        "&&",
        "||",
    ];

    private static readonly string[] CLikeOperators = ["=>", "?.", "===", "!==", "==", "!=", "<=", ">=", "&&", "||"];

    internal static List<CodeToken> Tokenize(string source, string language)
    {
        var scanner = new Scanner(source, language);
        while (!scanner.Done)
        {
            scanner.Next();
        }
        return scanner.Tokens;
    }

    /// <summary>A cursor over the source that reads one token, comment or whitespace character per step.</summary>
    private sealed class Scanner(string source, string language)
    {
        private readonly bool _js = language is "ts";
        private readonly bool _go = language is "go";
        private int _index;
        private int _line = 1;

        public List<CodeToken> Tokens { get; } = [];

        public bool Done => _index >= source.Length;

        /// <summary>The character <paramref name="offset"/> after the cursor, or '\0' past the end.</summary>
        private char At(int offset) => _index + offset < source.Length ? source[_index + offset] : '\0';

        public void Next()
        {
            var character = source[_index];
            if (char.IsWhiteSpace(character))
            {
                _line += character is '\n' ? 1 : 0;
                _index++;
            }
            else if (character is '/' && At(1) is '/' || character is '#' && Directive())
            {
                SkipLine();
            }
            else if (character is '/' && At(1) is '*')
            {
                SkipBlockComment();
            }
            else if (StringStart(character))
            {
                ReadString();
            }
            else if (character is '/' && _js && RegexAfter(Tokens.LastOrDefault()))
            {
                ReadRegex();
            }
            else
            {
                ReadWord(character);
            }
        }

        /// <summary>A C# preprocessor directive: '#' first on its line.</summary>
        private bool Directive() =>
            !_js
            && !_go
            && (
                Tokens.Count is 0
                || source[(source.LastIndexOf('\n', Math.Max(0, _index - 1)) + 1).._index]
                    .All(item => item is ' ' or '\t')
            );

        private static bool RegexAfter(CodeToken? previous) =>
            previous is null
            || previous.Kind is 'p'
                && ("(,=:[!&|?{};+-*%<>~^".Contains(previous.Text, StringComparison.Ordinal) || previous.Text is "=>")
            || previous.Kind is 'i' && previous.Text is "return" or "typeof" or "case" or "in" or "of";

        private bool StringStart(char character) =>
            character is '"' or '\'' or '`'
            || character is '@' or '$' && (At(1) is '"' || At(1) is '@' or '$' && At(2) is '"');

        private void SkipLine()
        {
            while (_index < source.Length && source[_index] is not '\n')
            {
                _index++;
            }
        }

        private void SkipBlockComment()
        {
            _index += 2;
            while (_index + 1 < source.Length && !(source[_index] is '*' && source[_index + 1] is '/'))
            {
                CountLine(source[_index++]);
            }
            _index = Math.Min(source.Length, _index + 2);
        }

        private void CountLine(char character)
        {
            if (character is '\n')
            {
                _line++;
            }
        }

        private void ReadString()
        {
            var start = _index;
            var firstLine = _line;
            var verbatim = false;
            while (_index < source.Length && source[_index] is '@' or '$')
            {
                verbatim |= source[_index++] is '@';
            }
            var quote = source[_index];
            if (quote is '"' && _index + 2 < source.Length && source.AsSpan(_index, 3).SequenceEqual("\"\"\""))
            {
                ReadRawString();
            }
            else
            {
                ReadQuoted(quote, verbatim);
            }
            Tokens.Add(new(_go && quote is '\'' ? 'n' : 's', source[start.._index], firstLine));
        }

        private void ReadRawString()
        {
            _index += 3;
            while (_index + 2 < source.Length && !source.AsSpan(_index, 3).SequenceEqual("\"\"\""))
            {
                CountLine(source[_index++]);
            }
            _index = Math.Min(source.Length, _index + 3);
        }

        /// <summary>
        /// Reads past the closing quote; an ordinary string also stops before a line break, leaving it
        /// unread and uncounted: the whitespace step counts it.
        /// </summary>
        private void ReadQuoted(char quote, bool verbatim)
        {
            var escapes = !verbatim && quote is not '`';
            _index++;
            while (_index < source.Length)
            {
                var character = source[_index];
                if (character is '\n' && escapes)
                {
                    return;
                }

                CountLine(character);

                if (character is '\\' && escapes)
                {
                    // An escaped line break continues the string on the next line.
                    CountLine(At(1));
                    _index = Math.Min(source.Length, _index + 2);
                    continue;
                }

                if (character == quote && !(verbatim && At(1) == quote))
                {
                    _index++;
                    return;
                }

                // A verbatim string's doubled quote is one escaped quote.
                _index += character == quote ? 2 : 1;
            }
        }

        private void ReadRegex()
        {
            var start = _index;
            var inClass = false;
            _index++;
            while (_index < source.Length && source[_index] is not '\n')
            {
                var character = source[_index];
                if (character is '\\')
                {
                    _index = Math.Min(source.Length, _index + 2);
                    continue;
                }

                _index++;
                if (character is '/' && !inClass)
                {
                    break;
                }

                inClass = character switch
                {
                    '[' => true,
                    ']' => false,
                    _ => inClass,
                };
            }
            SkipWhile(Identifier);
            Tokens.Add(new('s', source[start.._index], _line));
        }

        /// <summary>A number, an identifier or keyword, or punctuation (the longest operator first).</summary>
        private void ReadWord(char character)
        {
            var start = _index;
            if (char.IsAsciiDigit(character))
            {
                SkipWhile(item => Identifier(item) || item is '.');
                Tokens.Add(new('n', source[start.._index], _line));
                return;
            }

            if (IdentifierStart(character))
            {
                _index++;
                SkipWhile(Identifier);
                Tokens.Add(new('i', source[start.._index].TrimStart('@'), _line));
                return;
            }

            var punctuation =
                (_go ? GoOperators : CLikeOperators).FirstOrDefault(op =>
                    source.AsSpan(_index).StartsWith(op, StringComparison.Ordinal)
                ) ?? character.ToString();
            Tokens.Add(new('p', punctuation, _line));
            _index += punctuation.Length;
        }

        private void SkipWhile(Func<char, bool> predicate)
        {
            while (_index < source.Length && predicate(source[_index]))
            {
                _index++;
            }
        }
    }
}
