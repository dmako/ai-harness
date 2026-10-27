using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Architecture;
using Harness.Infrastructure.Workspaces;

namespace Harness.Infrastructure.Architecture;

public sealed partial class BuildReader
{
    private static readonly string[] TsCodeExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts"];
    private static readonly HashSet<string> Containers = new(
        ["features", "feature", "modules", "domains"],
        StringComparer.Ordinal
    );
    private static readonly HashSet<string> TestDirectories = new(
        ["__tests__", "test", "tests", "e2e", "cypress", "playwright", "__mocks__"],
        StringComparer.Ordinal
    );
    private static readonly HashSet<string> Generated = new(
        ["node_modules", "dist", "build", "out", ".next", "coverage", "storybook-static", ".turbo", ".vercel"],
        StringComparer.Ordinal
    );

    [GeneratedRegex(
        "(?:\\bfrom\\s*|\\bimport\\s*\\(?\\s*|\\brequire\\(\\s*)['\"]([^'\"]+)['\"]",
        EcosystemReader.RxOptions,
        1000
    )]
    private static partial Regex Imports { get; }

    [GeneratedRegex(
        "['\"]?([@~#$\\w/-]+)['\"]?\\s*:\\s*(?:path\\.)?(?:resolve|join)\\([^,]*,\\s*['\"]\\.?/?([^'\"]+)['\"]\\)",
        EcosystemReader.RxOptions,
        1000
    )]
    private static partial Regex ViteAlias { get; }

    internal sealed record PackageInfo(
        string Name,
        string Version,
        bool Private,
        string Main,
        bool Bin,
        Dictionary<string, string>[] Dependencies,
        IReadOnlyList<string> Workspaces
    );

    internal sealed record TsUnit(BuildUnit Build, string Folder, bool Root, PackageInfo Package)
    {
        internal HashSet<string> Npm { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// What resolving imports needs: the units, their names, the aliases, and the deep imports found.
    /// </summary>
    private sealed record TsGraph(
        HashSet<string> Files,
        List<TsUnit> Units,
        Dictionary<string, TsUnit> ByName,
        List<(string Prefix, string Directory)> Aliases
    )
    {
        internal Dictionary<(string From, string To), List<string>> Deeps { get; } = [];
    }

    public ProjectBuild TypeScript()
    {
        var files = repository.Files.Where(file => !TsGenerated(file)).ToHashSet(StringComparer.Ordinal);
        var code = repository
            .Files.Where(file =>
                files.Contains(file)
                && TsCodeExtensions.Contains(Path.GetExtension(file), StringComparer.Ordinal)
                && !file.EndsWith(".d.ts", StringComparison.Ordinal)
            )
            .ToArray();
        var root = ReadPackage("package.json");
        List<string> globs = [.. root.Workspaces, .. PnpmWorkspaces()];
        var noun = globs.Count > 0 ? "package" : "folder";
        var units = noun is "package" ? PackageUnits(files, globs) : FolderUnits(code, root);
        var graph = new TsGraph(
            files,
            units,
            noun is "package"
                ? units.ToDictionary(unit => unit.Build.Name, StringComparer.Ordinal)
                : new(StringComparer.Ordinal),
            Aliases()
        );
        foreach (var file in code)
        {
            ReadImports(graph, file);
        }
        foreach (var unit in units)
        {
            if (noun is "package")
            {
                ReferenceDependencies(graph, unit);
            }

            AddPackages(unit, noun is "package" ? unit.Package : root, root);
        }
        var names = units.ToDictionary(unit => unit.Build.Path, unit => unit.Build.Name, StringComparer.Ordinal);
        return new(
            "typescript",
            noun,
            [],
            units.Select(unit => unit.Build).OrderBy(unit => unit.Path, WorkspaceNameComparer.Instance).ToArray()
        )
        {
            Extra = DeepImportFindings(graph.Deeps, names),
        };
    }

    /// <summary>The package globs listed under <c>packages:</c> in pnpm-workspace.yaml.</summary>
    private List<string> PnpmWorkspaces()
    {
        List<string> globs = [];
        if (!repository.Exists("pnpm-workspace.yaml"))
        {
            return globs;
        }

        var inPackages = false;
        foreach (var line in AnalysisRepository.Lines(repository.Read("pnpm-workspace.yaml")))
        {
            var text = line.Trim();
            if (text.StartsWith("packages:", StringComparison.Ordinal))
            {
                inPackages = true;
            }
            else if (inPackages && text.StartsWith("- ", StringComparison.Ordinal))
            {
                globs.Add(text[2..].Trim().Trim('\'', '"'));
            }
            else if (inPackages && text.Length > 0 && !text.StartsWith('#'))
            {
                inPackages = false;
            }
        }
        return globs;
    }

    /// <summary>One unit per named package.json (other than the root one) in a workspace glob.</summary>
    private List<TsUnit> PackageUnits(HashSet<string> files, List<string> globs)
    {
        List<TsUnit> units = [];
        foreach (
            var file in repository.Files.Where(file =>
                files.Contains(file)
                && file != "package.json"
                && Path.GetFileName(file) is "package.json"
                && globs.Any(glob => Glob(glob, Directory(file)))
            )
        )
        {
            var package = ReadPackage(file);
            if (package.Name.Length is 0)
            {
                continue;
            }

            var dir = Directory(file);
            units.Add(
                new(
                    new()
                    {
                        Name = package.Name,
                        Path = file,
                        Kind = TsPackageKind(dir, package),
                        Package = package.Private ? null : package.Name,
                        Version = package.Private ? null : Empty(package.Version),
                    },
                    dir,
                    false,
                    package
                )
            );
        }
        return units;
    }

    private static string TsPackageKind(string dir, PackageInfo package)
    {
        if (TestDirectories.Contains(Path.GetFileName(dir)))
        {
            return "test";
        }

        return dir.StartsWith("apps/", StringComparison.Ordinal)
            || package.Private && package.Main.Length is 0 && !package.Bin
            ? "entry"
            : "library";
    }

    /// <summary>Records the references and npm packages a code file's imports add to its unit.</summary>
    private void ReadImports(TsGraph graph, string file)
    {
        var from = TsUnitOf(graph.Units, file);
        if (from is null || TsTest(file) && from.Build.Kind is not "test")
        {
            return;
        }

        var lines = AnalysisRepository.Lines(repository.Read(file));
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*'))
            {
                continue;
            }

            var at = file + ":" + (index + 1).ToString(CultureInfo.InvariantCulture);
            foreach (var import in Imports.Matches(lines[index]).Select(match => match.Groups))
            {
                TsImport(graph, from, file, at, import[1].Value);
            }
        }
    }

