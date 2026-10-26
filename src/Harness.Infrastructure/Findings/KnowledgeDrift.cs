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

    private const string MissingCodeName = "quotes a code name that appears nowhere in the repository now";

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

        var scan = new DriftScan(repository);
        List<ArchitectureFinding> result = [];
        foreach (var m in memories)
        {
            token.ThrowIfCancellationRequested();
            result.AddRange(scan.Check(m));
        }
        return result;
    }

    /// <summary>
    /// What one repository holds: its files, every folder above them, and (read once, when first needed)
    /// their text.
    /// </summary>
    private sealed class DriftScan
    {
        private readonly AnalysisRepository _repository;
        private readonly HashSet<string> _files;
        private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _top;
        private List<string>? _corpus;

        public DriftScan(AnalysisRepository repository)
        {
            _repository = repository;
            _files = repository.Files.ToHashSet(StringComparer.Ordinal);
            _top = _files.Select(f => f.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
            foreach (var f in _files)
            {
                for (
                    var dir = Path.GetDirectoryName(f)?.Replace('\\', '/');
                    !string.IsNullOrEmpty(dir) && _dirs.Add(dir);
                    dir = Path.GetDirectoryName(dir)?.Replace('\\', '/')
                )
                { /* Collect every ancestor once. */
                }
            }
        }

        /// <summary>
        /// A memory's stale path scope, then the paths and backticked code names it mentions that are gone.
        /// </summary>
        public List<ArchitectureFinding> Check(MemoryRecord m)
        {
            List<ArchitectureFinding> found = [];
            if (m.Scope.Kind is "path" && m.Scope.Value.Length > 0 && !Exists(m.Scope.Value))
            {
                found.Add(
                    Stale(m, m.Scope.Value, "scoped to a path no file of the repository is under now", "fact", 0)
                );
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (Match match in PathLike.Matches(m.Content))
            {
                if (MentionedPath(m, match.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')'), seen) is { } finding)
                {
                    found.Add(finding);
                }
            }
            foreach (Match match in Backticked.Matches(m.Content))
            {
                if (MentionedName(m, BacktickedName(match), seen) is { } finding)
                {
                    found.Add(finding);
                }
            }
            return found;
        }

        /// <summary>
        /// A path that is gone; or, when only its last ".Name" is not a path, that name if it appears
        /// nowhere.
        /// </summary>
        private ArchitectureFinding? MentionedPath(MemoryRecord m, string p, HashSet<string> seen)
        {
            if (seen.Contains(p) || !PathCandidate(p))
            {
                return null;
            }

            seen.Add(p);
            if (Exists(p))
            {
                return null;
            }

            var dot = p.LastIndexOf('.');
            if (dot > p.LastIndexOf('/') && Exists(p[..dot]) && CodeName.IsMatch(p[(dot + 1)..]))
            {
                return Contains(p[(dot + 1)..]) ? null : Stale(m, p, MissingCodeName, "heuristic", .7);
            }

            return Stale(m, p, "names a path that is not in the repository now", "fact", 0);
        }

        /// <summary>
        /// Not only dots, not a URL, and under a top-level folder of the repository or a source file.
        /// </summary>
        private bool PathCandidate(string p) =>
            !p.Split('/').All(s => s.StartsWith('.'))
            && !p.Contains("://", StringComparison.Ordinal)
            && (_top.Contains(p.Split('/')[0]) || SourceExtensions.Contains(Path.GetExtension(p)));

        /// <summary>A code name with a part of three or more characters that appears nowhere.</summary>
        private ArchitectureFinding? MentionedName(MemoryRecord m, string name, HashSet<string> seen)
        {
            if (seen.Contains(name) || !NameCandidate(name))
            {
                return null;
            }

            seen.Add(name);
            return name.Split('.').Any(part => part.Length >= 3 && !Contains(part))
                ? Stale(m, name, MissingCodeName, "heuristic", .7)
                : null;
        }

        private bool Exists(string p)
        {
            p = p.TrimEnd('/');
            if (p.StartsWith("./", StringComparison.Ordinal))
            {
                p = p[2..];
            }

            return _files.Contains(p) || _dirs.Contains(p);
        }

        private bool Contains(string name)
        {
            _corpus ??= _files
                .Where(f =>
                    !BinaryExtensions.Contains(Path.GetExtension(f))
                    && !f.Contains("node_modules/", StringComparison.Ordinal)
                )
                .Select(_repository.Read)
                .OfType<string>()
                .Where(text => Encoding.UTF8.GetByteCount(text) < 2 << 20)
                .ToList();
            return _corpus.Any(text => text.Contains(name, StringComparison.Ordinal));
        }

        private static string BacktickedName(Match match)
        {
            var name = match.Groups[1].Value.Trim();
            return name.EndsWith("()", StringComparison.Ordinal) ? name[..^2] : name;
        }

        /// <summary>
        /// At least four characters, shaped like a code name, and qualified, snake_case or capitalized.
        /// </summary>
        private static bool NameCandidate(string name) =>
            name.Length >= 4
            && CodeName.IsMatch(name)
            && (
                name.Contains('.', StringComparison.Ordinal)
                || name.Contains('_', StringComparison.Ordinal)
                || name.Any(char.IsUpper)
            );

        private static ArchitectureFinding Stale(
            MemoryRecord m,
            string what,
            string why,
            string basis,
            double confidence
        ) =>
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
                Why = why + ": \"" + Excerpt(m.Content) + "\"",
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
            };

        /// <summary>The content on one line, clipped to 160 UTF-8 bytes at a valid scalar boundary.</summary>
        private static string Excerpt(string text)
        {
            var content = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (Encoding.UTF8.GetByteCount(content) <= 160)
            {
                return content;
            }

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
            return clipped + "…";
        }
    }
}
