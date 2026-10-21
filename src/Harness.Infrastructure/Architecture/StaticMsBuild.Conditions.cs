using System.Globalization;

namespace Harness.Infrastructure.Architecture;

public sealed partial class StaticMsBuild
{
    private (bool Value, bool Known) Condition(Context context, string text, string directory)
    {
        if (text.Trim().Length is 0)
        {
            return (true, true);
        }

        if (text.Length > 8192)
        {
            return (true, false);
        }

        var tokens = Tokens(Property.Replace(Expand(context, text.Trim(), directory), ""));
        if (tokens.Count > 512)
        {
            return (true, false);
        }

        var parser = new ConditionParser(tokens, path => repository.Exists(repository.Relative(Path.GetFullPath(path.Length is 0 ? "." : path.Replace('\\', Path.DirectorySeparatorChar), directory))));
        var answer = parser.Or();
        return parser.Complete && answer.Known ? answer : (true, false);
    }
    private sealed class ConditionParser(IReadOnlyList<string> tokens, Func<string, bool> exists)
    {
        private int _index;
        internal bool Complete => _index == tokens.Count;
        private string Peek => _index < tokens.Count ? tokens[_index] : "";
        internal (bool Value, bool Known) Or()
        {
            var value = And();
            while (Peek.Equals("or", StringComparison.OrdinalIgnoreCase))
            {
                _index++;
                var next = And();
                value = (value, next) switch
                {
                    ({ Known: true, Value: true }, _) or (_, { Known: true, Value: true }) => (true, true),
                    ({ Known: true }, { Known: true }) => (false, true),
                    _ => (true, false),
                };
            }
            return value;
        }
        private (bool Value, bool Known) And()
        {
            var value = Not();
            while (Peek.Equals("and", StringComparison.OrdinalIgnoreCase))
            {
                _index++;
                var next = Not();
                value = (value, next) switch
                {
                    ({ Known: true, Value: false }, _) or (_, { Known: true, Value: false }) => (false, true),
                    ({ Known: true }, { Known: true }) => (true, true),
                    _ => (true, false),
                };
            }
            return value;
        }
        private (bool Value, bool Known) Not()
        {
            if (Peek is not "!")
            {
                return Compare();
            }

            _index++;
            var value = Not();
            return (!value.Value, value.Known);
        }
        private (bool Value, bool Known) Compare()
        {
            if (Peek is "(")
            {
                _index++;
                var value = Or();
                if (Peek is not ")")
                {
                    return (value.Value, false);
                }

                _index++;
                return value;
            }
            if (Peek.Equals("exists", StringComparison.OrdinalIgnoreCase) && _index + 1 < tokens.Count && tokens[_index + 1] is "(")
            {
                _index += 2;
                var argument = Operand();
                if (Peek is ")")
                {
                    _index++;
                }

                return (exists(argument), !argument.Contains("$(", StringComparison.Ordinal));
            }
            var left = Operand();
            var op = Peek;
            if (op is not ("==" or "!=" or "<" or ">" or "<=" or ">="))
            {
                return (left.Equals("true", StringComparison.OrdinalIgnoreCase), bool.TryParse(left, out _));
            }

            _index++;
            var right = Operand();
            if (left.Contains("$(", StringComparison.Ordinal) || right.Contains("$(", StringComparison.Ordinal))
            {
                return (false, false);
            }

            if (op is "==" or "!=")
            {
                return (left.Equals(right, StringComparison.OrdinalIgnoreCase) == (op is "=="), true);
            }

            if (!double.TryParse(left.TrimStart('v', 'V'), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) || !double.TryParse(right.TrimStart('v', 'V'), NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            {
                return (false, false);
            }

            return (op switch { "<" => a < b, ">" => a > b, "<=" => a <= b, _ => a >= b }, true);
        }
        private string Operand()
        {
            var text = Peek;
            _index++;
            return text.Length >= 2 && text[0] is '\'' && text[^1] is '\'' ? text[1..^1] : text;
        }
    }
    private static List<string> Tokens(string text)
    {
        List<string> tokens = [];
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            { i++; continue; }
            var start = i;
            switch (text[i])
            {
                case '\'':
                    var closing = text.IndexOf('\'', i + 1);
                    i = closing < 0 ? text.Length : closing + 1;
                    break;
                case '(' or ')':
                    i++;
                    break;
                case '<' or '>' or '!' or '=':
                    i += i + 1 < text.Length && text[i + 1] is '=' ? 2 : 1;
                    break;
                case '$' when i + 1 < text.Length && text[i + 1] is '(':
                    i++;
                    var depth = 0;
                    do
                    {
                        if (text[i] is '(')
                        {
                            depth++;
                        }

                        if (text[i] is ')')
                        {
                            depth--;
                        }

                        i++;
                    } while (i < text.Length && depth > 0);
                    break;
                default:
                    while (i < text.Length && !" \t\n\r'()=!<>".Contains(text[i], StringComparison.Ordinal))
                    {
                        i++;
                    }

                    break;
            }
            tokens.Add(text[start..i]);
        }
        return tokens;
    }
}
