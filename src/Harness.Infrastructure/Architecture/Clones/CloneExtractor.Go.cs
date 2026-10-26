using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture.Clones;

public sealed partial class CloneExtractor
{
    private static readonly Lazy<(string Name, IReadOnlyList<string> Canonical)[]> StandardForms = new(() =>
    {
        (string Name, string Source)[] sources =
        [
            (
                "cmp.Or",
                "func f(vs ...string) string { for _, v := range vs { if v != \"\" { return v } }; return \"\" }"
            ),
            ("cmp.Or", "func f(vs ...int) int { for _, v := range vs { if v != 0 { return v } }; return 0 }"),
            (
                "slices.Contains",
                "func f(xs []string, x string) bool { for _, y := range xs { if y == x { return true } }; return false }"
            ),
            (
                "slices.Contains",
                "func f(xs []string, x string) bool { for _, y := range xs { if x == y { return true } }; return false }"
            ),
            (
                "slices.Index",
                "func f(xs []string, x string) int { for i, y := range xs { if y == x { return i } }; return -1 }"
            ),
            ("max", "func f(a, b int) int { if a > b { return a }; return b }"),
            ("min", "func f(a, b int) int { if a < b { return a }; return b }"),
            (
                "slices.Clone",
                "func f(xs []string) []string { out := make([]string, len(xs)); copy(out, xs); return out }"
            ),
        ];
        return sources
            .Select(source =>
                (source.Name, GoFunctions("std.go", CloneLexer.Tokenize(source.Source, "go"), []).Single().Canonical)
            )
            .ToArray();
    });

    public static string Standard(CloneFunction function) =>
        function.Language is "go"
            ? StandardForms
                .Value.FirstOrDefault(standard =>
                    standard.Canonical.SequenceEqual(function.Canonical, StringComparer.Ordinal)
                )
                .Name
                ?? ""
            : "";

    private static readonly HashSet<string> Builtins = CloneLexer.Words(
        "append cap clear close copy delete len make max min new panic print println recover string byte rune int int64 float64"
    );

    /// <summary>
    /// A top-level Go func: its qualified name, receiver, and the token indexes of its parameter list
    /// and body.
    /// </summary>
    private sealed record GoDeclaration(
        string Name,
        int Line,
        List<CodeToken> Receiver,
        int Parameters,
        int CloseParameters,
        int Body,
        int End
    );

