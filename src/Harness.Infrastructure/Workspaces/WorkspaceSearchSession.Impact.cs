using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Harness.Core.Providers;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Retrieval;

namespace Harness.Infrastructure.Workspaces;

public sealed partial class WorkspaceSearchSession
{
    public async Task<WorkspaceImpact> ImpactAsync(
        string query,
        int limit,
        bool deep,
        CancellationToken cancellationToken
    )
    {
        var impact = QueryAsync(new(Capabilities.Impact, query, Limit: limit), deep, Current.Name, cancellationToken);
        var references = QueryAsync(new(Capabilities.References, query, Limit: limit), deep, "", cancellationToken);
        var names = QueryAsync(
            new(Capabilities.TextSearch, WorkspaceReferences.LastSegment(query), Limit: limit),
            deep,
            "",
            cancellationToken
        );
        var home = await HomeAsync(query, cancellationToken).ConfigureAwait(false);
        var packages =
            home.PackageRef.Length > 0
                ? QueryAsync(new(Capabilities.TextSearch, home.PackageRef, Limit: limit), deep, "", cancellationToken)
                : Task.FromResult(Empty());
        var namespaces =
            home.Namespace.Length > 0
                ? QueryAsync(new(Capabilities.TextSearch, home.Namespace, Limit: limit), deep, "", cancellationToken)
                : Task.FromResult(Empty());
        await Task.WhenAll(impact, references, names, packages, namespaces).ConfigureAwait(false);
        List<WorkspaceHit> hits = [];
        List<string> notes = [],
            errors = [];
        HashSet<(string, string?, int)> seen = [];
        Add(await impact.ConfigureAwait(false), "impact");
        Add(await references.ConfigureAwait(false), "references");
        Add(await packages.ConfigureAwait(false), "package");
        Add(await namespaces.ConfigureAwait(false), "namespace");
        Add(await names.ConfigureAwait(false), "name");
        return new(home, hits, notes, errors);

        void Add(WorkspaceAnswer answer, string via)
        {
            foreach (var hit in answer.Hits)
            {
                if (
                    (via is "impact") != (hit.Member.Id == Current.Id)
                    || via is "package" && !IsManifest(hit.Item.Path ?? "")
                    || via is "name" && IsManifest(hit.Item.Path ?? "")
                )
                {
                    continue;
                }

                if (!seen.Add((hit.Member.Id, hit.Item.Path, hit.Item.StartLine)))
                {
                    continue;
                }

                hits.Add(
                    hit with
                    {
                        Via = via,
                        Exact = via is "package" || via is "impact" or "references" && hit.Evidence is "exact",
                    }
                );
            }
            foreach (var note in answer.Notes.Where(note => !notes.Contains(note, StringComparer.Ordinal)))
            {
                notes.Add(note);
            }

            foreach (var error in answer.Errors.Where(error => !errors.Contains(error, StringComparer.Ordinal)))
            {
                errors.Add(error);
            }
        }
    }

