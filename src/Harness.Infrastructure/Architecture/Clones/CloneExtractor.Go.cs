using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Clones;

public sealed partial class CloneExtractor
{
    private static readonly Lazy<(string Name, IReadOnlyList<string> Canonical)[]> StandardForms = new(() =>
    {
        (string Name, string Source)[] sources =
        [
            ("cmp.Or", "func f(vs ...string) string { for _, v := range vs { if v != \"\" { return v } }; return \"\" }"),
            ("cmp.Or", "func f(vs ...int) int { for _, v := range vs { if v != 0 { return v } }; return 0 }"),
            ("slices.Contains", "func f(xs []string, x string) bool { for _, y := range xs { if y == x { return true } }; return false }"),
            ("slices.Contains", "func f(xs []string, x string) bool { for _, y := range xs { if x == y { return true } }; return false }"),
            ("slices.Index", "func f(xs []string, x string) int { for i, y := range xs { if y == x { return i } }; return -1 }"),
            ("max", "func f(a, b int) int { if a > b { return a }; return b }"),
            ("min", "func f(a, b int) int { if a < b { return a }; return b }"),
            ("slices.Clone", "func f(xs []string) []string { out := make([]string, len(xs)); copy(out, xs); return out }"),
        ];
        return sources.Select(source => (source.Name, GoFunctions("std.go", CloneLexer.Tokenize(source.Source, "go"), []).Single().Canonical)).ToArray();
    });
    public static string Standard(CloneFunction function) => function.Language is "go" ? StandardForms.Value.FirstOrDefault(standard => standard.Canonical.SequenceEqual(function.Canonical, StringComparer.Ordinal)).Name ?? "" : "";
    private static readonly HashSet<string> Builtins = CloneLexer.Words("append cap clear close copy delete len make max min new panic print println recover string byte rune int int64 float64");
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "S127:For loop stop conditions should be invariant", Justification = "The token cursor deliberately skips matched parameter/body subtrees.")]
    private static List<CloneFunction> GoFunctions(string path, List<CodeToken> tokens, IReadOnlyList<List<CodeToken>> peers)
    {
        HashSet<string> types = new(StringComparer.Ordinal), values = new(StringComparer.Ordinal);
        foreach (var peer in peers)
        {
            var braces = 0;
            for (var index = 0; index + 1 < peer.Count; index++)
            {
                if (peer[index].Text is "{")
                {
                    braces++;
                }
                else if (peer[index].Text is "}")
                {
                    braces--;
                }

                if (braces is not 0 || peer[index].Text is not ("type" or "const" or "var"))
                {
                    continue;
                }

                var kind = peer[index].Text;
                var names = kind is "type" ? types : values;
                if (peer[index + 1].Kind is 'i')
                {
                    names.Add(peer[index + 1].Text);
                }
                else if (peer[index + 1].Text is "(")
                {
                    var end = CloneLexer.Match(peer, index + 1);
                    for (var cursor = index + 2; cursor < end; cursor++)
                    {
                        if (peer[cursor].Kind is 'i' && (cursor == index + 2 || peer[cursor].Line > peer[cursor - 1].Line))
                        {
                            names.Add(peer[cursor].Text);
                        }
                    }
                }
            }
        }
        var imports = StandardImports(tokens);
        List<CloneFunction> output = [];
        var depth = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text is "{")
            {
                depth++;
            }
            else if (tokens[index].Text is "}")
            {
                depth--;
            }

            if (depth is not 0 || tokens[index].Text is not "func")
            {
                continue;
            }

            var declaration = index;
            var cursor = index + 1;
            List<CodeToken> receiver = [];
            if (cursor < tokens.Count && tokens[cursor].Text is "(")
            {
                var closeReceiver = CloneLexer.Match(tokens, cursor);
                if (closeReceiver < 0)
                {
                    continue;
                }

                receiver = tokens.GetRange(cursor + 1, closeReceiver - cursor - 1);
                cursor = closeReceiver + 1;
            }
            if (cursor >= tokens.Count || tokens[cursor].Kind is not 'i')
            {
                continue;
            }

            var name = tokens[cursor++].Text;
            if (receiver.Count > 0)
            {
                var receiverType = receiver.Skip(1).FirstOrDefault(token => token.Kind is 'i')?.Text ?? "?";
                name = receiverType + "." + name;
            }
            if (cursor < tokens.Count && tokens[cursor].Text is "[")
            {
                var close = CloneLexer.Match(tokens, cursor);
                if (close < 0)
                {
                    continue;
                }

                cursor = close + 1;
            }
            if (cursor >= tokens.Count || tokens[cursor].Text is not "(")
            {
                continue;
            }

            var parameters = cursor;
            var closeParameters = CloneLexer.Match(tokens, parameters);
            if (closeParameters < 0)
            {
                continue;
            }

            var body = closeParameters + 1;
            while (body < tokens.Count && tokens[body].Text is not "{")
            {
                body++;
            }

            if (body >= tokens.Count)
            {
                continue;
            }

            var end = CloneLexer.Match(tokens, body);
            if (end < 0)
            {
                continue;
            }

            var (parameterNames, parameterTypes) = GoFields(tokens.GetRange(parameters + 1, closeParameters - parameters - 1));
            var resultTokens = tokens.GetRange(closeParameters + 1, body - closeParameters - 1);
            if (resultTokens.Count > 0 && resultTokens[0].Text is "(" && CloneLexer.Match(resultTokens, 0) == resultTokens.Count - 1)
            {
                resultTokens = resultTokens.GetRange(1, resultTokens.Count - 2);
            }

            var (resultNames, resultTypes) = GoFields(resultTokens);
            HashSet<string> locals = new(parameterNames.Concat(resultNames), StringComparer.Ordinal);
            if (receiver.Count > 1 && receiver[0].Kind is 'i')
            {
                locals.Add(receiver[0].Text);
            }

            for (var current = body + 1; current < end; current++)
            {
                if (tokens[current].Text is ":=")
                {
                    for (var before = current - 1; before > body && tokens[before].Kind is 'i'; before -= 2)
                    {
                        locals.Add(tokens[before].Text);
                        if (before - 1 <= body || tokens[before - 1].Text is not ",")
                        {
                            break;
                        }
                    }
                }

                if (tokens[current].Text is "var" or "const" && current + 1 < end && tokens[current + 1].Kind is 'i')
                {
                    locals.Add(tokens[current + 1].Text);
                }

                if (tokens[current].Text is "func" && current + 1 < end && tokens[current + 1].Text is "(")
                {
                    var close = CloneLexer.Match(tokens, current + 1);
                    if (close > 0)
                    {
                        locals.UnionWith(GoFields(tokens.GetRange(current + 2, close - current - 2)).Names);
                    }
                }
            }
            string Qualify(string type) => EcosystemReader.Rx(@"\b[A-Za-z_]\w*\b").Replace(type, match => types.Contains(match.Value) && (match.Index is 0 || type[match.Index - 1] is not '.') ? Directory(path) + "." + match.Value : match.Value);
            List<string> signature = [.. resultTypes.Select(Qualify), "|", .. parameterTypes.Select(Qualify)];
            Dictionary<string, string> names = new(StringComparer.Ordinal);
            List<string> canonical = [], shape = [];
            var splitter = new StatementSplitter(true);
            var data = false;
            for (var current = body; current <= end; current++)
            {
                var token = tokens[current];
                if (current > body && token.Line > tokens[current - 1].Line && SemiAfter(tokens[current - 1]))
                {
                    splitter.End();
                }

                var previous = current > body ? tokens[current - 1].Text : "";
                if (token.Text is ";")
                { splitter.Add(";", ";", false, token.Line); continue; }
                switch (token.Kind)
                {
                    case 'i' when !CloneLexer.GoKeywords.Contains(token.Text):
                        data |= previous is not "." && !locals.Contains(token.Text) && values.Contains(token.Text);
                        var local = previous is not "." && !locals.Contains(token.Text) && types.Contains(token.Text) ? Directory(path) + "." + token.Text : LocalName(names, locals, token.Text);
                        canonical.Add(local);
                        shape.Add("$");
                        splitter.Token(local, token.Text, local != token.Text && locals.Contains(token.Text), token.Line);
                        break;
                    case 's':
                        data |= SignificantString(token.Text);
                        canonical.Add(token.Text);
                        shape.Add("\"");
                        splitter.Token(token.Text, token.Text, false, token.Line);
                        break;
                    case 'n':
                        data |= !token.Text.StartsWith('\'') && token.Text is not "0" and not "1";
                        canonical.Add("0");
                        shape.Add("0");
                        splitter.Token("0", token.Text, false, token.Line);
                        break;
                    default:
                        canonical.Add(token.Text);
                        shape.Add(token.Text);
                        splitter.Token(token.Text, token.Text, false, token.Line);
                        break;
                }
            }
            splitter.End();
            List<string> calls = [];
            for (var current = body + 1; current < end; current++)
            {
                if (tokens[current].Text is "(" && tokens[current - 1].Kind is 'i' && !CloneLexer.GoKeywords.Contains(tokens[current - 1].Text))
                {
                    var called = tokens[current - 1].Text;
                    if (current >= body + 3 && tokens[current - 2].Text is ".")
                    {
                        calls.Add(imports.Contains(tokens[current - 3].Text) ? tokens[current - 3].Text + "." + called : "");
                    }
                    else if (!Builtins.Contains(called))
                    {
                        calls.Add(called);
                    }
                }
            }

            output.Add(new() { Path = path, Name = name, Line = tokens[declaration].Line, End = tokens[end].Line, Language = "go", Canonical = canonical, Shape = shape, Signature = signature, Data = data, Statements = splitter.Statements, Wraps = calls.Count is 1 && calls[0].Contains('.', StringComparison.Ordinal) ? calls[0] : null });
            index = end;
        }
        return output;
    }
    private static bool SemiAfter(CodeToken token) => token.Kind is 'n' or 's' || token.Kind is 'i' && (!CloneLexer.GoKeywords.Contains(token.Text) || token.Text is "break" or "continue" or "fallthrough" or "return") || token.Text is "++" or "--" or ")" or "]" or "}";
    private static HashSet<string> StandardImports(List<CodeToken> tokens)
    {
        HashSet<string> output = new(StringComparer.Ordinal);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text is not "import")
            {
                continue;
            }

            var end = index + 2;
            if (index + 1 < tokens.Count && tokens[index + 1].Text is "(")
            {
                end = CloneLexer.Match(tokens, index + 1);
            }
            else if (index + 2 < tokens.Count && tokens[index + 1].Kind is 'i')
            {
                end++;
            }

            for (var cursor = index + 1; cursor < Math.Min(end, tokens.Count); cursor++)
            {
                if (tokens[cursor].Kind is 's')
                {
                    var path = tokens[cursor].Text.Trim('"', '`');
                    if (!path.Split('/')[0].Contains('.', StringComparison.Ordinal))
                    {
                        output.Add(cursor > index + 1 && tokens[cursor - 1].Kind is 'i' ? tokens[cursor - 1].Text : path.Split('/')[^1]);
                    }
                }
            }
        }
        return output;
    }
    private static (List<string> Names, List<string> Types) GoFields(List<CodeToken> tokens)
    {
        List<List<CodeToken>> groups = [];
        List<CodeToken> current = [];
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Text is "(" or "[" or "{")
            {
                depth++;
            }
            else if (token.Text is ")" or "]" or "}")
            {
                depth--;
            }

            if (depth is 0 && token.Text is ",")
            { groups.Add(current); current = []; }
            else
            {
                current.Add(token);
            }
        }
        if (current.Count > 0)
        {
            groups.Add(current);
        }

        List<string> names = [], types = [], pending = [];
        foreach (var group in groups)
        {
            var named = group.Count >= 2 && group[0].Kind is 'i' && group[1].Text is not "." && group[0].Text is not ("map" or "chan" or "func" or "interface" or "struct");
            if (group.Count is 1 && group[0].Kind is 'i' && groups.Any(other => other.Count >= 2 && other[0].Kind is 'i' && other[1].Kind is 'i'))
            { pending.Add(group[0].Text); continue; }
            var type = GoType(group.Skip(named ? 1 : 0).ToList());
            if (named)
            {
                pending.Add(group[0].Text);
                names.AddRange(pending);
                types.AddRange(Enumerable.Repeat(type, pending.Count));
                pending.Clear();
            }
            else
            {
                types.Add(type);
            }
        }
        types.AddRange(pending);
        return (names, types);
    }
    private static string GoType(List<CodeToken> tokens)
    {
        if (tokens.Count > 1 && tokens[0].Text is "func" && tokens[1].Text is "(")
        {
            var close = CloneLexer.Match(tokens, 1);
            if (close > 0)
            {
                var signature = "func(" + string.Join(", ", GoFields(tokens.GetRange(2, close - 2)).Types) + ")";
                if (close + 1 < tokens.Count)
                {
                    signature += " " + GoType(tokens.GetRange(close + 1, tokens.Count - close - 1));
                }

                return signature;
            }
        }
        return string.Concat(tokens.Select(token => token.Text));
    }
    private static bool SignificantString(string text)
    {
        try
        {
            var value = text.Length > 1 ? text[1..^1] : text;
            if (!text.StartsWith('`'))
            {
                value = System.Text.RegularExpressions.Regex.Unescape(value);
            }

            return value.EnumerateRunes().Count() > 1;
        }
        catch (ArgumentException) { return true; }
    }
}
