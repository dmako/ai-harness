using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Architecture;
using Harness.Core.Retrieval;
using Harness.Infrastructure.Workspaces;

namespace Harness.Infrastructure.Architecture;

public sealed class EcosystemReader(AnalysisRepository repository)
{
    private static readonly Regex Namespace = Rx(@"(?m)^\s*namespace\s+([A-Za-z_][\w.]*)");
    private static readonly Regex GoModule = Rx(@"(?m)^module\s+(\S+)");
    private static readonly Regex GoRequire = Rx(@"^\s*(?:require\s+)?([\w.-]+\.[\w./-]+)\s+(v\d\S*)");
    private static readonly Regex NpmVersion = Rx(@"^[~^]?\d");
    private readonly Dictionary<string, IReadOnlyList<ManifestDependency>> _dependencies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OfferedPackage> _packages = new(StringComparer.Ordinal);
    public StaticMsBuild Build { get; } = new(repository);

    public static string Of(string path) =>
        (Path.GetFileName(path).ToUpperInvariant(), Path.GetExtension(path).ToUpperInvariant()) switch
        {
            (_, ".CSPROJ" or ".FSPROJ" or ".VBPROJ" or ".PROPS" or ".TARGETS")
            or ("PACKAGES.CONFIG" or "PACKAGES.LOCK.JSON" or "PAKET.DEPENDENCIES", _) => "NuGet",
            ("PACKAGE.JSON", _) => "npm",
            _ when Path.GetFileName(path) is "go.mod" => "Go modules",
            _ => "",
        };

    public static bool Vendored(string path) => path.Contains("node_modules/", StringComparison.Ordinal);

