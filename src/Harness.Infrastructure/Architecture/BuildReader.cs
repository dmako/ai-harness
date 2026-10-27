using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Harness.Core.Architecture;
using Harness.Infrastructure.Workspaces;

namespace Harness.Infrastructure.Architecture;

public sealed partial class BuildReader(AnalysisRepository repository, EcosystemReader ecosystem)
{
    [GeneratedRegex(
        "^Project\\(\"\\{[^}]+\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"",
        EcosystemReader.RxOptions,
        1000
    )]
    private static partial Regex SolutionProject { get; }

    [GeneratedRegex("\"([^\"]+)\"", EcosystemReader.RxOptions, 1000)]
    private static partial Regex GoImport { get; }

    public ProjectBuild? Read()
    {
        var readers = new[]
        {
            (
                Count: repository.Files.Count(f => !EcosystemReader.Vendored(f) && Path.GetExtension(f) is ".cs"),
                Detect: repository.Files.Any(f => Path.GetExtension(f) is ".csproj"),
                Read: (Func<ProjectBuild>)Dotnet
            ),
            (
                Count: repository.Files.Count(f => !EcosystemReader.Vendored(f) && Path.GetExtension(f) is ".go"),
                Detect: repository.Files.Contains("go.mod", StringComparer.Ordinal),
                Read: (Func<ProjectBuild>)Go
            ),
            (
                Count: repository.Files.Count(f =>
                    !EcosystemReader.Vendored(f)
                    && TsCodeExtensions.Contains(Path.GetExtension(f), StringComparer.Ordinal)
                ),
                Detect: repository.Files.Any(f =>
                    !TsGenerated(f) && Path.GetFileName(f) is "package.json" or "tsconfig.json"
                ),
                Read: (Func<ProjectBuild>)TypeScript
            ),
        };
        foreach (var reader in readers.Where(reader => reader.Detect).OrderByDescending(reader => reader.Count))
        {
            var build = reader.Read();
            if (build.Projects.Count > 0)
            {
                return build;
            }
        }
        return null;
    }

    public ProjectBuild Dotnet()
    {
        List<BuildGroup> groups = [];
        Dictionary<string, List<string>> included = new(StringComparer.Ordinal);
        foreach (
            var file in repository.Files.Where(file =>
                !DotnetSkipped(file) && Path.GetExtension(file).ToUpperInvariant() is ".SLN" or ".SLNX"
            )
        )
        {
            groups.Add(ReadSolution(file, included));
        }
        List<BuildUnit> units = [];
        foreach (
            var file in repository.Files.Where(file =>
                !DotnetSkipped(file) && Path.GetExtension(file).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            units.Add(DotnetUnit(file, included));
        }
        return new(
            "dotnet",
            "project",
            groups,
            units.OrderBy(unit => unit.Path, WorkspaceNameComparer.Instance).ToArray()
        );
    }

    /// <summary>
    /// The solution's projects that exist and those that are missing; records which solutions include
    /// each project.
    /// </summary>
    private BuildGroup ReadSolution(string file, Dictionary<string, List<string>> included)
    {
        List<string> projects = [],
            missing = [];
        foreach (var path in SolutionPaths(file))
        {
            var relative = Join(Directory(file), path.Replace('\\', '/'));
            if (!repository.Exists(relative))
            {
                missing.Add(relative);
                continue;
            }
            projects.Add(relative);
            if (!included.TryGetValue(relative, out var solutions))
            {
                solutions = [];
                included.Add(relative, solutions);
            }
            solutions.Add(file);
        }
        return new(file, projects, missing.Count is 0 ? null : missing);
    }

    /// <summary>The .csproj paths a .sln or .slnx names, as written in it.</summary>
    private IEnumerable<string> SolutionPaths(string file)
    {
        if (!Path.GetExtension(file).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return AnalysisRepository
                .Lines(repository.Read(file))
                .Select(line => SolutionProject.Match(line.Trim()))
                .Where(match =>
                    match.Success
                    && Path.GetExtension(match.Groups[1].Value).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                )
                .Select(match => match.Groups[1].Value);
        }

        try
        {
            using var xml = XmlReader.Create(
                new StringReader(repository.Read(file).TrimStart('\uFEFF')),
                new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }
            );
            return XDocument
                .Load(xml)
                .Descendants()
                .Where(element => element.Name.LocalName is "Project")
                .Select(element => element.Attribute("Path")?.Value ?? "")
                .Where(path => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (XmlException)
        {
            return [];
        }
    }

    /// <summary>
    /// A project with its kind, published package and version, solutions, references and packages; an
    /// unreadable project file is a plain library.
    /// </summary>
    private BuildUnit DotnetUnit(string file, Dictionary<string, List<string>> included)
    {
        var xml = ecosystem.Build.Parse(file);
        var evaluated = ecosystem.Build.Evaluate(file);
        var kind = DotnetKind(file, xml?.Attribute("Sdk")?.Value ?? "", evaluated);
        var offered = ecosystem.DotnetPackage(file);
        var version = Property(evaluated, "PackageVersion") is { Length: > 0 } packageVersion
            ? packageVersion
            : Property(evaluated, "Version");
        var published = xml is not null && offered.Published;
        var unit = new BuildUnit
        {
            Name = Path.GetFileNameWithoutExtension(file),
            Path = file,
            Kind = xml is null ? "library" : kind,
            Package = published ? offered.Name : null,
            Version = published ? Empty(version) : null,
        };
        if (included.TryGetValue(file, out var solutionPaths))
        {
            unit.Solutions.AddRange(solutionPaths);
        }

        AddDependencies(unit, evaluated);
        return unit;
    }

    /// <summary>The project's distinct references with where each is declared, and its packages.</summary>
    private static void AddDependencies(BuildUnit unit, EvaluatedProject evaluated)
    {
        foreach (var reference in evaluated.References.DistinctBy(reference => reference.Path, StringComparer.Ordinal))
        {
            unit.References.Add(reference.Path);
            (unit.ReferenceLocations ??= new(StringComparer.Ordinal))[reference.Path] = reference.At;
        }
        unit.Packages.AddRange(
            evaluated.Packages.Select(package => new BuildPackage(
                package.Name,
                Empty(package.Version),
                package.Frameworks.Count is 0 ? null : package.Frameworks,
                package.Conditional,
                package.Unresolved
            ))
        );
    }

    /// <summary>A test project, an entry (an exe, web or worker project), or a library.</summary>
    private string DotnetKind(string file, string sdk, EvaluatedProject evaluated)
    {
        var outputType = Property(evaluated, "OutputType");
        var executable =
            outputType.Equals("exe", StringComparison.OrdinalIgnoreCase)
            || outputType.Equals("winexe", StringComparison.OrdinalIgnoreCase)
            || sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)
            || sdk.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase);
        return (ecosystem.DotnetTest(file), executable) switch
        {
            (true, _) => "test",
            (_, true) => "entry",
            _ => "library",
        };
    }