    private async Task<SymbolHome> HomeAsync(string query, CancellationToken token)
    {
        var shortName = WorkspaceReferences.LastSegment(query);
        var answer = await QueryAsync(new(Capabilities.SymbolSearch, shortName, Limit: 10), false, Current.Name, token)
            .ConfigureAwait(false);
        ProviderItem? best = null;
        foreach (var item in answer.Hits.Select(hit => hit.Item))
        {
            if (
                !string.Equals(
                    WorkspaceReferences.LastSegment(item.Name ?? ""),
                    shortName,
                    StringComparison.OrdinalIgnoreCase
                ) || string.IsNullOrEmpty(item.Path)
            )
            {
                continue;
            }

            if (
                best is null
                || query != shortName
                    && item.QualifiedName?.EndsWith(query, StringComparison.Ordinal) is true
                    && best.QualifiedName?.EndsWith(query, StringComparison.Ordinal) is not true
            )
            {
                best = item;
            }
        }
        if (best is null)
        {
            return new();
        }

        var reader = new RetrievalFileReader(Current.Root);
        var ns = "";
        try
        {
            ns = Regex
                .Match(
                    await reader.ReadBodyAsync(best.Path!, 1, int.MaxValue, token).ConfigureAwait(false),
                    @"(?m)^[\t\r\n ]*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)",
                    RegexOptions.NonBacktracking,
                    TimeSpan.FromSeconds(1)
                )
                .Groups[1]
                .Value;
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or DecoderFallbackException
            )
        { /* Namespace evidence can still come from a qualified symbol. */
        }
        if (
            ns.Length is 0
            && best.QualifiedName?.Count(c => c is '.') >= 2
            && !best.Path!.EndsWith(".go", StringComparison.Ordinal)
        )
        {
            ns = best.QualifiedName[..best.QualifiedName.LastIndexOf('.')];
        }

        var (package, space, reference) = await OwningPackageAsync(reader, best.Path!, ns, token).ConfigureAwait(false);
        return new(best.Path!, space, package, reference);
    }

    private async Task<(string Package, string Namespace, string Ref)> OwningPackageAsync(
        RetrievalFileReader reader,
        string path,
        string ns,
        CancellationToken token
    )
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(Current.Root, path)))!;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Current.Root));
        while (
            directory == root
            || directory.StartsWith(
                root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
            )
        )
        {
            try
            {
                var csproj = Directory
                    .EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault();
                if (csproj is not null)
                {
                    var package = await new PackageIdentityReader(root, reader)
                        .ReadAsync(csproj, token)
                        .ConfigureAwait(false);
                    return package.Length > 0 ? (package, ns, "Include=\"" + package + "\"") : ("", ns, "");
                }
                var npm = Path.Combine(directory, "package.json");
                if (File.Exists(npm))
                {
                    using var document = JsonDocument.Parse(
                        await reader
                            .ReadBodyAsync(Path.GetRelativePath(root, npm), 1, int.MaxValue, token)
                            .ConfigureAwait(false)
                    );
                    if (
                        document.RootElement.TryGetProperty("name", out var name)
                        && name.GetString() is { Length: > 0 } package
                    )
                    {
                        return (package, ns, "\"" + package + "\":");
                    }
                }
                var go = Path.Combine(directory, "go.mod");
                if (File.Exists(go))
                {
                    var text = await reader
                        .ReadBodyAsync(Path.GetRelativePath(root, go), 1, int.MaxValue, token)
                        .ConfigureAwait(false);
                    var module = Regex
                        .Match(text, @"(?m)^module\s+([^\s]+)", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1))
                        .Groups[1]
                        .Value.Trim('"');
                    if (module.Length > 0)
                    {
                        var fileDirectory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, path)))!;
                        var relative = Path.GetRelativePath(directory, fileDirectory).Replace('\\', '/');
                        return (module, relative is "." ? module : module + "/" + relative, module);
                    }
                }
            }
            catch (Exception error)
                when (error
                        is IOException
                            or UnauthorizedAccessException
                            or InvalidDataException
                            or DecoderFallbackException
                            or XmlException
                            or JsonException
                )
            { /* Preserve available evidence when a manifest is unreadable. */
            }
            if (directory == root)
            {
                break;
            }

            directory = Path.GetDirectoryName(directory)!;
        }
        return ("", ns, "");
    }

    private static bool IsManifest(string path) =>
        Path.GetExtension(path).ToUpperInvariant() is ".CSPROJ" or ".FSPROJ" or ".VBPROJ" or ".PROPS" or ".TARGETS"
        || Path.GetFileName(path).ToUpperInvariant()
            is "PACKAGES.CONFIG"
                or "PACKAGES.LOCK.JSON"
                or "PAKET.DEPENDENCIES"
                or "PACKAGE.JSON"
                or "GO.MOD";

    private static WorkspaceAnswer Empty() => new([], [], [], []);
}
