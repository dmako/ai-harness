using Harness.Core.Architecture;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Harness.Infrastructure.Git;

namespace Harness.Infrastructure.Architecture;

public static class ChangeCoupling
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(180);
    public const int MinTogether = 3;
    public const double MinShare = 0.5;
    public const int MinHistory = 5;

    public static bool Usual(ChangePartner partner) =>
        partner.Together >= MinTogether
        && partner.Share >= MinShare
        && partner.Together / partner.Share >= MinHistory - 0.5;

    /// <summary>The window's commits, read once, from which partners are counted.</summary>
    public static async Task<ChangeHistory> HistoryAsync(
        GitRepository repository,
        CancellationToken token,
        TimeProvider? clock = null
    ) =>
        new(
            await repository
                .ChurnAsync((clock ?? TimeProvider.System).GetUtcNow() - Window, token)
                .ConfigureAwait(false)
        );

    /// <summary>
    /// A file's partners: counted from the history when there is one, with a configured arch.change_coupling
    /// provider's answer added (the higher together count of a path wins).
    /// </summary>
    public static async Task<ChangePartners> PartnersAsync(
        IProviderRegistry? registry,
        string file,
        int limit,
        CancellationToken token,
        ChangeHistory? history = null
    )
    {
        Dictionary<string, ChangePartner> byPath = new(StringComparer.Ordinal);
        foreach (var partner in history?.Partners(file, 0) ?? [])
        {
            byPath[partner.Path] = partner;
        }

        List<string> errors = [];
        foreach (
            var result in registry is null
                ? []
                : await registry
                    .CallAsync(new(Capabilities.ArchChangeCoupling, file, Limit: limit), cancellationToken: token)
                    .ConfigureAwait(false)
        )
        {
            if (result.Error is not null)
            {
                errors.Add(result.Error.Message.Split('\n')[0]);
                continue;
            }
            foreach (var item in result.Items.Where(item => item.Path is { Length: > 0 } && item.Path != file))
            {
                var partner = new ChangePartner(item.Path!, item.Count, item.Confidence, item.Provider, item.Tool);
                if (!byPath.TryGetValue(partner.Path, out var previous) || partner.Together > previous.Together)
                {
                    byPath[partner.Path] = partner;
                }
            }
        }
        return new(Ranked(byPath.Values, limit), errors);
    }

    /// <summary>The code files changed most in the window, each with the files it changes together with.</summary>
    public static async Task<ChangeHotspots> HotspotsAsync(
        GitRepository repository,
        int limit,
        int partners,
        CancellationToken token,
        TimeProvider? clock = null
    ) =>
        Hotspots(
            await HistoryAsync(repository, token, clock).ConfigureAwait(false),
            repository.Root,
            limit,
            partners,
            token
        );

    public static ChangeHotspots Hotspots(
        ChangeHistory history,
        string root,
        int limit,
        int partners,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(history);
        var files = new AnalysisRepository(root, [], token);
        var output = history
            .Counts.Where(pair =>
                Code(pair.Key)
                && !TaskParsing.IsTestFile(pair.Key)
                && !Generated(pair.Key)
                && pair.Value > 1
                && files.Exists(pair.Key)
            )
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(Math.Max(0, limit))
            .Select(pair =>
            {
                var together =
                    partners > 0
                        ? history
                            .Partners(pair.Key, 0)
                            .Where(partner => !Generated(partner.Path) && files.Exists(partner.Path))
                            .Take(partners)
                            .ToArray()
                        : [];
                return new ChangeHotspot(pair.Key, pair.Value, together.Length is 0 ? null : together);
            })
            .ToArray();
        return new(output, history.Commits, []);
    }

    /// <summary>
    /// The usual partners of the changed files that the change leaves out, counted from the history; without one
    /// (no Git repository), from the configured providers.
    /// </summary>
    public static async Task<ChangeCheck> CheckAsync(
        ChangeHistory? history,
        IProviderRegistry? registry,
        string root,
        IReadOnlyList<string> changed,
        CancellationToken token
    )
    {
        var included = changed.ToHashSet(StringComparer.Ordinal);
        var files = new AnalysisRepository(root, [], token);
        List<MissingChangePartner> missing = [];
        List<string> errors = [];
        foreach (var file in changed.Where(Code).Take(30))
        {
            var result = await PartnersAsync(history is null ? registry : null, file, 20, token, history)
                .ConfigureAwait(false);
            errors.AddRange(result.Errors.Where(error => !errors.Contains(error, StringComparer.Ordinal)));
            missing.AddRange(
                result
                    .Partners.Where(partner =>
                        Usual(partner)
                        && !Generated(partner.Path)
                        && !included.Contains(partner.Path)
                        && files.Exists(partner.Path)
                    )
                    .Select(partner => new MissingChangePartner(file, partner))
            );
        }
        return new(
            missing
                .OrderByDescending(item => item.Partner.Share)
                .ThenByDescending(item => item.Partner.Together)
                .ThenBy(item => item.Partner.Path, StringComparer.Ordinal)
                .ToArray(),
            errors
        );
    }

    internal static IReadOnlyList<ChangePartner> Ranked(IEnumerable<ChangePartner> partners, int limit)
    {
        var ranked = partners
            .OrderByDescending(partner => partner.Together)
            .ThenByDescending(partner => partner.Share)
            .ThenBy(partner => partner.Path, StringComparer.Ordinal);
        return (limit > 0 ? ranked.Take(limit) : ranked).ToArray();
    }

    private static bool Code(string file) =>
        Path.GetExtension(file).ToUpperInvariant()
            is ".CS"
                or ".GO"
                or ".TS"
                or ".TSX"
                or ".JS"
                or ".JSX"
                or ".MJS"
                or ".PY"
                or ".JAVA"
                or ".KT"
                or ".RS"
                or ".DART"
                or ".VUE"
                or ".SWIFT"
                or ".SQL"
                or ".PROTO"
        && !file.Contains("node_modules/", StringComparison.Ordinal);

    internal static bool Generated(string file)
    {
        var name = Path.GetFileName(file);
        return new[] { ".designer.cs", ".g.cs", ".g.i.cs", "modelsnapshot.cs", ".pb.go", "_pb2.py" }.Any(suffix =>
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            )
            || name.Contains(".generated.", StringComparison.OrdinalIgnoreCase)
            || file.Split('/')
                .SkipLast(1)
                .Any(part =>
                    part.Equals("migrations", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("obj", StringComparison.OrdinalIgnoreCase)
                );
    }
}

