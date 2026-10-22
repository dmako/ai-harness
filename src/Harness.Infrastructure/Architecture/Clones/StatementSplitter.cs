using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Clones;

internal sealed class StatementSplitter(bool go = false)
{
    internal static readonly HashSet<string> Opens = CloneLexer.Words(
        "if for foreach while switch lock try catch finally do case else select go"
    );
    private static readonly HashSet<string> Zeros = CloneLexer.Words(
        "\u0001 nil null 0 \"\" false true default , err ( )"
    );
    private readonly List<string> _key = [],
        _raw = [],
        _own = [];
    private readonly Stack<bool> _literal = [];
    private int _line;
    public List<CloneStatement> Statements { get; } = [];

    internal void Add(string canonical, string raw, bool own, int line)
    {
        if (_key.Count is 0)
        {
            _line = line;
        }

        _key.Add(own ? "\u0001" : canonical);
        if (own)
        {
            _own.Add(canonical);
        }

        _raw.Add(raw);
    }

    internal void End()
    {
        if (_key.Count is 0)
        {
            return;
        }

        var idiom = Idiomatic(_key);
        var header =
            !idiom
            && (_key[^1] is "{" || Opens.Contains(_key[0]) || _key[0] is "using" && _key.Count > 1 && _key[1] is "(");
        var callee = Callee(_key);
        var data = _raw.Any(raw =>
            raw.StartsWith('"') && raw.Length > 2
            || raw.StartsWith('\'') && raw.Length > 3
            || raw.Length > 0 && char.IsAsciiDigit(raw[0]) && raw is not "0" and not "1"
        );
        Statements.Add(
            new(
                string.Join(' ', _key),
                _own.ToArray(),
                _raw.ToArray(),
                _line,
                _key.Count,
                idiom,
                header,
                !idiom && (!header || callee.Length > 0),
                callee,
                data
            )
        );
        _key.Clear();
        _raw.Clear();
        _own.Clear();
    }

    internal void Token(string canonical, string raw, bool own, int line)
    {
        switch (raw)
        {
            case ";":
                End();
                return;
            case "{":
                var literal = !Block();
                _literal.Push(literal);
                Add(canonical, raw, own, line);
                if (!literal)
                {
                    End();
                }

                return;
            case "}":
                if (_literal.TryPop(out var isLiteral) && isLiteral)
                {
                    Add(canonical, raw, false, line);
                    return;
                }
                End();
                Add(canonical, raw, false, line);
                End();
                return;
            default:
                Add(canonical, raw, own, line);
                return;
        }
    }

    private bool Block()
    {
        if (_key.Count is 0)
        {
            return true;
        }

        var open = _key.Count(key => key is "(") - _key.Count(key => key is ")");
        if (open > 0 && !_key.Contains("=>", StringComparer.Ordinal) && !_key.Contains("func", StringComparer.Ordinal))
        {
            return false;
        }

        if (Opens.Contains(_key[0]) || _key[0] is "func")
        {
            return true;
        }

        return go ? _key.Contains("func", StringComparer.Ordinal) : _raw[^1] is ")" or "=>";
    }

    private static bool Idiomatic(List<string> key) =>
        key.Count <= 2
        || key[0] is "\u0001" && key[1] is "?."
        || key[0] switch
        {
            "}" or "{" or "else" or "defer" => true,
            "return" or "throw" => key.Skip(1).All(Zeros.Contains),
            "using" => key.Count > 1 && key[1] is "var",
            "if" => key.Count <= 8 && key.Any(token => token is "nil" or "null" or "!"),
            _ => false,
        };

    private static string Callee(List<string> key)
    {
        for (var index = 1; index < key.Count; index++)
        {
            if (
                key[index] is "("
                && !Opens.Contains(key[index - 1])
                && !CloneLexer.Keywords.Contains(key[index - 1])
                && Word(key[index - 1])
            )
            {
                return index >= 3 && key[index - 2] is "." ? key[index - 3] + "." + key[index - 1] : key[index - 1];
            }
        }

        return "";
    }

    private static bool Word(string token) =>
        token.Length > 0 && (char.IsAsciiLetterOrDigit(token[^1]) || token[^1] is '_' or '>' or ')' or ']');
}