    public OfferedPackage Package(string path)
    {
        if (_packages.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var ecosystem = Of(path);
        var result = ecosystem switch
        {
            "NuGet" when path.EndsWith(".csproj", StringComparison.Ordinal) => DotnetPackage(path),
            "npm" when !Vendored(path) => NpmPackage(path),
            "Go modules" => new OfferedPackage(GoModule.Match(repository.Read(path)).Groups[1].Value, ecosystem),
            _ => new OfferedPackage("", ecosystem, false),
        };
        _packages.Add(path, result);
        return result;
    }

    public OfferedPackage DotnetPackage(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var xml = Build.Parse(path);
        if (xml is null)
        {
            return new(name, "NuGet", false);
        }

        var evaluated = Build.Evaluate(path);
        string Prop(string key) =>
            evaluated.Properties.GetValueOrDefault(key, "").Trim() is var value
            && !value.Contains("$(", StringComparison.Ordinal)
                ? value
                : "";
        var sdk = xml.Attribute("Sdk")?.Value ?? "";
        var test =
            Prop("IsTestProject").Equals("true", StringComparison.OrdinalIgnoreCase)
            || sdk.Equals("MSTest.Sdk", StringComparison.OrdinalIgnoreCase)
            || evaluated.Packages.Any(package =>
                package.Name.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
            )
            || TaskParsing.IsTestFile(path) && Prop("PackageId").Length is 0;
        var executable =
            Prop("OutputType").Equals("exe", StringComparison.OrdinalIgnoreCase)
            || sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)
            || sdk.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase);
        var id = new[] { Prop("PackageId"), Prop("AssemblyName"), name }.First(value => value.Length > 0);
        return new(
            id,
            "NuGet",
            !test && !executable && !Prop("IsPackable").Equals("false", StringComparison.OrdinalIgnoreCase)
                || Prop("IsPackable").Equals("true", StringComparison.OrdinalIgnoreCase)
        );
    }

    public bool DotnetTest(string path)
    {
        var evaluated = Build.Evaluate(path);
        var package = evaluated.Properties.GetValueOrDefault("PackageId", "");
        return evaluated
                .Properties.GetValueOrDefault("IsTestProject", "")
                .Equals("true", StringComparison.OrdinalIgnoreCase)
            || Build.Parse(path)?.Attribute("Sdk")?.Value.Equals("MSTest.Sdk", StringComparison.OrdinalIgnoreCase)
                is true
            || evaluated.Packages.Any(p => p.Name.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
            || TaskParsing.IsTestFile(path)
                && (package.Length is 0 || package.Contains("$(", StringComparison.Ordinal));
    }

    private OfferedPackage NpmPackage(string path)
    {
        using var document = Json(path);
        var name =
            document?.RootElement.TryGetProperty("name", out var value) is true
            && value.ValueKind is JsonValueKind.String
                ? value.GetString()!
                : "";
        return new(name, "npm", name.Length > 0);
    }

    public IReadOnlyList<OfferedPackage> Offered() =>
        repository
            .Files.Where(path => Of(path).Length > 0 && !Vendored(path))
            .Select(Package)
            .Where(package => package.Published && package.Name.Length > 0)
            .DistinctBy(package => package.Name, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<ManifestDependency> Dependencies(string path)
    {
        if (_dependencies.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var result = Of(path) switch
        {
            "NuGet" when Path.GetExtension(path).ToUpperInvariant() is ".CSPROJ" or ".FSPROJ" or ".VBPROJ" => Build
                .Evaluate(path)
                .Packages.Select(package =>
                {
                    var split = package.At.LastIndexOf(':');
                    return new ManifestDependency(
                        package.Name,
                        package.Version,
                        split > 0 ? package.At[..split] : package.At,
                        split > 0
                            ? int.Parse(package.At[(split + 1)..], System.Globalization.CultureInfo.InvariantCulture)
                            : 0,
                        package.Unresolved
                    );
                })
                .ToArray(),
            "npm" => NpmDependencies(path),
            "Go modules" => AnalysisRepository
                .Lines(repository.Read(path))
                .Select((line, index) => (Match: GoRequire.Match(line), Line: index + 1))
                .Where(row => row.Match.Success)
                .Select(row => new ManifestDependency(
                    row.Match.Groups[1].Value,
                    row.Match.Groups[2].Value,
                    path,
                    row.Line
                ))
                .ToArray(),
            _ => [],
        };
        _dependencies.Add(path, result);
        return result;
    }

    private ManifestDependency[] NpmDependencies(string path)
    {
        using var document = Json(path);
        if (document is null || document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            return [];
        }

        List<ManifestDependency> result = [];
        var lines = AnalysisRepository.Lines(repository.Read(path));
        foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
        {
            if (
                !document.RootElement.TryGetProperty(section, out var dependencies)
                || dependencies.ValueKind is not JsonValueKind.Object
                || dependencies.EnumerateObject().Any(item => item.Value.ValueKind is not JsonValueKind.String)
            )
            {
                continue;
            }

            var start = Math.Max(
                0,
                Array.FindIndex(lines, line => line.Contains('"' + section + '"', StringComparison.Ordinal))
            );
            foreach (
                var dependency in dependencies
                    .EnumerateObject()
                    .OrderBy(item => item.Name, WorkspaceNameComparer.Instance)
            )
            {
                var line =
                    Array.FindIndex(
                        lines,
                        start,
                        text => text.Contains('"' + dependency.Name + '"', StringComparison.Ordinal)
                    ) + 1;
                var version = dependency.Value.GetString()!;
                result.Add(new(dependency.Name, NpmVersion.IsMatch(version) ? version : "", path, line));
            }
        }
        return result.ToArray();
    }

    public (IReadOnlyList<string> Roots, string Ecosystem) ImportRoots(IReadOnlyList<OfferedPackage> offered)
    {
        foreach (var ecosystem in new[] { "NuGet", "npm", "Go modules" })
        {
            var packages = offered
                .Where(package => package.Ecosystem == ecosystem)
                .Select(package => package.Name)
                .ToArray();
            var roots = packages;
            if (ecosystem is "NuGet")
            {
                roots = repository
                    .Files.Where(path =>
                        path.EndsWith(".cs", StringComparison.Ordinal) && !TaskParsing.IsTestFile(path)
                    )
                    .Select(path => CSharpNamespace(repository.Read(path)))
                    .Where(ns => ns.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Select(ns => string.Join('.', ns.Split('.').Take(2)))
                    .Where(ns =>
                        packages.Length is 0
                        || packages.Any(package =>
                            package == ns || package.StartsWith(ns + ".", StringComparison.Ordinal)
                        )
                    )
                    .Distinct(StringComparer.Ordinal)
                    .Order(WorkspaceNameComparer.Instance)
                    .Take(5)
                    .ToArray();
            }

            if (roots.Length > 0)
            {
                return (roots, ecosystem);
            }
        }
        return ([], "");
    }

    public JsonDocument? Json(string path)
    {
        try
        {
            return JsonDocument.Parse(repository.Read(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string CSharpNamespace(string text) => Namespace.Match(text).Groups[1].Value;

    internal static Regex Rx(string pattern) =>
        new(pattern, RegexOptions.ECMAScript | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
