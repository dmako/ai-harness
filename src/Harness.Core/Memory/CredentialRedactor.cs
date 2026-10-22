using System.Text.RegularExpressions;

namespace Harness.Core.Memory;

public static class CredentialRedactor
{
    public const string Mark = "[redacted]";

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        Rule(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", Mark),
        Rule(@"([a-zA-Z][a-zA-Z0-9+.-]*://[^ \t\r\n\f:/@]+:)[^ \t\r\n\f/@]+@", "${1}" + Mark + "@"),
        Rule(@"\b(bearer|basic|token)[ \t\r\n\f]+[A-Za-z0-9\-._~+/]{16,}=*", "${1} " + Mark, ignoreCase: true),
        Rule(
            @"\b(?:sk-(?:ant-|proj-)?[A-Za-z0-9_\-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|glpat-[A-Za-z0-9_\-]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{35}|npm_[A-Za-z0-9]{36})\b",
            Mark
        ),
        Rule(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", Mark),
        Rule(
            "\\b([A-Za-z0-9_\\-]*(?:password|passwd|pwd|secret|token|api[_\\-]?key|access[_\\-]?key|private[_\\-]?key|client[_\\-]?secret)[A-Za-z0-9_\\-]*[\"']?[ \\t\\r\\n\\f]*[:=][ \\t\\r\\n\\f]*)(\"[^\"]*\"|'[^']*'|[^ \\t\\r\\n\\f,;&\"']+)",
            "${1}" + Mark,
            ignoreCase: true
        ),
    ];

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var (pattern, replacement) in Rules)
        {
            text = pattern.Replace(text, replacement);
        }

        return text;
    }

    public static bool HasCredential(string text) => Redact(text) != text;

    private static (Regex, string) Rule(string pattern, string replacement, bool ignoreCase = false)
    {
        // Go's regexp word boundaries are ASCII, even when matching case-insensitively.
        // .NET's ECMAScript boundary also treats U+0130 as a word character.
        pattern = pattern.Replace(
            @"\b",
            @"(?-i:(?<![A-Za-z0-9_])(?=[A-Za-z0-9_])|(?<=[A-Za-z0-9_])(?![A-Za-z0-9_]))",
            StringComparison.Ordinal
        );
        return (
            new Regex(
                pattern,
                RegexOptions.CultureInvariant
                    | RegexOptions.Compiled
                    | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
                TimeSpan.FromSeconds(1)
            ),
            replacement
        );
    }
}
