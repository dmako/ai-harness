using System.Text;
using System.Text.RegularExpressions;
using Harness.Core.Architecture;
using Harness.Core.Memory;
using Harness.Infrastructure.Architecture;

namespace Harness.Infrastructure.Findings;

public static class KnowledgeDrift
{
    private static readonly Regex Backticked = new(
        "`([^`\\n]{2,120})`",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex PathLike = new(
        "(?:^|[\\s(\\[“\"'])((?:[\\w.-]+/)+[\\w.-]+)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly Regex CodeName = new(
        @"\A[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*(?:\(\))?\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    private static readonly HashSet<string> SourceExtensions = new(
        ".cs .go .ts .tsx .js .jsx .py .java .kt .rs .dart .csproj .sln .slnx .proto .sql .md .yaml .yml .toml .props .razor .vue".Split(
            ' '
        ),
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly HashSet<string> BinaryExtensions = new(
        ".png .jpg .jpeg .gif .ico .pdf .zip .gz .db .dll .exe .woff .woff2 .ttf .bin .lock".Split(' '),
        StringComparer.OrdinalIgnoreCase
    );

    public static IReadOnlyList<ArchitectureFinding> Read(
        AnalysisRepository repository,
        IReadOnlyList<MemoryRecord> memories,
        CancellationToken token = default
    )
    {
        if (memories.Count is 0 || repository.Files.Count is 0)
        {
            return [];
        }

        var files = repository.Files.ToHashSet(StringComparer.Ordinal);
        HashSet<string> dirs = new(StringComparer.Ordinal);
        var top = files.Select(f => f.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var f in files)
        {
            for (
                var dir = Path.GetDirectoryName(f)?.Replace('\\', '/');
                !string.IsNullOrEmpty(dir) && dirs.Add(dir);
                dir = Path.GetDirectoryName(dir)?.Replace('\\', '/')
            )
            { /* Collect every ancestor once. */
            }
        }

        bool Exists(string p)
        {
            p = p.TrimEnd('/');
            if (p.StartsWith("./", StringComparison.Ordinal))
            {
                p = p[2..];
            }

            return files.Contains(p) || dirs.Contains(p);
        }
        List<string>? corpus = null;
        bool Contains(string name)
        {
            corpus ??= files
                .Where(f =>
                    !BinaryExtensions.Contains(Path.GetExtension(f))
                    && !f.Contains("node_modules/", StringComparison.Ordinal)
                )
                .Select(repository.Read)
                .OfType<string>()
                .Where(text => Encoding.UTF8.GetByteCount(text) < 2 << 20)
                .ToList();
            return corpus.Any(text => text.Contains(name, StringComparison.Ordinal));
        }
        List<ArchitectureFinding> result = [];
        void Add(MemoryRecord m, string what, string why, string basis, double confidence)
        {
            var content = string.Join(" ", m.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            // Go clips UTF-8 bytes; preserve a valid scalar boundary in the diagnostic excerpt.
            if (Encoding.UTF8.GetByteCount(content) > 160)
            {
                var clipped = new StringBuilder();
                var size = 0;
                foreach (var rune in content.EnumerateRunes())
                {
                    if (size + rune.Utf8SequenceLength > 160)
                    {
                        break;
                    }

                    clipped.Append(rune);
                    size += rune.Utf8SequenceLength;
                }
                content = clipped + "…";
            }
            result.Add(
                new()
                {
                    Kind = "stale-knowledge",
                    Level = "info",
                    Title =
                        "A "
                        + m.Type
                        + (why.StartsWith("scoped", StringComparison.Ordinal) ? " is scoped to " : " names ")
                        + what
                        + ", which is gone",
                    Why = why + ": \"" + content + "\"",
                    Fix =
                        "If the code moved or was replaced, change the "
                        + m.Type
                        + " to say so (or retire it if it no longer holds); if it should still be there, the code drifted.",
                    Key = ArchitectureAnalyzer.Key("stale-knowledge", m.Id, what),
                    Evidence = ["memory " + m.Id],
                    Basis = basis,
                    Confidence = confidence,
                    Source = ArchitectureAnalyzer.Harness("knowledge"),
                    Rule = new(m.Id, what, m.Scope.ToString(), m.Type),
                }
            );
        }
        foreach (var m in memories)
        {
            token.ThrowIfCancellationRequested();
            if (m.Scope.Kind is "path" && m.Scope.Value.Length > 0 && !Exists(m.Scope.Value))
            {
                Add(m, m.Scope.Value, "scoped to a path no file of the repository is under now", "fact", 0);
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (Match match in PathLike.Matches(m.Content))
            {
                var p = match.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')');
                if (
                    seen.Contains(p)
                    || p.Split('/').All(s => s.StartsWith('.'))
                    || p.Contains("://", StringComparison.Ordinal)
                    || !top.Contains(p.Split('/')[0]) && !SourceExtensions.Contains(Path.GetExtension(p))
                )
                {
                    continue;
                }

                seen.Add(p);
                if (Exists(p))
                {
                    continue;
                }

                var dot = p.LastIndexOf('.');
                if (dot > p.LastIndexOf('/') && Exists(p[..dot]) && CodeName.IsMatch(p[(dot + 1)..]))
                {
                    if (!Contains(p[(dot + 1)..]))
                    {
                        Add(m, p, "quotes a code name that appears nowhere in the repository now", "heuristic", .7);
                    }

                    continue;
                }
                Add(m, p, "names a path that is not in the repository now", "fact", 0);
            }
            foreach (Match match in Backticked.Matches(m.Content))
            {
                var name = match.Groups[1].Value.Trim();
                if (name.EndsWith("()", StringComparison.Ordinal))
                {
                    name = name[..^2];
                }

                if (
                    seen.Contains(name)
                    || name.Length < 4
                    || !CodeName.IsMatch(name)
                    || !name.Contains('.', StringComparison.Ordinal)
                        && !name.Contains('_', StringComparison.Ordinal)
                        && !name.Any(char.IsUpper)
                )
                {
                    continue;
                }

                seen.Add(name);
                if (name.Split('.').Any(part => part.Length >= 3 && !Contains(part)))
                {
                    Add(m, name, "quotes a code name that appears nowhere in the repository now", "heuristic", .7);
                }
            }
        }
        return result;
    }
}