    /// <summary>The trimmed property value, or "" while it still refers to another property.</summary>
    private static string Property(EvaluatedProject evaluated, string key) =>
        evaluated.Properties.GetValueOrDefault(key, "").Trim() is var value
        && !value.Contains("$(", StringComparison.Ordinal)
            ? value
            : "";

    /// <summary>
    /// The module's packages being read: one unit per directory, its external modules, and the internal
    /// imports that do not build.
    /// </summary>
    private sealed record GoModule(string Name, Dictionary<string, string> Required)
    {
        public Dictionary<string, BuildUnit> Units { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> External { get; } = new(StringComparer.Ordinal);
        public List<ArchitectureFinding> Internals { get; } = [];

        public BuildUnit UnitFor(string dir)
        {
            if (!Units.TryGetValue(dir, out var unit))
            {
                unit = new() { Name = dir is "." ? Path.GetFileName(Name) : dir, Path = dir };
                Units.Add(dir, unit);
                External.Add(dir, new(StringComparer.Ordinal));
            }
            return unit;
        }
    }

    public ProjectBuild Go()
    {
        var module = new GoModule(
            ecosystem.Package("go.mod").Name,
            ecosystem
                .Dependencies("go.mod")
                .GroupBy(dependency => dependency.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Version, StringComparer.Ordinal)
        );
        var nested = repository
            .Files.Where(file => file != "go.mod" && Path.GetFileName(file) is "go.mod")
            .Select(file => Directory(file) + "/")
            .ToArray();
        foreach (var file in repository.Files.Where(file => GoSource(file, nested)))
        {
            ReadGoFile(module, file);
        }
        foreach (var unit in module.Units.Values)
        {
            FinishGoUnit(unit, module);
        }
        return new(
            "go",
            "package",
            [],
            module.Units.Values.OrderBy(unit => unit.Path, WorkspaceNameComparer.Instance).ToArray()
        )
        {
            Extra = module.Internals,
        };
    }

    /// <summary>
    /// A non-test .go file of this module, outside nested modules, vendor, testdata and hidden folders.
    /// </summary>
    private static bool GoSource(string file, string[] nested) =>
        Path.GetExtension(file) is ".go"
        && !file.EndsWith("_test.go", StringComparison.Ordinal)
        && !nested.Any(prefix => file.StartsWith(prefix, StringComparison.Ordinal))
        && !Directory(file)
            .Split('/')
            .Any(part => part is "vendor" or "testdata" || part.StartsWith('.') || part.StartsWith('_'));

