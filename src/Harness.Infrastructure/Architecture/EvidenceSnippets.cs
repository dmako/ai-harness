using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture;

public static partial class EvidenceSnippets
{
    [GeneratedRegex(
        @"^(?:([^\s/:]+): )?([^\s:()]+?)(?::(\d+))?(?: \(([^()]+)\))?(?:: .*)?$",
        EcosystemReader.RxOptions,
        1000
    )]
    private static partial Regex Site { get; }

    public static EvidenceSite? Parse(string evidence)
    {
        if (
            evidence.Contains(" → ", StringComparison.Ordinal)
            || evidence.StartsWith("memory ", StringComparison.Ordinal)
        )
        {
            return null;
        }

        var match = Site.Match(evidence.Trim());
        if (!match.Success || match.Groups[2].Value.IndexOfAny(['.', '/']) < 0)
        {
            return null;
        }

        int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var line);
        return new(match.Groups[2].Value, line, Empty(match.Groups[4].Value), Empty(match.Groups[1].Value));
    }

    /// <summary>The clone reader can supply a containing function; otherwise show bounded line context.</summary>
    public static EvidenceSnippet Read(
        AnalysisRepository repository,
        EvidenceSite site,
        int span = 0,
        int max = 0,
        Func<EvidenceSite, EvidenceFunction?>? locate = null
    )
    {
        max = max is <= 0 or > 120 ? 120 : max;
        var file = repository.Resolve(site.Path);
        if (Directory.Exists(file))
        {
            return Folder(site, file, max);
        }

        var lines = TextLines(repository, site, file);
        var function =
            site.Line > 0
                ? (locate ?? new Clones.CloneExtractor(new(repository.Root, [site.Path])).Locate).Invoke(site)
                : null;
        var (start, end, truncated) = Window(site, lines.Length, function, max);
        return new(site)
        {
            Start = start,
            Lines = lines.Skip(start - 1).Take(Math.Max(0, end - start + 1)).ToArray(),
            Focus =
                span > 1 && site.Line > 0
                    ? [site.Line, (int)Math.Min((long)site.Line + span - 1, lines.Length)]
                    : [0, 0],
            Function = function?.Name,
            Truncated = truncated,
        };
    }

    /// <summary>A folder's visible entries, folders first.</summary>
    private static EvidenceSnippet Folder(EvidenceSite site, string folder, int max)
    {
        var entries = new DirectoryInfo(folder)
            .EnumerateFileSystemInfos()
            .Where(entry => !entry.Name.StartsWith('.'))
            .OrderBy(entry => entry is DirectoryInfo ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => entry.Name + (entry is DirectoryInfo ? "/" : ""))
            .ToArray();
        return new(site)
        {
            Folder = true,
            Lines = entries.Length is 0 ? null : entries.Take(max).ToArray(),
            Truncated = entries.Length > max,
        };
    }

    /// <summary>The file's lines; a missing, large, unreadable or binary file throws.</summary>
    private static string[] TextLines(AnalysisRepository repository, EvidenceSite site, string file)
    {
        if (!File.Exists(file))
        {
            throw new FileNotFoundException("no place in a file", site.Path);
        }

        if (new FileInfo(file).Length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException(site.Path + ": too large or not text");
        }

        var text = repository.Read(site.Path);
        if (
            repository.Notes.Any(note => note.StartsWith(site.Path + ":", StringComparison.Ordinal))
            || Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024
            || text.AsSpan(0, Math.Min(text.Length, 8000)).Contains('\0')
        )
        {
            throw new InvalidDataException(site.Path + ": too large or not text");
        }

        return text.TrimEnd('\n').Split('\n');
    }

    /// <summary>
    /// The lines shown: the containing function, else five lines around the site (the first 20 without one),
    /// cut to max lines that keep the site in view.
    /// </summary>
    private static (int Start, int End, bool Truncated) Window(
        EvidenceSite site,
        int count,
        EvidenceFunction? function,
        int max
    )
    {
        var start = site.Line > 0 ? Math.Max(1, site.Line - 5) : 1;
        var end = site.Line > 0 ? (int)Math.Min(count, (long)site.Line + 5) : Math.Min(count, 20);
        if (function is not null && function.Start > 0 && function.Start <= site.Line && site.Line <= function.End)
        {
            start = function.Start;
            end = Math.Min(function.End, count);
        }
        var truncated = end - start + 1 > max;
        if (truncated)
        {
            if (site.Line > (long)start + max - 1)
            {
                start = Math.Max(1, site.Line - 5);
            }

            end = (int)Math.Min(count, (long)start + max - 1);
        }
        return (Math.Clamp(start, 1, count + 1), end, truncated);
    }

    private static string? Empty(string text) => text.Length is 0 ? null : text;
}
