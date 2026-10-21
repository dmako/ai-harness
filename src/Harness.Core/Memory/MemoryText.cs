using System.Security.Cryptography;
using System.Text;

namespace Harness.Core.Memory;

public static class MemoryText
{
    public const int MaxContentBytes = 4000;
    public const double SimilarThreshold = 0.5;

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "goal", "constraint", "decision", "fact", "open_question", "warning", "convention",
    };

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "but", "not", "use", "with", "this", "that", "from", "into", "all", "any",
        "can", "has", "have", "was", "were", "will", "should", "must", "our", "its", "than", "then", "them", "they",
        "when", "which", "while", "only", "also", "per", "via",
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ParseType(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var type = Lower(text.Trim());
        return Types.Contains(type) ? type : throw new ArgumentException("invalid memory type " + Quote(text) + " (want one of: goal, constraint, decision, fact, open_question, warning, convention)");
    }

    public static MemoryInput Validate(MemoryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var content = input.Content.Trim();
        if (content.Length is 0)
            throw new ArgumentException("content must not be empty");
        var length = StrictUtf8.GetByteCount(content);
        if (length > MaxContentBytes)
            throw new ArgumentException($"content is {length} bytes (max {MaxContentBytes}): store one short statement, not a transcript or document");
        // Only the redacted form reaches diagnostics; never echo the rejected credential.
        if (CredentialRedactor.HasCredential(content))
            throw new ArgumentException("content looks like it holds a secret (" + CredentialRedactor.Redact(content).Trim() + "): harness never stores credentials; state the fact without it");
        var scope = (input.Scope is null or { Kind: "" } ? MemoryScope.Project : input.Scope).Normalize();
        if (scope.Wider)
            throw new ArgumentException($"{scope} memories are kept in that level's own store, not a project's");
        var source = input.Source;
        var sourceType = source.Type.Length is 0 ? "agent" : source.Type;
        if (sourceType is not ("agent" or "user" or "import" or "system"))
            throw new ArgumentException("invalid source type " + Quote(sourceType) + " (want agent, user, import or system)");
        var by = string.IsNullOrWhiteSpace(source.By) ? "unknown" : source.By;
        var reference = string.IsNullOrWhiteSpace(source.Ref) ? by : source.Ref;
        if (input.Confidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1))
            throw new ArgumentException("confidence must be between 0 and 1, got " + confidence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (input.ValidFrom is { } from && input.ValidUntil is { } until && until <= from)
            throw new ArgumentException("valid_until must be after valid_from");
        return input with { Content = content, Type = ParseType(input.Type), Scope = scope, Source = new MemorySource(sourceType, reference, by) };
    }

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Lower(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).TrimEnd('.', '!', ';', ',', ' ');
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(Normalize(text))));

    private static readonly System.Text.Json.JsonSerializerOptions QuoteOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Quote(string text) => System.Text.Json.JsonSerializer.Serialize(text, QuoteOptions);

    public static string FullTextQuery(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> terms = [];
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '_' or >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or > 127)
                word.Append(rune.ToString());
            else
                Flush();
        }

        Flush();
        return string.Join(" OR ", terms);

        void Flush()
        {
            if (word.Length is 0)
                return;
            terms.Add("\"" + word + "\"");
            word.Clear();
        }
    }

    public static double Similarity(string first, string second)
    {
        var left = Words(first);
        var right = Words(second);
        if (left.Count is 0 || right.Count is 0)
            return 0;
        var intersection = left.Count(right.Contains);
        return (double)intersection / (left.Count + right.Count - intersection);
    }

    private static HashSet<string> Words(string text)
    {
        HashSet<string> words = new(StringComparer.Ordinal);
        var word = new StringBuilder();
        foreach (var rune in Lower(text).EnumerateRunes())
        {
            if (GoUnicode.IsLetterOrDigit(rune))
                word.Append(rune.ToString());
            else
                Flush();
        }

        Flush();
        return words;

        void Flush()
        {
            var token = word.ToString();
            word.Clear();
            if (StrictUtf8.GetByteCount(token) < 3 || Stopwords.Contains(token))
                return;
            foreach (var suffix in new[] { "ing", "ed", "es", "s" })
            {
                if (StrictUtf8.GetByteCount(token) > suffix.Length + 3 && token.EndsWith(suffix, StringComparison.Ordinal))
                {
                    token = token[..^suffix.Length];
                    break;
                }
            }

            words.Add(token);
        }
    }

    private static string Lower(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            result.Append(GoUnicode.ToLower(rune).ToString());
        return result.ToString();
    }
}
