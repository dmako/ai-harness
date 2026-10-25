namespace Harness.Core.Architecture;

/// <summary>Analyzes file references; names provide roles, never compiler facts.</summary>
public static class ArchitectureAnalyzer
{
    public static string Key(string kind, params string[] units) => kind + ":" + string.Join('|', units);

    public static FindingSource Harness(string? tool = null) => new("harness", tool);

    public static ArchitectureFinding Complete(ArchitectureFinding finding, FindingSource? source = null)
    {
        var confidence = finding.Kind switch
        {
            "layering" => 0.6,
            "chatty" => 0.7,
            "contract-unused" => 0.5,
            "shared-database" => 0.6,
            _ => 0,
        };
        var defaultBasis = confidence > 0 ? "heuristic" : "fact";
        var basis = finding.Basis.Length > 0 ? finding.Basis : defaultBasis;
        return finding with
        {
            Key = finding.Key.Length > 0 ? finding.Key : Key(finding.Kind, finding.Project ?? finding.Title),
            Measure = finding.Measure is 0 ? Math.Max(1, finding.Evidence?.Count ?? 0) : finding.Measure,
            Basis = basis,
            Confidence = basis is "heuristic" && finding.Confidence is 0 ? confidence : finding.Confidence,
            Source = finding.Source.Provider.Length > 0 ? finding.Source : source ?? Harness(),
        };
    }

    public static int Severity(string level) =>
        level switch
        {
            "problem" => 0,
            "warn" => 1,
            "info" => 2,
            _ => 0,
        };

    public static IReadOnlyList<string> At(BuildUnit unit, string target) =>
        [unit.ReferenceLocations?.GetValueOrDefault(target) ?? unit.Path];

    public static ProjectArchitecture Analyze(ProjectBuild build, Func<string, bool>? used = null)
    {
        var byPath = build.Projects.ToDictionary(unit => unit.Path, StringComparer.Ordinal);
        var (components, members) = Components(build.Projects, byPath);
        Dictionary<int, int> layers = [];
        int Visit(int component)
        {
            if (layers.TryGetValue(component, out var layer))
            {
                return layer;
            }

            layers[component] = 0;
            foreach (var path in members[component])
            {
                foreach (var reference in byPath[path].References)
                {
                    if (components.TryGetValue(reference, out var target) && target != component)
                    {
                        layer = Math.Max(layer, Visit(target) + 1);
                    }
                }
            }

            return layers[component] = layer;
        }
        var referencedBy = build
            .Projects.SelectMany(unit => unit.References.Select(target => (target, unit.Path)))
            .GroupBy(pair => pair.target, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(pair => pair.Path).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal
            );
        var projects = build
            .Projects.Select(unit => new LayeredUnit(unit)
            {
                Layer = Visit(components[unit.Path]),
                Role = RoleOf(build.Language, unit),
                ReferencedBy = referencedBy.GetValueOrDefault(unit.Path) ?? [],
            })
            .ToArray();
        var findings = Findings(build, projects, byPath, members.Where(component => component.Count > 1), used);
        return new(
            build.Language,
            build.Noun,
            build.Solutions,
            projects,
            findings,
            projects.Length is 0 ? 0 : projects.Max(unit => unit.Layer) + 1
        );
    }