    private void TsImport(TsGraph graph, TsUnit from, string file, string at, string spec)
    {
        var (target, bare) = Resolve(spec, file, graph.Aliases, graph.Files);
        TsUnit? to = null;
        if (target.Length > 0)
        {
            to = TsUnitOf(graph.Units, target);
        }
        else if (
            bare.Length > 0
            && !graph.ByName.TryGetValue(bare, out to)
            && !bare.StartsWith("node:", StringComparison.Ordinal)
        )
        {
            from.Npm.Add(bare);
        }

        if (to is null || to == from)
        {
            return;
        }

        Reference(from.Build, to.Build.Path, at);
        if (target.Length > 0 && DeepInto(to, target))
        {
            AddDeepImport(graph.Deeps, (from.Build.Path, to.Build.Path), at + " " + spec);
        }
    }

    /// <summary>
    /// Whether <paramref name="target"/> is a file inside a feature of a container folder, not its index.
    /// </summary>
    private static bool DeepInto(TsUnit to, string target)
    {
        if (to.Folder.Length is 0 || !Containers.Contains(Path.GetFileName(Directory(to.Folder))))
        {
            return false;
        }

        var rest = target[to.Folder.Length..].TrimStart('/');
        return rest.Length > 0 && rest is not "index" && !rest.StartsWith("index.", StringComparison.Ordinal);
    }

    /// <summary>Keeps the first three places of each deep import between two units.</summary>
    private static void AddDeepImport(
        Dictionary<(string From, string To), List<string>> deeps,
        (string From, string To) key,
        string place
    )
    {
        if (!deeps.TryGetValue(key, out var places))
        {
            places = [];
            deeps.Add(key, places);
        }
        if (places.Count < 3)
        {
            places.Add(place);
        }
    }