/// <summary>
/// The files changed together in the window's commits, counted in-process: a partner's together count is the
/// commits that changed both files, its share the part of the file's own commits that did. As ripwire's
/// co-change, a partner needs at least <see cref="ChangeCoupling.MinTogether"/> commits together.
/// </summary>
public sealed class ChangeHistory
{
    private readonly IReadOnlyList<IReadOnlyList<string>> _changes;
    private readonly Dictionary<string, List<int>> _commitsOf = new(StringComparer.Ordinal);

    public ChangeHistory(GitChurn churn)
    {
        ArgumentNullException.ThrowIfNull(churn);
        Counts = churn.Counts;
        Commits = churn.Commits;
        _changes = churn.Changes;
        for (var index = 0; index < _changes.Count; index++)
        {
            foreach (var file in _changes[index])
            {
                if (!_commitsOf.TryGetValue(file, out var commits))
                {
                    _commitsOf[file] = commits = [];
                }

                commits.Add(index);
            }
        }
    }

    public IReadOnlyDictionary<string, int> Counts { get; }

    public int Commits { get; }

    /// <summary>The file's partners, most often together first; limit 0 for all of them.</summary>
    public IReadOnlyList<ChangePartner> Partners(string file, int limit)
    {
        if (!_commitsOf.TryGetValue(file, out var commits))
        {
            return [];
        }

        Dictionary<string, int> together = new(StringComparer.Ordinal);
        // a file is its own partner in none of its commits; a pair seen fewer than MinTogether times is chance
        foreach (var index in commits)
        {
            foreach (var other in _changes[index].Where(other => other != file))
            {
                together[other] = together.GetValueOrDefault(other) + 1;
            }
        }

        return ChangeCoupling.Ranked(
            together
                .Where(pair => pair.Value >= ChangeCoupling.MinTogether)
                .Select(pair => new ChangePartner(
                    pair.Key,
                    pair.Value,
                    Math.Round((double)pair.Value / commits.Count, 3, MidpointRounding.AwayFromZero),
                    "harness",
                    "git log"
                )),
            limit
        );
    }
}
