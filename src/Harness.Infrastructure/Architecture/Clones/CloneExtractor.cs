using System.Globalization;
using System.Text;
using Harness.Core.Architecture;
using Harness.Core.Retrieval;
using Harness.Infrastructure.Architecture.Complexity;

namespace Harness.Infrastructure.Architecture.Clones;

/// <summary>Bounded file-level function extraction, not a symbol index or compiler analysis.</summary>
public sealed partial class CloneExtractor(AnalysisRepository repository)
{
    private IReadOnlyList<CloneFunction>? _functions;
    private readonly List<CloneBranch> _branches = [];
    private readonly List<ComplexityMember> _complexity = [];

    public IReadOnlyList<CloneBranch> Branches()
    {
        Extract();
        return _branches;
    }

    /// <summary>C# members' cyclomatic complexity, counted from the same tokens as the functions.</summary>
    public IReadOnlyList<ComplexityMember> Complexity()
    {
        Extract();
        return _complexity;
    }

    public IReadOnlyList<CloneFunction> Extract()
    {
        if (_functions is not null)
        {
            return _functions;
        }

        List<CloneFunction> functions = [];
        List<(string Path, List<CodeToken> Tokens)> go = [];
        foreach (var file in repository.Files.Where(Reads))
        {
            var text = repository.Read(file);
            if (Encoding.UTF8.GetByteCount(text) > 1024 * 1024)
            {
                continue;
            }

            var language = Language(file);
            var tokens = CloneLexer.Tokenize(text, language);
            if (language is "go")
            {
                go.Add((file, tokens));
            }
            else
            {
                functions.AddRange(CFunctions(file, language, tokens));
                _complexity.AddRange(
                    language is "cs" ? ComplexityCounter.CSharp(file, tokens) : ComplexityCounter.Script(file, tokens)
                );
            }
        }
        foreach (var file in go)
        {
            var peers = go.Where(peer => Directory(peer.Path) == Directory(file.Path))
                .Select(peer => peer.Tokens)
                .ToArray();
            functions.AddRange(GoFunctions(file.Path, file.Tokens, peers));
            _branches.AddRange(GoBranches(file.Path, file.Tokens));
            _complexity.AddRange(ComplexityCounter.Go(file.Path, file.Tokens));
        }
        return _functions = functions;
    }

    public EvidenceFunction? Locate(EvidenceSite site)
    {
        var functions = Extract()
            .Where(function => function.Path == site.Path && function.Line <= site.Line && site.Line <= function.End)
            .ToArray();
        var name = site.Name ?? "?";
        var named = functions
            .Where(function => function.Name == name || function.Name.Split('.')[^1] == name)
            .MinBy(function => Math.Abs(function.Line - site.Line));
        var found = named ?? functions.MinBy(function => function.End - function.Line);
        return found is null ? null : new(found.Name, found.Line, found.End);
    }

    public static bool Reads(string file) =>
        Language(file).Length > 0
        && !TaskParsing.IsTestFile(file)
        && !ChangeCoupling.Generated(file)
        && !file.Split('/').Any(part => part is "node_modules" or "vendor" or "testdata" || part.StartsWith('.'));

    private static string Language(string file) =>
        Path.GetExtension(file).ToUpperInvariant() switch
        {
            ".GO" => "go",
            ".CS" => "cs",
            ".TS"
            or ".TSX"
            or ".JS"
            or ".JSX"
            or ".MJS"
                when !file.EndsWith(".d.ts", StringComparison.Ordinal)
                    && !file.EndsWith(".min.js", StringComparison.Ordinal) => "ts",
            _ => "",
        };

    private static string Directory(string file) =>
        Path.GetDirectoryName(file)?.Replace('\\', '/') is { Length: > 0 } dir ? dir : ".";

    internal sealed record Declaration(CloneFunction Function, int Parameters, int Body, int End);

    private static List<CloneFunction> CFunctions(string path, string language, List<CodeToken> tokens)
    {
        var declarations = CDeclarations(path, language, tokens);
        var locals = declarations
            .Select(declaration =>
                Forms(tokens, declaration.Parameters, declaration.Body, declaration.End, null, language is "ts").Locals
            )
            .ToArray();
        return declarations
            .Select(declaration =>
                CFunction(tokens, language, declaration, CapturedNames(declarations, locals, declaration))
            )
            .ToList();
    }

