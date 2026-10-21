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
    public static bool Usual(ChangePartner partner) => partner.Together >= MinTogether && partner.Share >= MinShare && partner.Together / partner.Share >= MinHistory - 0.5;
    public static async Task<ChangePartners> PartnersAsync(IProviderRegistry? registry, string file, int limit, CancellationToken token)
    {
        if (registry is null)
            return new([], []);
        Dictionary<string, ChangePartner> byPath = new(StringComparer.Ordinal);
        List<string> errors = [];
        foreach (var result in await registry.CallAsync(new(Capabilities.ArchChangeCoupling, file, Limit: limit), cancellationToken: token).ConfigureAwait(false))
        {
            if (result.Error is not null)
            { errors.Add(result.Error.Message.Split('\n')[0]); continue; }
            foreach (var item in result.Items.Where(item => item.Path is { Length: > 0 } && item.Path != file))
            {
                var partner = new ChangePartner(item.Path!, item.Count, item.Confidence, item.Provider, item.Tool);
                if (!byPath.TryGetValue(partner.Path, out var previous) || partner.Together > previous.Together)
                    byPath[partner.Path] = partner;
            }
        }
        var partners = byPath.Values.OrderByDescending(partner => partner.Together).ThenByDescending(partner => partner.Share).ThenBy(partner => partner.Path, StringComparer.Ordinal);
        return new((limit > 0 ? partners.Take(limit) : partners).ToArray(), errors);
    }
    public static async Task<ChangeHotspots> HotspotsAsync(GitRepository repository, IProviderRegistry? registry, int limit, int partners, CancellationToken token, TimeProvider? clock = null)
    {
        var churn = await repository.ChurnAsync((clock ?? TimeProvider.System).GetUtcNow() - Window, token).ConfigureAwait(false);
        var files = new AnalysisRepository(repository.Root, [], token);
        var output = churn.Counts.Where(pair => Code(pair.Key) && !TaskParsing.IsTestFile(pair.Key) && !Generated(pair.Key) && pair.Value > 1 && files.Exists(pair.Key)).Select(pair => new ChangeHotspot(pair.Key, pair.Value)).OrderByDescending(hotspot => hotspot.Commits).ThenBy(hotspot => hotspot.Path, StringComparer.Ordinal).Take(Math.Max(0, limit)).ToArray();
        List<string> errors = [];
        if (registry is not null && partners > 0)
            for (var index = 0; index < output.Length; index++)
            {
                var result = await PartnersAsync(registry, output[index].Path, partners, token).ConfigureAwait(false);
                output[index] = output[index] with { Partners = result.Partners.Count is 0 ? null : result.Partners };
                errors.AddRange(result.Errors.Where(error => !errors.Contains(error, StringComparer.Ordinal)));
            }
        return new(output, churn.Commits, errors);
    }
    public static async Task<ChangeCheck> CheckAsync(IProviderRegistry? registry, string root, IReadOnlyList<string> changed, CancellationToken token)
    {
        var included = changed.ToHashSet(StringComparer.Ordinal);
        var files = new AnalysisRepository(root, [], token);
        List<MissingChangePartner> missing = [];
        List<string> errors = [];
        foreach (var file in changed.Where(Code).Take(30))
        {
            var result = await PartnersAsync(registry, file, 20, token).ConfigureAwait(false);
            errors.AddRange(result.Errors.Where(error => !errors.Contains(error, StringComparer.Ordinal)));
            missing.AddRange(result.Partners.Where(partner => Usual(partner) && !included.Contains(partner.Path) && files.Exists(partner.Path)).Select(partner => new MissingChangePartner(file, partner)));
        }
        return new(missing.OrderByDescending(item => item.Partner.Share).ThenByDescending(item => item.Partner.Together).ThenBy(item => item.Partner.Path, StringComparer.Ordinal).ToArray(), errors);
    }
    private static bool Code(string file) => Path.GetExtension(file).ToUpperInvariant() is ".CS" or ".GO" or ".TS" or ".TSX" or ".JS" or ".JSX" or ".MJS" or ".PY" or ".JAVA" or ".KT" or ".RS" or ".DART" or ".VUE" or ".SWIFT" or ".SQL" or ".PROTO" && !file.Contains("node_modules/", StringComparison.Ordinal);
    internal static bool Generated(string file)
    {
        var name = Path.GetFileName(file);
        return new[] { ".designer.cs", ".g.cs", ".g.i.cs", "modelsnapshot.cs", ".pb.go", "_pb2.py" }.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) || name.Contains(".generated.", StringComparison.OrdinalIgnoreCase) || file.Split('/').SkipLast(1).Any(part => part.Equals("migrations", StringComparison.OrdinalIgnoreCase) || part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }
}
