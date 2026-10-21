namespace Harness.Infrastructure.Architecture.Clones;

internal sealed record CodeToken(char Kind, string Text, int Line);

internal static class CloneLexer
{
    internal static readonly HashSet<string> Keywords = Words("abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach function get goto if implicit in int interface internal is let lock long namespace new null object of operator out override params private protected public readonly ref return sbyte sealed set short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while yield record init required when where with nameof undefined export import from extends implements instanceof delete type declare keyof readonly");
    internal static readonly HashSet<string> GoKeywords = Words("break default func interface select case defer go map struct chan else goto package switch const fallthrough if range type continue for import return var");
    internal static readonly HashSet<string> Control = Words("if for foreach while switch catch using lock fixed return new nameof typeof sizeof when await throw else do function checked unchecked default is as not and or");
    internal static readonly HashSet<string> Modifiers = Words("public private protected internal static async virtual override abstract sealed extern unsafe new partial readonly export default function const let var declare get set");
    internal static readonly HashSet<string> Declares = Words("class record struct interface");
    internal static HashSet<string> Words(string text) => new(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    internal static bool IdentifierStart(char character) => character is '_' or '$' or '@' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '\u0080';
    internal static bool Identifier(char character) => IdentifierStart(character) || char.IsAsciiDigit(character);
    internal static bool TypeLike(CodeToken token) => token.Kind is 'i' && !Control.Contains(token.Text) || token.Kind is 'p' && token.Text is ">" or "]" or "?";
    internal static int Match(IReadOnlyList<CodeToken> tokens, int at)
    {
        var open = tokens[at].Text;
        var close = open switch { "(" => ")", "{" => "}", "[" => "]", _ => "" };
        var depth = 0;
        for (var index = at; index < tokens.Count; index++)
        {
            if (tokens[index].Kind is not 'p')
                continue;
            if (tokens[index].Text == open)
                depth++;
            else if (tokens[index].Text == close && --depth is 0)
                return index;
        }
        return -1;
    }
    internal static List<CodeToken> Tokenize(string source, string language)
    {
        List<CodeToken> tokens = [];
        var line = 1;
        var index = 0;
        var js = language is "ts";
        var go = language is "go";
        var operators = go ? new[] { "...", "<<=", ">>=", "&^=", ":=", "++", "--", "<<", ">>", "&^", "<-", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "==", "!=", "<=", ">=", "&&", "||" } : ["=>", "?.", "===", "!==", "==", "!=", "<=", ">=", "&&", "||"];
        while (index < source.Length)
        {
            var character = source[index];
            if (char.IsWhiteSpace(character))
            { if (character is '\n') { line++; } index++; continue; }
            if (character is '/' && index + 1 < source.Length && source[index + 1] is '/')
            { while (index < source.Length && source[index] is not '\n') { index++; } continue; }
            if (character is '/' && index + 1 < source.Length && source[index + 1] is '*')
            {
                index += 2;
                while (index + 1 < source.Length && !(source[index] is '*' && source[index + 1] is '/'))
                { if (source[index++] is '\n') line++; }
                index = Math.Min(source.Length, index + 2);
                continue;
            }
            if (character is '#' && !js && !go && (tokens.Count is 0 || source[(source.LastIndexOf('\n', Math.Max(0, index - 1)) + 1)..index].All(item => item is ' ' or '\t')))
            { while (index < source.Length && source[index] is not '\n') { index++; } continue; }
            var start = index;
            var firstLine = line;
            if (character is '"' or '\'' or '`' || character is '@' or '$' && index + 1 < source.Length && (source[index + 1] is '"' || source[index + 1] is '@' or '$' && index + 2 < source.Length && source[index + 2] is '"'))
            {
                var verbatim = false;
                while (index < source.Length && source[index] is '@' or '$')
                { verbatim |= source[index++] is '@'; }
                var quote = source[index];
                if (quote is '"' && index + 2 < source.Length && source.AsSpan(index, 3).SequenceEqual("\"\"\""))
                {
                    index += 3;
                    while (index + 2 < source.Length && !source.AsSpan(index, 3).SequenceEqual("\"\"\""))
                    { if (source[index++] is '\n') line++; }
                    index = Math.Min(source.Length, index + 3);
                }
                else
                {
                    index++;
                    while (index < source.Length)
                    {
                        if (source[index] is '\n')
                        { line++; if (quote is not '`' && !verbatim) break; }
                        if (source[index] is '\\' && !verbatim && quote is not '`')
                        { index = Math.Min(source.Length, index + 2); continue; }
                        if (source[index] == quote)
                        {
                            if (verbatim && index + 1 < source.Length && source[index + 1] == quote)
                            { index += 2; continue; }
                            index++;
                            break;
                        }
                        index++;
                    }
                }
                tokens.Add(new(go && quote is '\'' ? 'n' : 's', source[start..index], firstLine));
                continue;
            }
            if (character is '/' && js && RegexAfter(tokens.LastOrDefault()))
            {
                var inClass = false;
                index++;
                while (index < source.Length && source[index] is not '\n')
                {
                    if (source[index] is '\\')
                    { index = Math.Min(source.Length, index + 2); continue; }
                    if (source[index] is '[')
                        inClass = true;
                    else if (source[index] is ']')
                        inClass = false;
                    else if (source[index] is '/' && !inClass)
                    { index++; break; }
                    index++;
                }
                while (index < source.Length && Identifier(source[index]))
                    index++;
                tokens.Add(new('s', source[start..index], firstLine));
                continue;
            }
            if (char.IsAsciiDigit(character))
            {
                while (index < source.Length && (Identifier(source[index]) || source[index] is '.'))
                    index++;
                tokens.Add(new('n', source[start..index], line));
                continue;
            }
            if (IdentifierStart(character))
            {
                index++;
                while (index < source.Length && Identifier(source[index]))
                    index++;
                tokens.Add(new('i', source[start..index].TrimStart('@'), line));
                continue;
            }
            var op = operators.FirstOrDefault(op => source.AsSpan(index).StartsWith(op, StringComparison.Ordinal));
            var punctuation = op ?? character.ToString();
            tokens.Add(new('p', punctuation, line));
            index += punctuation.Length;
        }
        return tokens;
    }
    private static bool RegexAfter(CodeToken? previous) => previous is null || previous.Kind is 'p' && ("(,=:[!&|?{};+-*%<>~^".Contains(previous.Text, StringComparison.Ordinal) || previous.Text is "=>") || previous.Kind is 'i' && previous.Text is "return" or "typeof" or "case" or "in" or "of";
}