    /// <summary>Named members and methods with a body, and (TypeScript) arrow functions bound to a name.</summary>
    private static List<Declaration> CDeclarations(string path, string language, List<CodeToken> tokens)
    {
        List<Declaration> declarations = [];
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (DeclarationAt(path, language, tokens, index) is { } declaration)
            {
                declarations.Add(declaration);
            }
        }
        return declarations;
    }

    private static Declaration? DeclarationAt(string path, string language, List<CodeToken> tokens, int index)
    {
        if (NamedCall(tokens, index))
        {
            return MemberDeclaration(path, language, tokens, index);
        }

        return language is "ts" && tokens[index].Text is "=>" && tokens[index + 1].Text is "{"
            ? ArrowDeclaration(path, language, tokens, index)
            : null;
    }

    /// <summary>An identifier, not a keyword, directly followed by '('.</summary>
    private static bool NamedCall(List<CodeToken> tokens, int index) =>
        tokens[index].Kind is 'i'
        && tokens[index + 1].Text is "("
        && !CloneLexer.Control.Contains(tokens[index].Text)
        && !CloneLexer.Keywords.Contains(tokens[index].Text);

    /// <summary>
    /// The member named at <paramref name="name"/>, unless it is a call, a constructor call or a type.
    /// </summary>
    private static Declaration? MemberDeclaration(string path, string language, List<CodeToken> tokens, int name)
    {
        if (
            name > 0
            && (tokens[name - 1].Text is "." or "?." or "new" || CloneLexer.Declares.Contains(tokens[name - 1].Text))
        )
        {
            return null;
        }

        if (name > 0 && language is not "ts" && !CloneLexer.TypeLike(tokens[name - 1]))
        {
            return null;
        }

        var close = CloneLexer.Match(tokens, name + 1);
        return close >= 0 && MemberBody(language, tokens, close) is { } body
            ? Declare(path, language, tokens, name, name + 1, body)
            : null;
    }

    /// <summary>
    /// The '{' opening the body after the parameter list closed at <paramref name="close"/>, within 60
    /// tokens and before any ';', '=>', '=', '}' or ','.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeSmell",
        "S127:For loop stop conditions should be invariant",
        Justification = "The token cursor deliberately skips matched parenthesized subtrees."
    )]
    private static int? MemberBody(string language, List<CodeToken> tokens, int close)
    {
        for (var cursor = close + 1; cursor < tokens.Count && cursor < close + 60; cursor++)
        {
            var current = tokens[cursor];
            if (current.Kind is 'p' && current.Text is ";" or "=>" or "=" or "}" or ",")
            {
                return null;
            }

            if (current.Kind is 'p' && current.Text is "(" && CloneLexer.Match(tokens, cursor) is var end && end > 0)
            {
                cursor = end;
                continue;
            }

            if (current.Kind is 'p' && current.Text is "{")
            {
                return language is "cs" || cursor == close + 1 || tokens[close + 1].Text is ":" ? cursor : null;
            }

            if (language is "ts" && cursor == close + 1 && current.Text is not ":")
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>An arrow function with a block body assigned to, or a property named, an identifier.</summary>
    private static Declaration? ArrowDeclaration(string path, string language, List<CodeToken> tokens, int arrow)
    {
        var before = arrow - 1;
        var parameters = -1;
        if (before >= 0 && tokens[before].Text is ")")
        {
            parameters = OpeningParenthesis(tokens, before);
            before = parameters - 1;
        }
        else if (before >= 0 && tokens[before].Kind is 'i')
        {
            parameters = before--;
        }

        if (before >= 0 && tokens[before].Text is "async")
        {
            before--;
        }

        return before >= 1 && tokens[before].Text is "=" or ":" && tokens[before - 1].Kind is 'i' && parameters >= 0
            ? Declare(path, language, tokens, before - 1, parameters, arrow + 1)
            : null;
    }

    /// <summary>The '(' matching the ')' at <paramref name="close"/>, scanning backwards; -1 if none.</summary>
    private static int OpeningParenthesis(List<CodeToken> tokens, int close)
    {
        var depth = 0;
        for (var index = close; index >= 0; index--)
        {
            if (tokens[index].Text is ")")
            {
                depth++;
            }
            else if (tokens[index].Text is "(" && --depth is 0)
            {
                return index;
            }
        }
        return -1;
    }

    private static Declaration? Declare(
        string path,
        string language,
        List<CodeToken> tokens,
        int name,
        int parameters,
        int body
    )
    {
        var end = CloneLexer.Match(tokens, body);
        if (end < 0)
        {
            return null;
        }

        var (result, overrides) = ReturnPrefix(tokens, name);
        var close = tokens[parameters].Text is "(" ? CloneLexer.Match(tokens, parameters) : parameters;
        if (language is "ts" && close + 1 < body && tokens[close + 1].Text is ":")
        {
            result = tokens
                .Skip(close + 2)
                .Take(body - close - 2)
                .TakeWhile(token => token.Text is not "=>")
                .Select(token => token.Text)
                .ToList();
        }

        var signature = new List<string> { string.Concat(result), "|" };
        signature.AddRange(
            tokens[parameters].Text is "("
                ? ParameterTypes(tokens.GetRange(parameters + 1, close - parameters - 1), language)
                : ["?"]
        );
        return new(
            new()
            {
                Path = path,
                Name = tokens[name].Text,
                Line = tokens[name].Line,
                End = tokens[end].Line,
                Language = language,
                Override = overrides,
                Signature = signature,
            },
            parameters,
            body,
            end
        );
    }

    /// <summary>
    /// The tokens before the name (up to 12, back to a delimiter) without modifiers, and whether one was
    /// <c>override</c>.
    /// </summary>
    private static (List<string> Result, bool Overrides) ReturnPrefix(List<CodeToken> tokens, int name)
    {
        var overrides = false;
        List<string> result = [];
        for (
            var index = name - 1;
            index >= 0 && index >= name - 12 && !";{}],()=".Contains(tokens[index].Text, StringComparison.Ordinal);
            index--
        )
        {
            overrides |= tokens[index].Text is "override";
            if (!CloneLexer.Modifiers.Contains(tokens[index].Text))
            {
                result.Insert(0, tokens[index].Text);
            }
        }
        return (result, overrides);
    }

    /// <summary>
    /// The locals of every declaration enclosing <paramref name="declaration"/>, each mapped to its
    /// owner's name@line prefix; inner owners win.
    /// </summary>
    private static Dictionary<string, string> CapturedNames(
        List<Declaration> declarations,
        HashSet<string>[] locals,
        Declaration declaration
    )
    {
        Dictionary<string, string> captured = new(StringComparer.Ordinal);
        foreach (
            var outer in declarations
                .Select((value, index) => (value, index))
                .Where(pair =>
                    pair.value != declaration && pair.value.Body < declaration.Body && declaration.End <= pair.value.End
                )
                .OrderBy(pair => pair.value.Body)
        )
        {
            var owner =
                outer.value.Function.Name
                + "@"
                + outer.value.Function.Line.ToString(CultureInfo.InvariantCulture)
                + ".";
            foreach (var local in locals[outer.index])
            {
                captured[local] = owner;
            }
        }
        return captured;
    }

    private static CloneFunction CFunction(
        List<CodeToken> tokens,
        string language,
        Declaration declaration,
        Dictionary<string, string> captured
    )
    {
        var forms = Forms(
            tokens,
            declaration.Parameters,
            declaration.Body,
            declaration.End,
            captured,
            language is "ts"
        );
        return declaration.Function with
        {
            Canonical = forms.Canonical,
            Shape = forms.Shape,
            Statements = forms.Statements,
            Data = tokens
                .Skip(declaration.Body)
                .Take(declaration.End - declaration.Body)
                .Any(token =>
                    token.Kind is 'n' && token.Text is not "0" and not "1"
                    || token.Kind is 's' && token.Text.EnumerateRunes().Count() > 3
                ),
        };
    }

    private static string[] ParameterTypes(List<CodeToken> tokens, string language)
    {
        List<string> output = [];
        List<CodeToken> current = [];
        var nesting = new ParameterNesting();
        var defaultValue = false;
        foreach (var token in tokens)
        {
            if (nesting.Skips(token.Text, current.Count is 0 && language is "cs"))
            {
                continue;
            }

            if (nesting.Depth is 0 && token.Text is ",")
            {
                Flush(output, current, language);
                defaultValue = false;
            }
            else if (nesting.Depth is 0 && token.Text is "=")
            {
                defaultValue = true;
            }
            else if (!defaultValue)
            {
                current.Add(token);
            }
        }
        Flush(output, current, language);
        return output.ToArray();
    }

    /// <summary>The bracket depth in a parameter list and the C# attributes ([ at a parameter's start).</summary>
    private sealed class ParameterNesting
    {
        private int _attribute;
        internal int Depth { get; private set; }

        /// <summary>Tracks the token; true for an attribute's bracket or a token inside an attribute.</summary>
        internal bool Skips(string text, bool attributeStart)
        {
            switch (text)
            {
                case "(" or "<" or "{":
                    Depth++;
                    break;
                case ")" or ">" or "}":
                    Depth--;
                    break;
                case "[" when Depth is 0 && attributeStart:
                    _attribute++;
                    return true;
                case "[":
                    Depth++;
                    break;
                case "]" when _attribute > 0:
                    _attribute--;
                    return true;
                case "]":
                    Depth--;
                    break;
            }
            return _attribute > 0;
        }
    }

    /// <summary>Adds the parameter read so far, if any, as its type.</summary>
    private static void Flush(List<string> output, List<CodeToken> current, string language)
    {
        if (current.Count is 0)
        {
            return;
        }

        output.Add(language is "ts" ? ScriptParameterType(current) : CSharpParameterType(current));
        current.Clear();
    }

    /// <summary>The annotation after the colon, or ? when there is none.</summary>
    private static string ScriptParameterType(List<CodeToken> parameter)
    {
        var colon = parameter.FindIndex(token => token.Text is ":");
        return colon < 0 ? "?" : string.Concat(parameter.Skip(colon + 1).Select(token => token.Text));
    }

    /// <summary>The tokens before the name, without parameter modifiers.</summary>
    private static string CSharpParameterType(List<CodeToken> parameter) =>
        string.Concat(
            parameter
                .Take(parameter[^1].Kind is 'i' ? parameter.Count - 1 : parameter.Count)
                .Where(token =>
                    token.Kind is not 'i'
                    || token.Text is not ("this" or "ref" or "out" or "in" or "params" or "scoped")
                )
                .Select(token => token.Text)
        );
}
