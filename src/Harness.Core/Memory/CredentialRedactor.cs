using System.Text.RegularExpressions;

namespace Harness.Core.Memory;

public static partial class CredentialRedactor
{
    public const string Mark = "[redacted]";

    // Credential pattern word boundaries are ASCII, even when matching case-insensitively.
    // .NET's ECMAScript boundary also treats U+0130 as a word character.
    private const string Boundary = @"(?-i:(?<![A-Za-z0-9_])(?=[A-Za-z0-9_])|(?<=[A-Za-z0-9_])(?![A-Za-z0-9_]))";
    private const RegexOptions Options = RegexOptions.CultureInvariant;

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        (PrivateKey, Mark),
        (UrlCredential, "${1}" + Mark + "@"),
        (AuthorizationToken, "${1} " + Mark),
        (VendorToken, Mark),
        (JsonWebToken, Mark),
        (SecretAssignment, "${1}" + Mark),
    ];

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", Options, 1000)]
    private static partial Regex PrivateKey { get; }

    [GeneratedRegex(@"([a-zA-Z][a-zA-Z0-9+.-]*://[^ \t\r\n\f:/@]+:)[^ \t\r\n\f/@]+@", Options, 1000)]
    private static partial Regex UrlCredential { get; }

    [GeneratedRegex(
        Boundary + @"(bearer|basic|token)[ \t\r\n\f]+[A-Za-z0-9\-._~+/]{16,}=*",
        Options | RegexOptions.IgnoreCase,
        1000
    )]
    private static partial Regex AuthorizationToken { get; }

    [GeneratedRegex(
        Boundary
            + @"(?:sk-(?:ant-|proj-)?[A-Za-z0-9_\-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|glpat-[A-Za-z0-9_\-]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{35}|npm_[A-Za-z0-9]{36})"
            + Boundary,
        Options,
        1000
    )]
    private static partial Regex VendorToken { get; }

    [GeneratedRegex(Boundary + @"eyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", Options, 1000)]
    private static partial Regex JsonWebToken { get; }

    [GeneratedRegex(
        Boundary
            + "([A-Za-z0-9_\\-]*(?:password|passwd|pwd|secret|token|api[_\\-]?key|access[_\\-]?key|private[_\\-]?key|client[_\\-]?secret)[A-Za-z0-9_\\-]*[\"']?[ \\t\\r\\n\\f]*[:=][ \\t\\r\\n\\f]*)(\"[^\"]*\"|'[^']*'|[^ \\t\\r\\n\\f,;&\"']+)",
        Options | RegexOptions.IgnoreCase,
        1000
    )]
    private static partial Regex SecretAssignment { get; }

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
}
