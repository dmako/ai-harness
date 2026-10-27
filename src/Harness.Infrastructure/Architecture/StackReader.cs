using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Architecture;
using Harness.Core.Retrieval;
using Harness.Infrastructure.Workspaces;

namespace Harness.Infrastructure.Architecture;

public sealed partial class StackReader(AnalysisRepository repository, EcosystemReader ecosystem)
{
    [GeneratedRegex(@"\d+(?:\.\d+){0,2}", EcosystemReader.RxOptions, 1000)]
    private static partial Regex Number { get; }

    [GeneratedRegex("<Project\\s+Sdk=\"([^\"]+)\"", EcosystemReader.RxOptions, 1000)]
    private static partial Regex SdkAttribute { get; }

    [GeneratedRegex(@"^\s*ARG\s+(\w+)\s*=\s*(\S+)", EcosystemReader.RxOptions, 1000)]
    private static partial Regex DockerArgument { get; }

    [GeneratedRegex(@"^\s*FROM\s+(?:--\S+\s+)?node:(\S+)", EcosystemReader.RxOptions, 1000)]
    private static partial Regex DockerFromNode { get; }

    [GeneratedRegex(@"\$\{([^}]+)\}|\$([A-Za-z_]\w*)", EcosystemReader.RxOptions, 1000)]
    private static partial Regex EnvironmentVariable { get; }

    private static readonly (string Package, string Name)[] DotnetFrameworks =
    [
        ("Microsoft.AspNetCore", "ASP.NET Core"),
        ("Microsoft.EntityFrameworkCore", "EF Core"),
        ("Grpc.AspNetCore", "gRPC"),
        ("protobuf-net.Grpc", "gRPC (protobuf-net)"),
        ("MassTransit", "MassTransit"),
        ("MediatR", "MediatR"),
        ("Microsoft.Maui", "MAUI"),
        ("Avalonia", "Avalonia"),
    ];
    private static readonly (string Package, string Name)[] JsFrameworks =
    [
        ("next", "Next.js"),
        ("react", "React"),
        ("nuxt", "Nuxt"),
        ("vue", "Vue"),
        ("@angular/core", "Angular"),
        ("svelte", "Svelte"),
        ("solid-js", "Solid"),
        ("@nestjs/core", "NestJS"),
        ("express", "Express"),
        ("fastify", "Fastify"),
        ("react-native", "React Native"),
        ("vite", "Vite"),
    ];
    private static readonly (string Package, string Name)[] GoFrameworks =
    [
        ("github.com/gin-gonic/gin", "Gin"),
        ("github.com/labstack/echo", "Echo"),
        ("github.com/go-chi/chi", "chi"),
        ("github.com/gofiber/fiber", "Fiber"),
        ("google.golang.org/grpc", "gRPC"),
        ("github.com/spf13/cobra", "Cobra"),
    ];

    public ProjectStack? Read()
    {
        var files = repository.Files.Where(file => !Skipped(file)).ToArray();
        string[] languages = ["C#", "Go", "TypeScript", "JavaScript", "Dart"];
        var counts = languages
            .Select(language =>
                (Language: language, Count: files.Count(file => Language(Path.GetExtension(file)) == language))
            )
            .ToArray();
        var best = counts.OrderByDescending(row => row.Count).First();
        if (best.Count is 0)
        {
            return null;
        }

        var stack = best.Language switch
        {
            "C#" => Dotnet(files),
            "Go" => Go(),
            "Dart" => Dart(),
            _ => Node(files, best.Language),
        };
        return stack with { Badge = Badge(stack) };
    }

    /// <summary>
    /// A project's target framework, where it is set, and its host rank (2 web/worker, 1 exe, 0 library).
    /// </summary>
    private readonly record struct DotnetTarget(string Framework, string At, int Host);

    [GeneratedRegex(@"^net(\d+)\.(\d+)$", EcosystemReader.RxOptions, 1000)]
    private static partial Regex NetFramework { get; }

    [GeneratedRegex(@"^netstandard(\d+\.\d+)$", EcosystemReader.RxOptions, 1000)]
    private static partial Regex NetStandard { get; }