    private static ArchitectureFinding[] Findings(
        ProjectBuild build,
        LayeredUnit[] projects,
        Dictionary<string, BuildUnit> byPath,
        IEnumerable<List<string>> cycles,
        Func<string, bool>? used
    )
    {
        List<ArchitectureFinding> found = [];
        var noun = build.Noun.Length is 0 ? "project" : build.Noun;
        foreach (var cycle in cycles)
        {
            List<string> evidence = [];
            foreach (var path in cycle)
            {
                var target = byPath[path]
                    .References.FirstOrDefault(reference => cycle.Contains(reference, StringComparer.Ordinal));
                if (target is not null && evidence.Count < 6)
                {
                    evidence.Add(
                        At(byPath[path], target)[0] + " (" + byPath[path].Name + " → " + byPath[target].Name + ")"
                    );
                }
            }
            found.Add(
                new()
                {
                    Kind = "cycle",
                    Level = "problem",
                    Title =
                        $"{cycle.Count} {noun}s depend on each other in a circle: "
                        + string.Join(", ", cycle.Select(path => byPath[path].Name)),
                    Why = build.Language is "dotnet"
                        ? "MSBuild cannot build a circle of project references."
                        : "Each of them reaches the others through its references, so none of them can be changed, tested or understood alone.",
                    Fix =
                        "Move what they share into a lower unit, or turn one reference around (the lower one owns an interface the higher one implements).",
                    Project = cycle[0],
                    Evidence = evidence,
                    Key = Key("cycle", [.. cycle.Order(StringComparer.Ordinal)]),
                    Measure = cycle.Count,
                }
            );
        }
        var roles = projects.ToDictionary(unit => unit.Path, unit => unit.Role, StringComparer.Ordinal);
        foreach (var unit in projects)
        {
            foreach (var reference in unit.References)
            {
                if (!byPath.TryGetValue(reference, out var target))
                {
                    found.Add(
                        new()
                        {
                            Kind = "missing-project",
                            Level = "warn",
                            Title = unit.Name + " references a " + noun + " that is not there",
                            Why = reference + " does not exist in the repository.",
                            Fix = "Remove the stale reference, or restore what it points to.",
                            Project = unit.Path,
                            Evidence = At(unit, reference),
                            Key = Key("missing-project", unit.Path, reference),
                        }
                    );
                    continue;
                }
                if (target.Kind is "test" && unit.Kind is not "test")
                {
                    found.Add(
                        new()
                        {
                            Kind = "test-reference",
                            Level = "problem",
                            Title = unit.Name + " references the test " + noun + " " + target.Name,
                            Why = "Production code that depends on tests ships test code and its test frameworks.",
                            Fix =
                                "Move what production needs out of the test project; shared test helpers go into a test-support project only tests reference.",
                            Project = unit.Path,
                            Evidence = At(unit, reference),
                            Key = Key("test-reference", unit.Path, reference),
                        }
                    );
                }

                var fromRank = Rank(build.Language, unit.Role);
                var toRank = Rank(build.Language, roles[reference]);
                if (unit.Kind is "library" && fromRank >= 0 && toRank >= 0 && fromRank < toRank)
                {
                    found.Add(
                        new()
                        {
                            Kind = "layering",
                            Level = "warn",
                            Title = $"{unit.Name} ({unit.Role}) references {target.Name} ({roles[reference]})",
                            Why =
                                $"A lower layer depends on a higher one: {unit.Role} should not know {roles[reference]}. Roles are read from the names.",
                            Fix =
                                "Move the used code down to a lower unit, or let the lower unit own an interface the higher one implements.",
                            Project = unit.Path,
                            Evidence = At(unit, reference),
                            Key = Key("layering", unit.Path, reference),
                        }
                    );
                }
            }
            if (unit.Kind is "library" && !unit.ReferencedBy.Any(path => byPath[path].Kind is not "test"))
            {
                var onlyTests = unit.ReferencedBy.Count > 0 ? " except its tests" : "";
                if (unit.Package is null)
                {
                    found.Add(
                        new()
                        {
                            Kind = "unused",
                            Level = "warn",
                            Title = unit.Name + " is not used",
                            Why = "No " + noun + " references it" + onlyTests + ".",
                            Fix = "Delete it, or use it where it was meant to be used.",
                            Project = unit.Path,
                            Evidence = [unit.Path],
                        }
                    );
                }
                else if (used is not null && !used(unit.Package))
                {
                    found.Add(
                        new()
                        {
                            Kind = "unused",
                            Level = "info",
                            Title = unit.Name + " is used by nothing here and no workspace member",
                            Why =
                                "Nothing in the repository references it"
                                + onlyTests
                                + " and no other member references its package "
                                + unit.Package
                                + "; it may still be used outside the workspace.",
                            Fix =
                                "Check whether anything outside the workspace uses its package; if nothing does, it can go.",
                            Project = unit.Path,
                            Evidence = [unit.Path],
                        }
                    );
                }
            }
            if (build.Solutions.Count > 0 && unit.Solutions.Count is 0)
            {
                found.Add(
                    new()
                    {
                        Kind = "no-solution",
                        Level = "warn",
                        Title = unit.Name + " is in no solution",
                        Why =
                            "Building or testing the solution skips it, and so do tools that load the solution (graphify).",
                        Fix = "Add it to the solution, or delete it if it is obsolete.",
                        Project = unit.Path,
                        Evidence = [unit.Path],
                    }
                );
            }
        }
        foreach (var solution in build.Solutions)
        {
            foreach (var missing in solution.Missing ?? [])
            {
                found.Add(
                    new()
                    {
                        Kind = "missing-project",
                        Level = "warn",
                        Title = solution.Path.Split('/')[^1] + " names a " + noun + " that is not there",
                        Why = missing + " does not exist; the solution does not load cleanly.",
                        Fix = "Remove the stale entry from the solution, or restore the project.",
                        Evidence = [solution.Path],
                        Key = Key("missing-project", solution.Path, missing),
                    }
                );
            }
        }

        if (build.Solutions.Count > 1)
        {
            var partial = build
                .Solutions.SelectMany(solution => solution.Projects)
                .GroupBy(path => path, StringComparer.Ordinal)
                .Where(group => group.Count() < build.Solutions.Count)
                .Select(group => byPath.TryGetValue(group.Key, out var unit) ? unit.Name : "")
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (partial.Length > 0)
            {
                found.Add(
                    new()
                    {
                        Kind = "solutions-differ",
                        Level = "info",
                        Title = "The solutions hold different projects",
                        Why =
                            "Only some solutions include "
                            + string.Join(", ", partial)
                            + "; which solution is built decides what is checked.",
                        Fix =
                            "Decide which solution is the one that is built and checked, and keep the others in step (or say why they differ).",
                        Evidence = build.Solutions.Select(solution => solution.Path).ToArray(),
                        Key = Key("solutions-differ"),
                        Measure = partial.Length,
                    }
                );
            }
        }
        var reader = build.Language switch
        {
            "dotnet" => "internal/dotnet",
            "go" => "internal/goarch",
            "typescript" => "internal/tsarch",
            _ => null,
        };
        return found
            .Concat(build.Extra)
            .Select(finding => Complete(finding, Harness(reader)))
            .OrderBy(finding => Severity(finding.Level))
            .ToArray();
    }

