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
                if (language is "cs")
                {
                    _complexity.AddRange(ComplexityCounter.Members(file, tokens));
                }
            }
        }
        foreach (var file in go)
        {
            var peers = go.Where(peer => Directory(peer.Path) == Directory(file.Path))
                .Select(peer => peer.Tokens)
                .ToArray();
            functions.AddRange(GoFunctions(file.Path, file.Tokens, peers));
            _branches.AddRange(GoBranches(file.Path, file.Tokens));
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeSmell",
        "S127:For loop stop conditions should be invariant",
        Justification = "The token cursor deliberately skips matched parameter/body subtrees."
    )]
    private static List<CloneFunction> CFunctions(string path, string language, List<CodeToken> tokens)
    {
        List<Declaration> declarations = [];
        void Add(int name, int parameters, int body)
        {
            var end = CloneLexer.Match(tokens, body);
            if (end < 0)
            {
                return;
            }

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
            declarations.Add(
                new(
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
                )
            );
        }
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            var token = tokens[index];
            if (
                token.Kind is 'i'
                && tokens[index + 1].Text is "("
                && !CloneLexer.Control.Contains(token.Text)
                && !CloneLexer.Keywords.Contains(token.Text)
            )
            {
                if (
                    index > 0
                    && (
                        tokens[index - 1].Text is "." or "?." or "new"
                        || CloneLexer.Declares.Contains(tokens[index - 1].Text)
                    )
                )
                {
                    continue;
                }

                var declaration = index is 0 || language is "ts" || CloneLexer.TypeLike(tokens[index - 1]);
                var close = CloneLexer.Match(tokens, index + 1);
                if (!declaration || close < 0)
                {
                    continue;
                }

                for (var cursor = close + 1; cursor < tokens.Count && cursor < close + 60; cursor++)
                {
                    var current = tokens[cursor];
                    if (current.Kind is 'p' && current.Text is ";" or "=>" or "=" or "}" or ",")
                    {
                        break;
                    }

                    if (
                        current.Kind is 'p'
                        && current.Text is "("
                        && CloneLexer.Match(tokens, cursor) is var end
                        && end > 0
                    )
                    {
                        cursor = end;
                        continue;
                    }
                    if (current.Kind is 'p' && current.Text is "{")
                    {
                        if (language is "cs" || cursor == close + 1 || tokens[close + 1].Text is ":")
                        {
                            Add(index, index + 1, cursor);
                        }

                        break;
                    }
                    if (language is "ts" && cursor == close + 1 && current.Text is not ":")
                    {
                        break;
                    }
                }
                continue;
            }
            if (token.Text is not "=>" || tokens[index + 1].Text is not "{" || language is not "ts")
            {
                continue;
            }

            var before = index - 1;
            var parameters = -1;
            if (before >= 0 && tokens[before].Text is ")")
            {
                var depth = 0;
                for (; before >= 0; before--)
                {
                    if (tokens[before].Text is ")")
                    {
                        depth++;
                    }
                    else if (tokens[before].Text is "(" && --depth is 0)
                    {
                        break;
                    }
                }
                parameters = before--;
            }
            else if (before >= 0 && tokens[before].Kind is 'i')
            {
                parameters = before--;
            }

            if (before >= 0 && tokens[before].Text is "async")
            {
                before--;
            }

            if (before >= 1 && tokens[before].Text is "=" or ":" && tokens[before - 1].Kind is 'i' && parameters >= 0)
            {
                Add(before - 1, parameters, index + 1);
            }
        }
        var locals = declarations
            .Select(declaration =>
                Forms(tokens, declaration.Parameters, declaration.Body, declaration.End, null, language is "ts").Locals
            )
            .ToArray();
        List<CloneFunction> output = [];
        foreach (var declaration in declarations)
        {
            Dictionary<string, string> captured = new(StringComparer.Ordinal);
            foreach (
                var outer in declarations
                    .Select((value, index) => (value, index))
                    .Where(pair =>
                        pair.value != declaration
                        && pair.value.Body < declaration.Body
                        && declaration.End <= pair.value.End
                    )
                    .OrderBy(pair => pair.value.Body)
            )
            {
                foreach (var local in locals[outer.index])
                {
                    captured[local] =
                        outer.value.Function.Name
                        + "@"
                        + outer.value.Function.Line.ToString(CultureInfo.InvariantCulture)
                        + ".";
                }
            }

            var forms = Forms(
                tokens,
                declaration.Parameters,
                declaration.Body,
                declaration.End,
                captured,
                language is "ts"
            );
            output.Add(
                declaration.Function with
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
                }
            );
        }
        return output;
    }

    private static string[] ParameterTypes(List<CodeToken> tokens, string language)
    {
        List<string> output = [];
        List<CodeToken> current = [];
        void Flush()
        {
            if (current.Count is 0)
            {
                return;
            }

            if (language is "ts")
            {
                var colon = current.FindIndex(token => token.Text is ":");
                output.Add(colon < 0 ? "?" : string.Concat(current.Skip(colon + 1).Select(token => token.Text)));
            }
            else
            {
                output.Add(
                    string.Concat(
                        current
                            .Take(current[^1].Kind is 'i' ? current.Count - 1 : current.Count)
                            .Where(token =>
                                token.Kind is not 'i'
                                || token.Text is not ("this" or "ref" or "out" or "in" or "params" or "scoped")
                            )
                            .Select(token => token.Text)
                    )
                );
            }

            current.Clear();
        }
        var depth = 0;
        var attribute = 0;
        var defaultValue = false;
        foreach (var token in tokens)
        {
            switch (token.Text)
            {
                case "(" or "<" or "{":
                    depth++;
                    break;
                case ")" or ">" or "}":
                    depth--;
                    break;
                case "[" when depth is 0 && current.Count is 0 && language is "cs":
                    attribute++;
                    continue;
                case "[":
                    depth++;
                    break;
                case "]" when attribute > 0:
                    attribute--;
                    continue;
                case "]":
                    depth--;
                    break;
            }
            if (attribute > 0)
            {
                continue;
            }

            if (depth is 0 && token.Text is ",")
            {
                Flush();
                defaultValue = false;
            }
            else if (depth is 0 && token.Text is "=")
            {
                defaultValue = true;
            }
            else if (!defaultValue)
            {
                current.Add(token);
            }
        }
        Flush();
        return output.ToArray();
    }
}