    /// <summary>A package's dependencies: references to workspace packages, npm packages otherwise.</summary>
    private void ReferenceDependencies(TsGraph graph, TsUnit unit)
    {
        foreach (var name in unit.Package.Dependencies.SelectMany(section => section.Keys))
        {
            if (!graph.ByName.TryGetValue(name, out var to))
            {
                unit.Npm.Add(name);
            }
            else if (to != unit)
            {
                Reference(unit.Build, to.Build.Path, LineOf(unit.Build.Path, '"' + name + '"'));
            }
        }
    }

    private static void AddPackages(TsUnit unit, PackageInfo source, PackageInfo root)
    {
        unit.Build.Packages.AddRange(
            unit.Npm.Order(WorkspaceNameComparer.Instance)
                .Select(name => new BuildPackage(
                    name,
                    Empty(
                        PackageVersion(source, name) is { Length: > 0 } version
                            ? version
                            : PackageVersion(root, name)
                    )
                ))
        );
        unit.Build.References.Sort(WorkspaceNameComparer.Instance);
    }

    private static ArchitectureFinding[] DeepImportFindings(
        Dictionary<(string From, string To), List<string>> deeps,
        Dictionary<string, string> names
    ) =>
        deeps
            .Select(pair => new ArchitectureFinding
            {
                Kind = "deep-import",
                Level = "warn",
                Title = names[pair.Key.From] + " reaches into " + names[pair.Key.To] + "'s internals",
                Why =
                    "It imports files inside the feature instead of its entry file (index), so a change inside "
                    + names[pair.Key.To]
                    + " can break "
                    + names[pair.Key.From]
                    + ". Export what is needed from its index.",
                Fix = "Export what is used from the feature's index and import it from there.",
                Project = pair.Key.From,
                Evidence = pair.Value,
                Key = ArchitectureAnalyzer.Key("deep-import", pair.Key.From, pair.Key.To),
            })
            .OrderBy(finding => finding.Title, StringComparer.Ordinal)
            .ToArray();

    /// <summary>The unit with the longest folder containing the file, else a root unit owning it.</summary>
    private static TsUnit? TsUnitOf(List<TsUnit> units, string file) =>
        units
            .Where(unit =>
                unit.Folder.Length > 0
                && (file == unit.Folder || file.StartsWith(unit.Folder + "/", StringComparison.Ordinal))
            )
            .OrderByDescending(unit => unit.Folder.Length)
            .FirstOrDefault()
        ?? units.FirstOrDefault(unit =>
            unit.Root
            && (!Under(file, unit.Build.Path, out var relative) || !relative.Contains('/', StringComparison.Ordinal))
        );

