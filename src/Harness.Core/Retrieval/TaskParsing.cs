using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Harness.Core.Retrieval;

[SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "The pinned retrieval contract emits lowercase content words and naming variants.")]
public static class TaskParsing
{
    private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly Regex Issue = new(@"\A[A-Z][A-Z0-9]*-[0-9]", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly Regex Abbreviation = new(@"\A[a-z]{1,2}(\.[a-z]{1,2})+\z", RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Word = new(@"[A-Za-z][A-Za-z0-9]+", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly Regex TestWord = new(@"(?:\A|[^A-Za-z0-9_])(?:tests?|testing|spec)(?:\z|[^A-Za-z0-9_])|_test(?:\z|[^A-Za-z0-9_])", RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> Stopwords = new(("this that these those with from into onto over under about when while then than " +
        "there their they them what which where have been being does done doing should would could will shall must " +
        "instead because before after also only just very some other another each every both either neither such " +
        "make makes made uses used using change changes changing work working task").Split(' '), StringComparer.Ordinal);

    public static IReadOnlyList<string> Symbols(string text)
    {
        List<string> code = [], words = [];
        foreach (Match match in Identifier.Matches(text))
        {
            var word = match.Value;
            if (word.Length < 3 || NotAName(text, match))
                continue;
            if (LooksLikeCode(word))
            { if (!code.Contains(word, StringComparer.Ordinal)) code.Add(word); }
            else if (NamedInProse(text, match) && !words.Contains(word, StringComparer.Ordinal))
                words.Add(word);
        }
        return code.Take(5).Concat(words.Take(Math.Max(0, 5 - code.Count))).ToArray();
    }

    public static IReadOnlyList<string> Phrases(string text)
    {
        List<string> phrases = [];
        var previous = "";
        var previousEnd = -1;
        foreach (Match match in Identifier.Matches(text))
        {
            var word = match.Value;
            var lower = word.ToLowerInvariant();
            if (lower.Length < 4 || Stopwords.Contains(lower) || LooksLikeCode(word) || !Word.IsMatch(word) || NotAName(text, match) || NamedInProse(text, match))
            { previous = ""; continue; }
            if (previous.Length > 0 && string.IsNullOrWhiteSpace(text[previousEnd..match.Index]))
            {
                var phrase = Title(previous) + Title(Singular(lower));
                if (!phrases.Contains(phrase, StringComparer.Ordinal))
                {
                    phrases.Add(phrase);
                    if (phrases.Count is 3)
                        break;
                }
            }
            previous = lower;
            previousEnd = match.Index + match.Length;
        }
        return phrases;
    }

    public static string ContentWords(string text) => string.Join(' ', Word.Matches(text).Select(match => match.Value.ToLowerInvariant()).Where(word => word.Length >= 4 && !Stopwords.Contains(word)).Distinct(StringComparer.Ordinal));
    public static IReadOnlyList<string> NamingVariants(string name)
    {
        if (!name.Contains('_', StringComparison.Ordinal) || name.Contains('.', StringComparison.Ordinal) || name == name.ToUpperInvariant())
            return [];
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return [];
        var camel = parts[0].ToLowerInvariant() + string.Concat(parts.Skip(1).Select(part => Title(part.ToLowerInvariant())));
        return [camel, Title(camel)];
    }
    public static string HeadNoun(string phrase)
    {
        for (var i = phrase.Length - 1; i > 0; i--)
        {
            if (char.IsAsciiLetterUpper(phrase[i]))
                return phrase[i..].ToLowerInvariant();
        }
        return phrase.ToLowerInvariant();
    }
    public static string LastName(string name)
    {
        name = name.Split('(')[0];
        return name[(name.LastIndexOfAny(['.', ':']) + 1)..];
    }
    internal static string Qualifier(string name)
    {
        name = name.Split('(')[0];
        var split = name.LastIndexOfAny(['.', ':']);
        if (split <= 0)
            return "";
        var qualifier = name[..split].TrimEnd(':');
        return qualifier[(qualifier.LastIndexOfAny(['.', ':']) + 1)..];
    }
    internal static bool InQualifier(Candidate candidate, string qualifier)
    {
        var q = qualifier.ToLowerInvariant();
        return candidate.QualifiedName.ToLowerInvariant().Split(['.', ':', '/', '(', ')', '*', ' '], StringSplitOptions.RemoveEmptyEntries).Contains(q, StringComparer.Ordinal) ||
            candidate.Path.ToLowerInvariant().Split('/').Any(segment => segment[..(segment.Length - RetrievalRanker.Extension(segment).Length)] == q);
    }
    public static bool TaskAboutTests(string text) => TestWord.IsMatch(text);
    public static bool IsTestFile(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith("_test.go", StringComparison.Ordinal) || name.EndsWith("Tests.cs", StringComparison.Ordinal) || name.EndsWith("Test.cs", StringComparison.Ordinal) || name.EndsWith("Tests.csproj", StringComparison.Ordinal) || name.EndsWith("Test.csproj", StringComparison.Ordinal) || lower.Contains(".test.", StringComparison.Ordinal) || lower.Contains(".spec.", StringComparison.Ordinal) || lower.StartsWith("test_", StringComparison.Ordinal) && lower.EndsWith(".py", StringComparison.Ordinal) || lower.EndsWith("_test.py", StringComparison.Ordinal))
            return true;
        var slash = path.LastIndexOf('/');
        return slash >= 0 && path[..slash].ToLowerInvariant().Split('/').Any(segment => segment is "test" or "__tests__" || segment.EndsWith("tests", StringComparison.Ordinal) || segment.Contains(".tests.", StringComparison.Ordinal) || segment.EndsWith(".test", StringComparison.Ordinal) || segment.EndsWith("-test", StringComparison.Ordinal) || segment.EndsWith("_test", StringComparison.Ordinal));
    }
    private static bool NotAName(string text, Match match) => Issue.IsMatch(text[match.Index..]) || Abbreviation.IsMatch(match.Value) || !match.Value.Contains('.', StringComparison.Ordinal) && !match.Value.Contains('_', StringComparison.Ordinal) && match.Value == match.Value.ToUpperInvariant();
    private static bool NamedInProse(string text, Match match)
    {
        if (!char.IsAsciiLetterUpper(match.Value[0]) || LooksLikeCode(match.Value))
            return false;
        var before = text[..match.Index].TrimEnd(' ', '\t');
        return before.Length > 0 && !".!?\n\"'`(*-#>".Contains(before[^1], StringComparison.Ordinal);
    }
    private static bool LooksLikeCode(string word) => word.IndexOfAny(['.', '_']) >= 0 || word.Skip(1).Any(char.IsAsciiLetterUpper);
    private static string Singular(string word) => word.Length > 4 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && !word.EndsWith("us", StringComparison.Ordinal) && !word.EndsWith("is", StringComparison.Ordinal) ? word[..^1] : word;
    private static string Title(string word) => char.ToUpperInvariant(word[0]) + word[1..];
}