    /// <summary>Reads a file's package clause and imports, stopping at its first declaration.</summary>
    private void ReadGoFile(GoModule module, string file)
    {
        var dir = Directory(file);
        var unit = module.UnitFor(dir);
        var block = false;
        var package = "";
        var lines = AnalysisRepository.Lines(repository.Read(file));
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (package.Length is 0 && line.StartsWith("package ", StringComparison.Ordinal))
            {
                package = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
                if (package is "main")
                {
                    unit.Kind = "entry";
                }

                continue;
            }
            if (line.StartsWith("import (", StringComparison.Ordinal))
            {
                block = true;
                continue;
            }
            if (block && line is ")")
            {
                break;
            }

            if (block || line.StartsWith("import ", StringComparison.Ordinal))
            {
                GoImportLine(module, unit, line, file + ":" + (index + 1).ToString(CultureInfo.InvariantCulture));
            }
            else if (package.Length > 0 && GoDeclaration(line))
            {
                break;
            }
        }
    }

    private static bool GoDeclaration(string line) =>
        new[] { "func ", "type ", "var ", "const " }.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// An import of this module becomes a reference (and maybe an internal-import finding); a
    /// domain-qualified one, the required module it belongs to.
    /// </summary>
    private static void GoImportLine(GoModule module, BuildUnit unit, string line, string at)
    {
        var import = GoImport.Match(line);
        if (!import.Success)
        {
            return;
        }

        var path = import.Groups[1].Value;
        if (
            module.Name.Length > 0
            && (path == module.Name || path.StartsWith(module.Name + "/", StringComparison.Ordinal))
        )
        {
            var target = path[module.Name.Length..].TrimStart('/');
            GoModuleImport(module, unit, target.Length is 0 ? "." : target, at);
        }
        else if (path.Split('/')[0].Contains('.', StringComparison.Ordinal))
        {
            module.External[unit.Path].Add(RequiredModule(module.Required, path));
        }
    }

    private static void GoModuleImport(GoModule module, BuildUnit unit, string target, string at)
    {
        var dir = unit.Path;
        if (target != dir && !unit.References.Contains(target, StringComparer.Ordinal))
        {
            unit.References.Add(target);
            (unit.ReferenceLocations ??= new(StringComparer.Ordinal))[target] = at;
        }

        var parent = InternalParent(target);
        if (
            target != dir
            && parent.Length > 0
            && dir != parent
            && !dir.StartsWith(parent + "/", StringComparison.Ordinal)
        )
        {
            module.Internals.Add(
                new()
                {
                    Kind = "internal-import",
                    Level = "problem",
                    Title = dir + " imports " + target + " from outside its tree",
                    Why =
                        "Go allows an internal package to be imported only from below the folder that holds internal/; this does not build.",
                    Fix =
                        "Move the importing package below the internal package's parent, or move the package out of internal/.",
                    Project = dir,
                    Evidence = [at],
                    Key = ArchitectureAnalyzer.Key("internal-import", dir, target),
                }
            );
        }
    }

    /// <summary>The folder holding the last internal/ in a package path; "" when it has none.</summary>
    private static string InternalParent(string target)
    {
        var wrapped = "/" + target + "/";
        var internalAt = wrapped.LastIndexOf("/internal/", StringComparison.Ordinal);
        return internalAt < 0 ? "" : wrapped[..internalAt].Trim('/');
    }

    /// <summary>The longest required module the import path belongs to, else the path itself.</summary>
    private static string RequiredModule(Dictionary<string, string> required, string path) =>
        required
            .Keys.Where(name => path == name || path.StartsWith(name + "/", StringComparison.Ordinal))
            .OrderByDescending(name => name.Length)
            .FirstOrDefault()
        ?? path;

    /// <summary>
    /// Adds the unit's external modules with their required versions, and marks test helper packages.
    /// </summary>
    private static void FinishGoUnit(BuildUnit unit, GoModule module)
    {
        unit.Packages.AddRange(
            module
                .External[unit.Path]
                .Order(WorkspaceNameComparer.Instance)
                .Select(name => new BuildPackage(name, Empty(module.Required.GetValueOrDefault(name, ""))))
        );
        unit.References.Sort(WorkspaceNameComparer.Instance);
        var name = Path.GetFileName(unit.Path);
        if (
            unit.Kind is "library"
            && (name.EndsWith("test", StringComparison.Ordinal) || name is "testutil" or "testhelpers")
        )
        {
            unit.Kind = "test";
        }
    }

    private string Join(string dir, string path) =>
        repository.Relative(Path.GetFullPath(path, Path.GetFullPath(Path.Combine(repository.Root, dir))));

    private static string Directory(string path) =>
        Path.GetDirectoryName(path)?.Replace('\\', '/') is { Length: > 0 } value ? value : ".";

    private static string? Empty(string value) => value.Length is 0 ? null : value;

    private static bool DotnetSkipped(string file) =>
        file.Split('/')
            .Any(part =>
                part.StartsWith('.')
                || part is "bin" or "obj" or "node_modules" or "packages" or "artifacts" or "TestResults" or "vendor"
            );
}