    private ProjectStack Dotnet(IReadOnlyList<string> files)
    {
        List<DotnetTarget> targets = [];
        Dictionary<string, (string Version, string At)> packages = new(StringComparer.Ordinal);
        foreach (
            var file in files.Where(file =>
                Path.GetExtension(file) is ".csproj"
                && !TaskParsing.IsTestFile(file)
                && !file.Contains("example", StringComparison.OrdinalIgnoreCase)
                && !ecosystem.DotnetTest(file)
            )
        )
        {
            ReadProject(file, targets, packages);
        }

        var runtime = DotnetRuntime(targets);
        return new("C#", runtime, DotnetFrameworksOf(packages, runtime), "");
    }

    /// <summary>
    /// Adds the project's target frameworks with its host rank, and its packages (a version found later
    /// fills an empty one).
    /// </summary>
    private void ReadProject(
        string file,
        List<DotnetTarget> targets,
        Dictionary<string, (string Version, string At)> packages
    )
    {
        var host = WebOrWorker(file) ? 2 : 0;
        if (host is 2)
        {
            packages["Microsoft.AspNetCore"] = ("", "");
        }

        var evaluated = ecosystem.Build.Evaluate(file);
        
        if (
            host is 0
            && (evaluated.PropertyLocations.GetValueOrDefault("OutputType") is string outputType
                && (outputType.Equals("exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("winexe", StringComparison.OrdinalIgnoreCase))
            )
        )
        {
            host = 1;
        }

        var at =
            evaluated.PropertyLocations.GetValueOrDefault("TargetFrameworks")
            ?? evaluated.PropertyLocations.GetValueOrDefault("TargetFramework", "");
        targets.AddRange(evaluated.Frameworks.Select(framework => new DotnetTarget(framework, at, host)));
        foreach (var package in evaluated.Packages)
        {
            if (!packages.TryGetValue(package.Name, out var old) || old.Version.Length is 0)
            {
                packages[package.Name] = (package.Unresolved ? "" : package.Version, package.At);
            }
        }
    }

    private bool WebOrWorker(string file) =>
        AnalysisRepository
            .Lines(repository.Read(file))
            .Select(line => SdkAttribute.Match(line).Groups[1].Value)
            .Any(name =>
                name.EndsWith(".Web", StringComparison.Ordinal) || name.EndsWith(".Worker", StringComparison.Ordinal)
            );

    /// <summary>The runtime of the highest host rank that targets one: the highest .NET, else .NET Standard.</summary>
    private static StackPart? DotnetRuntime(List<DotnetTarget> targets)
    {
        for (var rank = 2; rank >= 0; rank--)
        {
            if (RankRuntime(targets, rank) is { } runtime)
            {
                return runtime;
            }
        }
        return null;
    }

    /// <summary>
    /// The highest .NET version among the targets of at least this host rank; the first .NET Standard
    /// one when none is .NET.
    /// </summary>
    private static StackPart? RankRuntime(List<DotnetTarget> targets, int rank)
    {
        StackPart? runtime = null;
        var highest = -1d;
        foreach (var target in targets.Where(target => target.Host >= rank))
        {
            var match = NetFramework.Match(target.Framework);
            if (match.Success)
            {
                var version = match.Groups[1].Value + "." + match.Groups[2].Value;
                var numeric = double.Parse(version, CultureInfo.InvariantCulture);
                if (numeric > highest)
                {
                    highest = numeric;
                    runtime = Part(
                        ".NET",
                        version,
                        match.Groups[2].Value is "0" ? match.Groups[1].Value : version,
                        target.At
                    );
                }
            }
            else if (NetStandard.Match(target.Framework) is { Success: true } std && runtime is null)
            {
                runtime = Part(".NET Standard", std.Groups[1].Value, std.Groups[1].Value, target.At);
            }
        }
        return runtime;
    }

    /// <summary>
    /// Each known framework found among the packages (exact name first, then by prefix); ASP.NET Core
    /// takes the runtime's version.
    /// </summary>
    private static List<StackPart> DotnetFrameworksOf(
        Dictionary<string, (string Version, string At)> packages,
        StackPart? runtime
    )
    {
        List<StackPart> frameworks = [];
        foreach (var framework in DotnetFrameworks)
        {
            var name = packages
                .Keys.OrderByDescending(name => name == framework.Package)
                .ThenBy(name => name, WorkspaceNameComparer.Instance)
                .FirstOrDefault(name =>
                    name == framework.Package || name.StartsWith(framework.Package + ".", StringComparison.Ordinal)
                );
            if (name is null)
            {
                continue;
            }

            var value = packages[name];
            var version =
                framework.Name is "ASP.NET Core" && runtime is not null ? runtime.Version ?? "" : value.Version;
            frameworks.Add(Part(framework.Name, version, Major(version), value.At));
        }
        return frameworks;
    }

    private ProjectStack Node(IReadOnlyList<string> files, string language)
    {
        using var document = ecosystem.Json("package.json");
        var root = document?.RootElement ?? default;
        if (ObjectHas(root, "devDependencies", "typescript") || files.Contains("tsconfig.json", StringComparer.Ordinal))
        {
            language = "TypeScript";
        }

        var dependencies = ecosystem.Dependencies("package.json");
        var frameworks = JsFrameworks
            .Select(framework =>
                (Framework: framework, Dependency: dependencies.FirstOrDefault(d => d.Name == framework.Package))
            )
            .Where(row => row.Dependency is not null)
            .Select(row =>
                Part(
                    row.Framework.Name,
                    row.Dependency!.Version,
                    Major(row.Dependency.Version),
                    row.Dependency.Path + ":" + row.Dependency.Line.ToString(CultureInfo.InvariantCulture)
                )
            )
            .ToList();
        if (frameworks.FirstOrDefault()?.Name is "Next.js")
        {
            frameworks.RemoveAll(framework => framework.Name is "React");
        }

        StackPart? runtime = null;
        var engine = ObjectString(root, "engines", "node");
        if (Number.Match(engine) is { Success: true } number)
        {
            runtime = Part("Node", engine, number.Value.Split('.')[0], LineOf("package.json", "\"node\""));
        }

        foreach (var file in new[] { ".nvmrc", ".node-version" })
        {
            if (runtime is not null || !files.Contains(file, StringComparer.Ordinal))
            {
                continue;
            }

            var value = repository.Read(file).Trim().TrimStart('v').Trim();
            if (Number.IsMatch(value) && !value.Contains("lts", StringComparison.Ordinal))
            {
                runtime = Part("Node", value, Major(value), file);
            }
        }
        foreach (
            var file in files.Where(file =>
                Path.GetFileName(file).StartsWith("dockerfile", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            if (runtime is not null)
            {
                break;
            }

            Dictionary<string, string> arguments = new(StringComparer.Ordinal);
            foreach (
                var (line, index) in AnalysisRepository
                    .Lines(repository.Read(file))
                    .Select((line, index) => (line, index + 1))
            )
            {
                var arg = DockerArgument.Match(line);
                if (arg.Success)
                {
                    arguments[arg.Groups[1].Value] = arg.Groups[2].Value;
                }

                var from = DockerFromNode.Match(line);
                if (!from.Success)
                {
                    continue;
                }

                var tag = EnvironmentVariable.Replace(
                    from.Groups[1].Value,
                    match =>
                        arguments.GetValueOrDefault(
                            match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value,
                            ""
                        )
                );
                var version = Number.Match(tag).Value;
                if (version.Length > 0)
                {
                    runtime = Part(
                        "Node",
                        version,
                        Major(version),
                        file + ":" + index.ToString(CultureInfo.InvariantCulture)
                    );
                    break;
                }
            }
        }
        return new(language, runtime, frameworks, "");
    }

    private ProjectStack Go()
    {
        StackPart? runtime = null;
        foreach (
            var (line, index) in AnalysisRepository
                .Lines(repository.Read("go.mod"))
                .Select((line, index) => (line.Trim(), index + 1))
        )
        {
            if (runtime is null && line.StartsWith("go ", StringComparison.Ordinal))
            {
                var version = line[3..].Trim();
                runtime = Part(
                    "Go",
                    version,
                    string.Join('.', version.Split('.').Take(2)),
                    "go.mod:" + index.ToString(CultureInfo.InvariantCulture)
                );
            }
        }

        var dependencies = ecosystem.Dependencies("go.mod");
        var frameworks = GoFrameworks
            .SelectMany(framework =>
                dependencies
                    .Where(dependency => dependency.Name.StartsWith(framework.Package, StringComparison.Ordinal))
                    .Select(dependency =>
                        Part(
                            framework.Name,
                            dependency.Version,
                            Major(dependency.Version),
                            dependency.Path + ":" + dependency.Line.ToString(CultureInfo.InvariantCulture)
                        )
                    )
            )
            .ToArray();
        return new("Go", runtime, frameworks, "");
    }

    private ProjectStack Dart()
    {
        StackPart? runtime = null;
        List<StackPart> frameworks = [];
        var section = "";
        foreach (
            var (line, index) in AnalysisRepository
                .Lines(repository.Read("pubspec.yaml"))
                .Select((line, index) => (line, index + 1))
        )
        {
            var trimmed = line.Trim();
            if (!line.StartsWith(' ') && trimmed.EndsWith(':'))
            {
                section = trimmed[..^1];
            }

            if (
                section is "environment"
                && trimmed.StartsWith("sdk:", StringComparison.Ordinal)
                && Number.IsMatch(trimmed)
            )
            {
                runtime = Part(
                    "Dart",
                    trimmed[4..].Trim(' ', '"', '\''),
                    Major(trimmed),
                    "pubspec.yaml:" + index.ToString(CultureInfo.InvariantCulture)
                );
            }

            if (section is "environment" && trimmed.StartsWith("flutter:", StringComparison.Ordinal))
            {
                frameworks.Add(
                    Part(
                        "Flutter",
                        trimmed[8..].Trim(' ', '"', '\''),
                        Major(trimmed),
                        "pubspec.yaml:" + index.ToString(CultureInfo.InvariantCulture)
                    )
                );
            }

            if (section is "dependencies" && trimmed is "flutter:" && frameworks.Count is 0)
            {
                frameworks.Add(Part("Flutter", "", "", "pubspec.yaml:" + index.ToString(CultureInfo.InvariantCulture)));
            }
        }
        return new("Dart", runtime, frameworks, "");
    }

    private string LineOf(string file, string needle)
    {
        var index = Array.FindIndex(
            AnalysisRepository.Lines(repository.Read(file)),
            line => line.Contains(needle, StringComparison.Ordinal)
        );
        return index < 0 ? file : file + ":" + (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static bool ObjectHas(JsonElement root, string section, string key) =>
        root.ValueKind is JsonValueKind.Object
        && root.TryGetProperty(section, out var value)
        && value.ValueKind is JsonValueKind.Object
        && value.TryGetProperty(key, out _);

    private static string ObjectString(JsonElement root, string section, string key) =>
        root.ValueKind is JsonValueKind.Object
        && root.TryGetProperty(section, out var value)
        && value.ValueKind is JsonValueKind.Object
        && value.TryGetProperty(key, out var item)
        && item.ValueKind is JsonValueKind.String
            ? item.GetString()!
            : "";

    private static StackPart Part(string name, string version, string major, string at) =>
        new(name, Empty(version), Empty(major), Empty(at));

    private static string? Empty(string value) => value.Length is 0 ? null : value;

    private static string Major(string value) => Number.Match(value).Value.Split('.')[0];

    private static string Language(string extension) =>
        extension switch
        {
            ".cs" => "C#",
            ".go" => "Go",
            ".ts" or ".tsx" => "TypeScript",
            ".js" or ".jsx" or ".mjs" => "JavaScript",
            ".dart" => "Dart",
            _ => "",
        };

    private static bool Skipped(string file) =>
        file.Split('/')
            .Any(part =>
                part is "node_modules" or "bin" or "obj" or "dist" or "build" or "vendor"
                || part.StartsWith('.') && part.Length > 1 && part is not (".nvmrc" or ".node-version")
            );

    private static string Badge(ProjectStack stack)
    {
        if (stack.Language is "Go" && stack.Runtime is { } go)
        {
            return "Go " + go.Major;
        }

        List<string> parts = [stack.Language];
        var framework = stack.Frameworks.FirstOrDefault(f =>
            f.Name is not ("Vite" or "EF Core" or "ASP.NET Core" or "MediatR")
            && !f.Name.StartsWith("gRPC", StringComparison.Ordinal)
        );
        if (framework is not null)
        {
            parts.Add((framework.Name + " " + framework.Major).Trim());
        }

        if (stack.Runtime is { } runtime)
        {
            if (runtime.Name == stack.Language)
            {
                parts[0] = (stack.Language + " " + runtime.Major).Trim();
            }
            else
            {
                parts.Add((runtime.Name + " " + runtime.Major).Trim());
            }
        }
        return string.Join(" · ", parts);
    }
}