    private List<TsUnit> FolderUnits(string[] code, PackageInfo root)
    {
        var source = code.Any(file => file.StartsWith("src/", StringComparison.Ordinal)) ? "src" : "";
        while (true)
        {
            var under = code.Where(file => Under(file, source, out _))
                .Select(file =>
                {
                    _ = Under(file, source, out var relative);
                    return relative;
                })
                .ToArray();
            var next = under
                .Where(file => file.Contains('/', StringComparison.Ordinal))
                .GroupBy(file => file.Split('/')[0], StringComparer.Ordinal)
                .Where(group =>
                    group.Count() * 10 >= under.Length * 9
                    && !Containers.Contains(group.Key)
                    && !TestDirectories.Contains(group.Key)
                )
                .Select(group => group.Key)
                .LastOrDefault();
            if (next is null || under.Length is 0)
            {
                break;
            }

            source = Join(source.Length is 0 ? "." : source, next);
        }
        var dirs = code.Where(file => Under(file, source, out _))
            .Select(file =>
            {
                _ = Under(file, source, out var relative);
                var parts = relative.Split('/');
                return parts.Length switch
                {
                    < 2 => "",
                    >= 3 when Containers.Contains(parts[0]) => parts[0] + "/" + parts[1],
                    _ => parts[0],
                };
            })
            .Where(dir => dir.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(WorkspaceNameComparer.Instance);
        var rootName = Path.GetFileName(source);
        if (source.Length is 0)
        {
            rootName = root.Name.Length is 0 ? "root" : root.Name;
        }

        List<TsUnit> units =
        [
            new(
                new()
                {
                    Name = rootName,
                    Path = source.Length is 0 ? "." : source,
                    Kind = "entry",
                },
                "",
                true,
                root
            ),
        ];
        foreach (var dir in dirs)
        {
            units.Add(
                new(
                    new()
                    {
                        Name = dir,
                        Path = Join(source.Length is 0 ? "." : source, dir),
                        Kind = TestDirectories.Contains(Path.GetFileName(dir)) ? "test" : "library",
                    },
                    Join(source.Length is 0 ? "." : source, dir),
                    false,
                    root
                )
            );
        }

        return units;
    }

    private PackageInfo ReadPackage(string file)
    {
        using var json = repository.Exists(file) ? ecosystem.Json(file) : null;
        var root = json?.RootElement ?? default;
        string Text(string key) =>
            root.ValueKind is JsonValueKind.Object
            && root.TryGetProperty(key, out var item)
            && item.ValueKind is JsonValueKind.String
                ? item.GetString()!
                : "";
        Dictionary<string, string> Section(string key) =>
            root.ValueKind is JsonValueKind.Object
            && root.TryGetProperty(key, out var item)
            && item.ValueKind is JsonValueKind.Object
                ? item.EnumerateObject()
                    .Where(property => property.Value.ValueKind is JsonValueKind.String)
                    .ToDictionary(
                        property => property.Name,
                        property => property.Value.GetString()!,
                        StringComparer.Ordinal
                    )
                : new(StringComparer.Ordinal);
        List<string> workspaces = [];
        if (root.ValueKind is JsonValueKind.Object && root.TryGetProperty("workspaces", out var ws))
        {
            if (ws.ValueKind is JsonValueKind.Object)
            {
                _ = ws.TryGetProperty("packages", out ws);
            }

            if (ws.ValueKind is JsonValueKind.Array)
            {
                workspaces.AddRange(
                    ws.EnumerateArray()
                        .Where(item => item.ValueKind is JsonValueKind.String)
                        .Select(item => item.GetString()!)
                );
            }
        }
        return new(
            Text("name"),
            Text("version"),
            root.ValueKind is JsonValueKind.Object
                && root.TryGetProperty("private", out var priv)
                && priv.ValueKind is JsonValueKind.True,
            Text("main"),
            root.ValueKind is JsonValueKind.Object && root.TryGetProperty("bin", out _),
            [Section("dependencies"), Section("devDependencies"), Section("peerDependencies")],
            workspaces
        );
    }

    private List<(string Prefix, string Directory)> Aliases()
    {
        List<(string Prefix, string Directory)> aliases = [];
        foreach (
            var file in repository.Files.Where(file =>
                !TsGenerated(file)
                && Path.GetFileName(file).StartsWith("tsconfig", StringComparison.Ordinal)
                && file.EndsWith(".json", StringComparison.Ordinal)
            )
        )
        {
            try
            {
                using var document = JsonDocument.Parse(
                    repository.Read(file),
                    new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }
                );
                if (
                    !document.RootElement.TryGetProperty("compilerOptions", out var options)
                    || options.ValueKind is not JsonValueKind.Object
                )
                {
                    continue;
                }

                var baseUrl =
                    options.TryGetProperty("baseUrl", out var value) && value.ValueKind is JsonValueKind.String
                        ? value.GetString()!
                        : "";
                var directory = Join(Directory(file), baseUrl.Length is 0 ? "." : baseUrl);
                if (options.TryGetProperty("paths", out var paths) && paths.ValueKind is JsonValueKind.Object)
                {
                    foreach (
                        var property in paths
                            .EnumerateObject()
                            .Where(property =>
                                property.Value.ValueKind is JsonValueKind.Array
                                && property.Value.GetArrayLength() > 0
                                && property.Value[0].ValueKind is JsonValueKind.String
                            )
                    )
                    {
                        var path = property.Value[0].GetString()!;
                        aliases.Add(
                            (
                                property.Name.TrimEnd('*'),
                                Join(directory, path.TrimEnd('*')).TrimEnd('/')
                                    + (path.EndsWith('*') || path.EndsWith('/') ? "/" : "")
                            )
                        );
                    }
                }

                if (baseUrl.Length > 0)
                {
                    aliases.Add(("", directory.TrimEnd('/') + "/"));
                }
            }
            catch (JsonException)
            { /* Unreadable alias configuration supplies no resolution evidence. */
            }
        }
        foreach (
            var file in repository.Files.Where(file =>
                !TsGenerated(file) && Path.GetFileName(file).StartsWith("vite.config.", StringComparison.Ordinal)
            )
        )
        {
            foreach (var groups in ViteAlias.Matches(repository.Read(file)).Select(match => match.Groups))
            {
                aliases.Add((groups[1].Value.TrimEnd('/') + "/", Join(Directory(file), groups[2].Value) + "/"));
            }
        }

        return aliases.OrderByDescending(alias => alias.Prefix.Length).ToList();
    }

