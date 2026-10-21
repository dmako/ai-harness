using System.Text.RegularExpressions;
using Harness.Core.Retrieval;
using Harness.Core.Workspaces;

namespace Harness.Infrastructure.Architecture;

public static class DeclarationReader
{
    private static readonly Regex CSharp = EcosystemReader.Rx(@"^\s*public\s+(?:(?:sealed|static|abstract|partial|readonly|ref)\s+)*(record\s+struct|record\s+class|class|interface|record|struct|enum)\s+([A-Za-z_]\w*)");
    private static readonly Regex Go = EcosystemReader.Rx(@"^(?:type\s+([A-Z]\w*)|func\s+([A-Z]\w*)\s*[\[(])");
    private static readonly Regex TypeScript = EcosystemReader.Rx(@"^export\s+(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(class|interface|function|type|enum|const)\s+([A-Za-z_$][\w$]*)");
    private static readonly Regex CSharpBases = EcosystemReader.Rx(@"^\s*(?:<[^>{]*>)?\s*(?:\([^){]*\))?\s*:\s*([^{]+?)\s*(?:\bwhere\b|\{|$)");
    private static readonly Regex TypeScriptBases = EcosystemReader.Rx(@"\b(?:extends|implements)\s+([^{]+)");
    public static IReadOnlyList<DeclaredType> Read(AnalysisRepository repository)
    {
        List<DeclaredType> result = [];
        foreach (var file in repository.Files)
        {
            var regex = Path.GetExtension(file).ToUpperInvariant() switch { ".CS" => CSharp, ".GO" => Go, ".TS" or ".TSX" or ".JS" or ".MJS" => TypeScript, _ => null };
            if (regex is null || TaskParsing.IsTestFile(file) || EcosystemReader.Vendored(file) || file.EndsWith(".d.ts", StringComparison.Ordinal))
                continue;
            var lines = AnalysisRepository.Lines(repository.Read(file));
            for (var index = 0; index < lines.Length; index++)
            {
                var match = regex.Match(lines[index]);
                if (!match.Success)
                    continue;
                var kind = string.Join(' ', match.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                var name = match.Groups[2].Value;
                List<string> bases = [];
                if (regex == Go)
                {
                    kind = match.Groups[1].Length > 0 ? "type" : "func";
                    name = match.Groups[1].Length > 0 ? match.Groups[1].Value : name;
                }
                else
                {
                    var rest = lines[index][match.Length..];
                    if (regex == CSharp && CSharpBases.Match(rest) is { Success: true } cs)
                        bases.AddRange(BaseNames(cs.Groups[1].Value));
                    if (regex == TypeScript)
                        bases.AddRange(TypeScriptBases.Matches(rest).SelectMany(ts => BaseNames(ts.Groups[1].Value.Replace("implements", ",", StringComparison.Ordinal).Replace("extends", ",", StringComparison.Ordinal))));
                }
                result.Add(new(name, kind, file, index + 1, bases.Count is 0 ? null : bases));
            }
        }
        return result;
    }
    private static List<string> BaseNames(string list)
    {
        List<string> result = [];
        var depth = 0;
        var start = 0;
        for (var index = 0; index < list.Length; index++)
            switch (list[index])
            {
                case '<' or '(':
                    depth++;
                    break;
                case '>' or ')':
                    depth--;
                    break;
                case ',' when depth is 0:
                    Add(list[start..index]);
                    start = index + 1;
                    break;
            }
        Add(list[start..]);
        return result;
        void Add(string text)
        {
            text = text.Trim();
            var end = text.IndexOfAny(['<', '(', ' ']);
            if (end >= 0)
                text = text[..end];
            text = text[(text.LastIndexOf('.') + 1)..];
            if (text.Length > 0 && text is not ("implements" or "extends"))
                result.Add(text);
        }
    }
}