    /// <summary>The package's declared type names and value (const/var) names.</summary>
    private sealed record GoPackage(HashSet<string> Types, HashSet<string> Values);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeSmell",
        "S127:For loop stop conditions should be invariant",
        Justification = "The token cursor deliberately skips matched function bodies."
    )]
    private static List<CloneFunction> GoFunctions(
        string path,
        List<CodeToken> tokens,
        IReadOnlyList<List<CodeToken>> peers
    )
    {
        var package = GoPackageNames(peers);
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

            if (ReadGoDeclaration(tokens, index) is not { } declaration)
            {
                continue;
            }

            output.Add(GoFunction(path, tokens, declaration, package, imports));
            index = declaration.End;
        }
        return output;
    }

    private static GoPackage GoPackageNames(IReadOnlyList<List<CodeToken>> peers)
    {
        GoPackage package = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
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

                if (braces is 0 && peer[index].Text is "type" or "const" or "var")
                {
                    AddDeclaredNames(peer, index, peer[index].Text is "type" ? package.Types : package.Values);
                }
            }
        }
        return package;
    }

    /// <summary>Adds the names of the top-level type/const/var declaration at <paramref name="index"/>.</summary>
    private static void AddDeclaredNames(List<CodeToken> tokens, int index, HashSet<string> names)
    {
        if (tokens[index + 1].Kind is 'i')
        {
            names.Add(tokens[index + 1].Text);
            return;
        }

        if (tokens[index + 1].Text is not "(")
        {
            return;
        }

        var end = CloneLexer.Match(tokens, index + 1);
        for (var cursor = index + 2; cursor < end; cursor++)
        {
            if (tokens[cursor].Kind is 'i' && (cursor == index + 2 || tokens[cursor].Line > tokens[cursor - 1].Line))
            {
                names.Add(tokens[cursor].Text);
            }
        }
    }

    private static GoDeclaration? ReadGoDeclaration(List<CodeToken> tokens, int declaration)
    {
        var cursor = declaration + 1;
        List<CodeToken> receiver = [];
        if (cursor < tokens.Count && tokens[cursor].Text is "(")
        {
            var closeReceiver = CloneLexer.Match(tokens, cursor);
            if (closeReceiver < 0)
            {
                return null;
            }

            receiver = tokens.GetRange(cursor + 1, closeReceiver - cursor - 1);
            cursor = closeReceiver + 1;
        }
        if (cursor >= tokens.Count || tokens[cursor].Kind is not 'i')
        {
            return null;
        }

        var name = tokens[cursor++].Text;
        if (receiver.Count > 0)
        {
            name = GoReceiverType(receiver) + "." + name;
        }
        if (cursor < tokens.Count && tokens[cursor].Text is "[")
        {
            var close = CloneLexer.Match(tokens, cursor);
            if (close < 0)
            {
                return null;
            }

            cursor = close + 1;
        }
        return ReadGoSignature(tokens, cursor) is { } signature
            ? new(
                name,
                tokens[declaration].Line,
                receiver,
                cursor,
                signature.CloseParameters,
                signature.Body,
                signature.End
            )
            : null;
    }

    private static string GoReceiverType(List<CodeToken> receiver) =>
        receiver.Skip(1).FirstOrDefault(token => token.Kind is 'i')?.Text ?? "?";

    /// <summary>The ends of the parameter list starting at <paramref name="parameters"/> and of the body after it.</summary>
    private static (int CloseParameters, int Body, int End)? ReadGoSignature(List<CodeToken> tokens, int parameters)
    {
        if (parameters >= tokens.Count || tokens[parameters].Text is not "(")
        {
            return null;
        }

        var closeParameters = CloneLexer.Match(tokens, parameters);
        if (closeParameters < 0)
        {
            return null;
        }

        var body = closeParameters + 1;
        while (body < tokens.Count && tokens[body].Text is not "{")
        {
            body++;
        }

        if (body >= tokens.Count)
        {
            return null;
        }

        var end = CloneLexer.Match(tokens, body);
        return end < 0 ? null : (closeParameters, body, end);
    }

    private static CloneFunction GoFunction(
        string path,
        List<CodeToken> tokens,
        GoDeclaration declaration,
        GoPackage package,
        HashSet<string> imports
    )
    {
        var (parameterNames, parameterTypes) = GoFields(
            tokens.GetRange(declaration.Parameters + 1, declaration.CloseParameters - declaration.Parameters - 1)
        );
        var (resultNames, resultTypes) = GoFields(GoResultTokens(tokens, declaration));
        var locals = GoLocals(tokens, declaration, parameterNames.Concat(resultNames));
        List<string> signature =
        [
            .. resultTypes.Select(type => QualifyGoType(path, type, package.Types)),
            "|",
            .. parameterTypes.Select(type => QualifyGoType(path, type, package.Types)),
        ];
        var body = GoBody(path, tokens, declaration, locals, package);
        var calls = GoCalls(tokens, declaration, imports);
        return new()
        {
            Path = path,
            Name = declaration.Name,
            Line = declaration.Line,
            End = tokens[declaration.End].Line,
            Language = "go",
            Canonical = body.Canonical,
            Shape = body.Shape,
            Signature = signature,
            Data = body.Data,
            Statements = body.Statements,
            Wraps = calls.Count is 1 && calls[0].Contains('.', StringComparison.Ordinal) ? calls[0] : null,
        };
    }

    /// <summary>The result list between the parameters and the body, without its enclosing parentheses.</summary>
    private static List<CodeToken> GoResultTokens(List<CodeToken> tokens, GoDeclaration declaration)
    {
        var result = tokens.GetRange(
            declaration.CloseParameters + 1,
            declaration.Body - declaration.CloseParameters - 1
        );
        return result.Count > 0 && result[0].Text is "(" && CloneLexer.Match(result, 0) == result.Count - 1
            ? result.GetRange(1, result.Count - 2)
            : result;
    }

    /// <summary>Parameters, results, the receiver and every name the body declares.</summary>
    private static HashSet<string> GoLocals(
        List<CodeToken> tokens,
        GoDeclaration declaration,
        IEnumerable<string> fields
    )
    {
        HashSet<string> locals = new(fields, StringComparer.Ordinal);
        if (declaration.Receiver.Count > 1 && declaration.Receiver[0].Kind is 'i')
        {
            locals.Add(declaration.Receiver[0].Text);
        }

        var (body, end) = (declaration.Body, declaration.End);
        for (var current = body + 1; current < end; current++)
        {
            if (tokens[current].Text is ":=")
            {
                AddShortDeclared(tokens, body, current, locals);
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
        return locals;
    }

    /// <summary>Adds the comma-separated names left of the <c>:=</c> at <paramref name="assign"/>.</summary>
    private static void AddShortDeclared(List<CodeToken> tokens, int body, int assign, HashSet<string> locals)
    {
        for (var before = assign - 1; before > body && tokens[before].Kind is 'i'; before -= 2)
        {
            locals.Add(tokens[before].Text);
            if (before - 1 <= body || tokens[before - 1].Text is not ",")
            {
                break;
            }
        }
    }

    private static string QualifyGoType(string path, string type, HashSet<string> types) =>
        EcosystemReader
            .Rx(@"\b[A-Za-z_]\w*\b")
            .Replace(
                type,
                match =>
                    types.Contains(match.Value) && (match.Index is 0 || type[match.Index - 1] is not '.')
                        ? Directory(path) + "." + match.Value
                        : match.Value
            );

    private sealed record GoBodyForms(
        List<string> Canonical,
        List<string> Shape,
        IReadOnlyList<CloneStatement> Statements,
        bool Data
    );

    /// <summary>The body's canonical and shape tokens and statements, and whether it holds significant data.</summary>
    private static GoBodyForms GoBody(
        string path,
        List<CodeToken> tokens,
        GoDeclaration declaration,
        HashSet<string> locals,
        GoPackage package
    )
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        List<string> canonical = [],
            shape = [];
        var splitter = new StatementSplitter(true);
        var data = false;
        var body = declaration.Body;
        for (var current = body; current <= declaration.End; current++)
        {
            var token = tokens[current];
            if (current > body && token.Line > tokens[current - 1].Line && SemiAfter(tokens[current - 1]))
            {
                splitter.End();
            }

            if (token.Text is ";")
            {
                splitter.Add(";", ";", false, token.Line);
                continue;
            }

            var previous = current > body ? tokens[current - 1].Text : "";
            var (form, mark, own) = token.Kind switch
            {
                'i' when !CloneLexer.GoKeywords.Contains(token.Text) => GoIdentifier(
                    path,
                    token.Text,
                    previous is not "." && !locals.Contains(token.Text),
                    names,
                    locals,
                    package.Types
                ),
                's' => (token.Text, "\"", false),
                'n' => ("0", "0", false),
                _ => (token.Text, token.Text, false),
            };
            data |= GoData(token, previous, locals, package.Values);
            canonical.Add(form);
            shape.Add(mark);
            splitter.Token(form, token.Text, own, token.Line);
        }
        splitter.End();
        return new(canonical, shape, splitter.Statements, data);
    }

    /// <summary>
    /// An identifier's canonical form: a package type qualified by its directory, otherwise its local
    /// placeholder.
    /// </summary>
    private static (string Form, string Mark, bool Own) GoIdentifier(
        string path,
        string text,
        bool unqualified,
        Dictionary<string, string> names,
        HashSet<string> locals,
        HashSet<string> types
    )
    {
        var local = unqualified && types.Contains(text)
            ? Directory(path) + "." + text
            : LocalName(names, locals, text);
        return (local, "$", local != text && locals.Contains(text));
    }

    /// <summary>Whether the token is a package value, a significant string or a number other than 0 and 1.</summary>
    private static bool GoData(CodeToken token, string previous, HashSet<string> locals, HashSet<string> values) =>
        token.Kind switch
        {
            'i' when !CloneLexer.GoKeywords.Contains(token.Text) => previous is not "."
                && !locals.Contains(token.Text)
                && values.Contains(token.Text),
            's' => SignificantString(token.Text),
            'n' => !token.Text.StartsWith('\'') && token.Text is not "0" and not "1",
            _ => false,
        };

    /// <summary>The body's calls: standard-library calls as package.Name, other selector calls as "".</summary>
    private static List<string> GoCalls(List<CodeToken> tokens, GoDeclaration declaration, HashSet<string> imports)
    {
        List<string> calls = [];
        var body = declaration.Body;
        for (var current = body + 1; current < declaration.End; current++)
        {
            if (
                tokens[current].Text is not "("
                || tokens[current - 1].Kind is not 'i'
                || CloneLexer.GoKeywords.Contains(tokens[current - 1].Text)
            )
            {
                continue;
            }

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
        return calls;
    }

    private static bool SemiAfter(CodeToken token) =>
        token.Kind is 'n' or 's'
        || token.Kind is 'i'
            && (
                !CloneLexer.GoKeywords.Contains(token.Text)
                || token.Text is "break" or "continue" or "fallthrough" or "return"
            )
        || token.Text is "++" or "--" or ")" or "]" or "}";

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
                        output.Add(
                            cursor > index + 1 && tokens[cursor - 1].Kind is 'i'
                                ? tokens[cursor - 1].Text
                                : path.Split('/')[^1]
                        );
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
            {
                groups.Add(current);
                current = [];
            }
            else
            {
                current.Add(token);
            }
        }
        if (current.Count > 0)
        {
            groups.Add(current);
        }

        List<string> names = [],
            types = [],
            pending = [];
        foreach (var group in groups)
        {
            var named =
                group.Count >= 2
                && group[0].Kind is 'i'
                && group[1].Text is not "."
                && group[0].Text is not ("map" or "chan" or "func" or "interface" or "struct");
            if (
                group.Count is 1
                && group[0].Kind is 'i'
                && groups.Any(other => other.Count >= 2 && other[0].Kind is 'i' && other[1].Kind is 'i')
            )
            {
                pending.Add(group[0].Text);
                continue;
            }
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
        catch (ArgumentException)
        {
            return true;
        }
    }
}