    private static (Dictionary<string, int> Components, List<List<string>> Members) Components(
        IReadOnlyList<BuildUnit> units,
        Dictionary<string, BuildUnit> byPath
    )
    {
        Dictionary<string, int> index = new(StringComparer.Ordinal),
            low = new(StringComparer.Ordinal),
            components = new(StringComparer.Ordinal);
        HashSet<string> on = new(StringComparer.Ordinal);
        Stack<string> stack = [];
        List<List<string>> members = [];
        var next = 0;
        void Strong(string path)
        {
            index[path] = low[path] = next++;
            stack.Push(path);
            on.Add(path);
            foreach (var target in byPath[path].References.Where(byPath.ContainsKey))
            {
                if (!index.TryGetValue(target, out var targetIndex))
                {
                    Strong(target);
                    low[path] = Math.Min(low[path], low[target]);
                }
                else if (on.Contains(target))
                {
                    low[path] = Math.Min(low[path], targetIndex);
                }
            }
            if (low[path] != index[path])
            {
                return;
            }

            List<string> component = [];
            string member;
            do
            {
                member = stack.Pop();
                on.Remove(member);
                components[member] = members.Count;
                component.Add(member);
            } while (member != path);
            component.Sort(StringComparer.Ordinal);
            members.Add(component);
        }
        foreach (var unit in units.Where(unit => !index.ContainsKey(unit.Path)))
        {
            Strong(unit.Path);
        }

        return (components, members);
    }

    private static readonly (string Name, string[] Words)[] DotnetRoles =
    [
        (
            "model",
            [
                "models",
                "model",
                "contracts",
                "contract",
                "domain",
                "entities",
                "entity",
                "dto",
                "dtos",
                "abstractions",
                "enums",
            ]
        ),
        (
            "data",
            [
                "dal",
                "data",
                "dataaccess",
                "persistence",
                "repositories",
                "repository",
                "infrastructure",
                "entityframeworkcore",
                "migrations",
            ]
        ),
        ("logic", ["bl", "bll", "business", "businesslogic", "services", "application", "logic"]),
        ("api", ["webapi", "api", "web", "host", "server", "worker", "functions"]),
    ];
    private static readonly (string Name, string[] Words)[] GoRoles =
    [
        (
            "base",
            ["util", "utils", "types", "model", "models", "errors", "log", "logging", "config", "buildinfo", "version"]
        ),
        ("data", ["storage", "store", "db", "database", "repository", "repo", "sqlite", "postgres", "cache"]),
        ("logic", ["service", "services", "domain", "core", "app", "usecase", "usecases"]),
        ("api", ["server", "api", "http", "handler", "handlers", "gui", "web", "cli", "grpc", "rest"]),
    ];
    private static readonly (string Name, string[] Words)[] TypeScriptRoles =
    [
        (
            "base",
            [
                "types",
                "typings",
                "models",
                "model",
                "constants",
                "utils",
                "util",
                "lib",
                "libs",
                "helpers",
                "config",
                "assets",
                "theme",
                "styles",
                "i18n",
                "locales",
                "core",
                "shared",
                "common",
            ]
        ),
        (
            "data",
            [
                "api",
                "apis",
                "services",
                "service",
                "store",
                "stores",
                "state",
                "queries",
                "actions",
                "context",
                "contexts",
                "providers",
                "redux",
                "slices",
                "clients",
                "client",
            ]
        ),
        ("hooks", ["hooks"]),
        ("ui", ["components", "component", "ui", "layouts", "layout", "widgets", "icons", "form", "forms"]),
        ("feature", ["features", "feature", "modules", "module", "containers", "domains"]),
        ("page", ["pages", "routes", "routing", "router", "views", "screens", "app"]),
    ];

    private static (string Name, string[] Words)[] Roles(string language) =>
        language switch
        {
            "dotnet" => DotnetRoles,
            "go" => GoRoles,
            "typescript" => TypeScriptRoles,
            _ => [],
        };

    private static int Rank(string language, string? role) =>
        Array.FindIndex(Roles(language), item => item.Name == role);

    private static string? RoleOf(string language, BuildUnit unit)
    {
        if (unit.Kind is "test")
        {
            return "test";
        }

        var words = language switch
        {
            "dotnet" => unit.Name.Split('.'),
            "go" => unit.Path.Split('/'),
            "typescript" => unit.Name.Split(['/', '@', '-', '.'], StringSplitOptions.RemoveEmptyEntries),
            _ => [],
        };
        foreach (var word in words.Reverse())
        {
            var role = Roles(language)
                .FirstOrDefault(role => role.Words.Contains(word, StringComparer.OrdinalIgnoreCase));
            if (role.Name is not null)
            {
                return role.Name;
            }
        }
        return null;
    }
}