    private (string Target, string Bare) Resolve(
        string spec,
        string from,
        IReadOnlyList<(string Prefix, string Directory)> aliases,
        HashSet<string> files
    )
    {
        if (spec.StartsWith('.'))
        {
            return (Exists(Join(Directory(from), spec), files), "");
        }

        foreach (var alias in aliases)
        {
            if (alias.Prefix.Length is 0)
            {
                var target = Exists(Join(alias.Directory, spec), files);
                if (target.Length > 0)
                {
                    return (target, "");
                }
            }
            else if (spec == alias.Prefix.TrimEnd('/') || spec.StartsWith(alias.Prefix, StringComparison.Ordinal))
            {
                var rest = spec[alias.Prefix.TrimEnd('/').Length..].TrimStart('/');
                var target = Exists(Join(alias.Directory, rest.Length is 0 ? "." : rest), files);
                if (target.Length > 0)
                {
                    return (target, "");
                }
            }
        }
        var parts = spec.Split('/');
        return ("", spec.StartsWith('@') && parts.Length > 1 ? parts[0] + "/" + parts[1] : parts[0]);
    }

    private static string Exists(string path, HashSet<string> files)
    {
        if (files.Contains(path))
        {
            return path[..(path.Length - Path.GetExtension(path).Length)];
        }

        if (
            TsCodeExtensions
                .Append(".d.ts")
                .Any(extension => files.Contains(path + extension) || files.Contains(path + "/index" + extension))
        )
        {
            return path;
        }

        var ext = Path.GetExtension(path);
        var stem = path[..(path.Length - ext.Length)];
        return ext is ".js" or ".jsx" && (files.Contains(stem + ".ts") || files.Contains(stem + ".tsx")) ? stem : "";
    }

    private string LineOf(string file, string needle)
    {
        var line = Array.FindIndex(
            AnalysisRepository.Lines(repository.Read(file)),
            text => text.Contains(needle, StringComparison.Ordinal)
        );
        return line < 0 ? file : file + ":" + (line + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static void Reference(BuildUnit unit, string to, string at)
    {
        if (unit.References.Contains(to, StringComparer.Ordinal))
        {
            return;
        }

        unit.References.Add(to);
        (unit.ReferenceLocations ??= new(StringComparer.Ordinal))[to] = at;
    }

    private static string PackageVersion(PackageInfo package, string name) =>
        package
            .Dependencies.Select(section => section.GetValueOrDefault(name))
            .FirstOrDefault(value => value is not null)
        ?? "";

    private static bool Under(string file, string directory, out string relative)
    {
        relative = file.StartsWith(directory + "/", StringComparison.Ordinal) ? file[(directory.Length + 1)..] : "";
        if (directory is "" or ".")
        {
            relative = file;
        }

        return directory is "" or "." || relative.Length > 0;
    }

    private static bool Glob(string glob, string directory)
    {
        if (glob.StartsWith("./", StringComparison.Ordinal))
        {
            glob = glob[2..];
        }

        glob = glob.TrimEnd('/');
        if (glob.EndsWith("/**", StringComparison.Ordinal))
        {
            return directory.StartsWith(glob[..^2], StringComparison.Ordinal);
        }

        var pattern =
            "^"
            + Regex
                .Escape(glob)
                .Replace("\\*", "[^/]*", StringComparison.Ordinal)
                .Replace("\\?", "[^/]", StringComparison.Ordinal)
            + "$";
        return Regex.IsMatch(
            directory,
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromSeconds(1)
        );
    }

    private static bool TsGenerated(string file) => file.Split('/').Any(Generated.Contains);

    private static bool TsTest(string file) =>
        Path.GetFileName(file).Contains(".test.", StringComparison.Ordinal)
        || Path.GetFileName(file).Contains(".spec.", StringComparison.Ordinal)
        || Path.GetFileName(file).Contains(".stories.", StringComparison.Ordinal)
        || Directory(file).Split('/').Any(TestDirectories.Contains);
}
