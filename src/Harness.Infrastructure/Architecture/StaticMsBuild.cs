using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Harness.Core.Architecture;

namespace Harness.Infrastructure.Architecture;

/// <summary>Bounded property/item evaluation; never loads MSBuild or executes project code.</summary>
public sealed partial class StaticMsBuild(AnalysisRepository repository)
{
    [GeneratedRegex(@"\$\(([A-Za-z_][\w.]*)\)", EcosystemReader.RxOptions, 1000)]
    private static partial Regex Property { get; }

    [GeneratedRegex(
        @"(?i)^\$\(\[MSBuild\]::GetPathOfFileAbove\(\s*'?([^',)]+)'?\s*(?:,\s*'?([^')]*)'?\s*)?\)\)$",
        EcosystemReader.RxOptions,
        1000
    )]
    private static partial Regex Above { get; }

    private readonly Dictionary<string, XElement?> _xml = new(StringComparer.Ordinal);

    private sealed class Context
    {
        internal Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string> Locations { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<(string File, XElement Root)> Order { get; } = [];
        internal HashSet<string> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
    }

    public EvaluatedProject Evaluate(string project)
    {
        if (repository.Evaluations.TryGetValue(project, out var cached))
        {
            return cached;
        }

        var first = Properties(project, "");
        var frameworks = Split(first.Properties.GetValueOrDefault("TargetFrameworks", ""));
        if (frameworks.Length is 0)
        {
            frameworks = Split(first.Properties.GetValueOrDefault("TargetFramework", ""));
        }

        var contexts = frameworks.Length is 0
            ? new[] { first }
            : frameworks.Select(framework => Properties(project, framework)).ToArray();
        List<EvaluatedPackage> packages = [];
        List<EvaluatedReference> references = [];
        for (var i = 0; i < contexts.Length; i++)
        {
            var (ps, rs) = Items(project, contexts[i]);
            var framework = frameworks.Length is 0 ? "" : frameworks[i];
            foreach (var item in ps)
            {
                var existing = packages.FindIndex(p =>
                    p.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase) && p.Version == item.Version
                );
                if (existing < 0)
                {
                    packages.Add(item with { Frameworks = Add([], framework) });
                }
                else
                {
                    packages[existing] = packages[existing] with
                    {
                        Frameworks = Add(packages[existing].Frameworks, framework),
                        Conditional = packages[existing].Conditional || item.Conditional,
                    };
                }
            }
            foreach (var item in rs)
            {
                var existing = references.FindIndex(r => r.Path == item.Path);
                if (existing < 0)
                {
                    references.Add(item with { Frameworks = Add([], framework) });
                }
                else
                {
                    references[existing] = references[existing] with
                    {
                        Frameworks = Add(references[existing].Frameworks, framework),
                        Conditional = references[existing].Conditional || item.Conditional,
                    };
                }
            }
        }
        var result = new EvaluatedProject(
            frameworks,
            contexts[0].Properties,
            contexts[0].Locations,
            packages.Select(p => p with { Frameworks = All(p.Frameworks) }).ToArray(),
            references.Select(r => r with { Frameworks = All(r.Frameworks) }).ToArray()
        );
        repository.Evaluations.Add(project, result);
        return result;
        IReadOnlyList<string> All(IReadOnlyList<string> fws) =>
            frameworks.Length <= 1 || fws.Count == frameworks.Length ? [] : fws;
    }

    private Context Properties(string project, string framework)
    {
        var context = new Context();
        var projectPath = Path.GetFullPath(Path.Combine(repository.Root, project));
        var directory = Path.GetDirectoryName(projectPath)!;
        context.Properties["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(project);
        context.Properties["MSBuildProjectDirectory"] = directory;
        context.Properties["MSBuildProjectFile"] = Path.GetFileName(project);
        context.Properties["Configuration"] = "Debug";
        context.Properties["Platform"] = "AnyCPU";
        if (framework.Length > 0)
        {
            context.Properties["TargetFramework"] = framework;
            context.Globals.Add("TargetFramework");
            FrameworkProperties(framework, context.Properties);
        }
        foreach (
            var file in new[]
            {
                Nearest("Directory.Build.props", directory),
                Nearest("Directory.Packages.props", directory),
                project,
                Nearest("Directory.Build.targets", directory),
            }
        )
        {
            if (file.Length > 0)
            {
                Walk(context, file, 0);
            }
        }

        return context;
    }

    /// <summary>Reads a file's properties and follows its imports, at most 8 deep and each file once.</summary>
    private void Walk(Context context, string file, int depth)
    {
        if (depth > 8 || !context.Seen.Add(file) || Parse(file) is not { } xml)
        {
            return;
        }

        context.Order.Add((file, xml));
        var dir = Path.GetDirectoryName(Path.Combine(repository.Root, file))!;
        foreach (var element in xml.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "PropertyGroup":
                    PropertyGroup(context, element, file, dir);
                    break;
                case "Import":
                    Import(context, element, dir, depth, checkCondition: true);
                    break;
                case "ImportGroup":
                    ImportGroup(context, element, dir, depth);
                    break;
            }
        }
    }

    /// <summary>
    /// Sets the group's properties, except global ones and, under an unknown condition, ones already set.
    /// </summary>
    private void PropertyGroup(Context context, XElement group, string file, string dir)
    {
        if (!Condition(context, Attr(group, "Condition"), dir).Value)
        {
            return;
        }

        foreach (var property in group.Elements())
        {
            var (value, known) = Condition(context, Attr(property, "Condition"), dir);
            var name = property.Name.LocalName;
            if (
                !value && known
                || context.Globals.Contains(name)
                || !known && context.Properties.GetValueOrDefault(name, "").Length > 0
            )
            {
                continue;
            }

            context.Properties[name] = Expand(context, property.Value.Trim(), dir);
            context.Locations[name] =
                file + ":" + ((IXmlLineInfo)property).LineNumber.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The group's imports, whose own conditions are not checked.</summary>
    private void ImportGroup(Context context, XElement group, string dir, int depth)
    {
        var condition = Condition(context, Attr(group, "Condition"), dir);
        if (!condition.Value && condition.Known)
        {
            return;
        }

        foreach (var import in group.Elements().Where(e => e.Name.LocalName is "Import"))
        {
            Import(context, import, dir, depth, checkCondition: false);
        }
    }

    private void Import(Context context, XElement element, string dir, int depth, bool checkCondition)
    {
        var condition = Condition(context, Attr(element, "Condition"), dir);
        if (checkCondition && !condition.Value && condition.Known)
        {
            return;
        }

        var path = ImportPath(context, Attr(element, "Project"), dir);
        if (path.Length > 0)
        {
            Walk(context, path, depth + 1);
        }
    }

    private (List<EvaluatedPackage> Packages, List<EvaluatedReference> References) Items(
        string project,
        Context context
    )
    {
        var items = new ItemLists();
        foreach (var (file, xml) in context.Order)
        {
            var directory = Path.GetDirectoryName(Path.Combine(repository.Root, file))!;
            foreach (var group in xml.Elements().Where(e => e.Name.LocalName is "ItemGroup"))
            {
                var condition = Condition(context, Attr(group, "Condition"), directory);
                if (!condition.Value && condition.Known)
                {
                    continue;
                }

                foreach (var item in group.Elements())
                {
                    Item(project, context, items, file, item, condition.Known);
                }
            }
        }
        return (items.Packages.Select(package => Centrally(package, items.Central)).ToList(), items.References);
    }

    private sealed class ItemLists
    {
        internal List<EvaluatedPackage> Packages { get; } = [];
        internal List<EvaluatedReference> References { get; } = [];
        internal Dictionary<string, string> Central { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One item of a group whose condition is true or unknown (then the item is conditional).</summary>
    private void Item(string project, Context context, ItemLists items, string file, XElement item, bool known)
    {
        var directory = Path.GetDirectoryName(Path.Combine(repository.Root, file))!;
        var childCondition = Condition(context, Attr(item, "Condition"), directory);
        if (!childCondition.Value && childCondition.Known)
        {
            return;
        }

        var conditional = !known || !childCondition.Known;
        var at = file + ":" + ((IXmlLineInfo)item).LineNumber.ToString(CultureInfo.InvariantCulture);
        var include = Expand(context, Attr(item, "Include"), directory);
        var update = Expand(context, Attr(item, "Update"), directory);
        var remove = Expand(context, Attr(item, "Remove"), directory);
        var version = ItemVersion(context, item, directory);
        switch (item.Name.LocalName)
        {
            case "PackageVersion":
                PackageVersion(items.Central, include, update, version);
                break;
            case "PackageReference":
                PackageReference(items.Packages, include, update, remove, new(include, version, at, [], conditional));
                break;
            case "ProjectReference":
                items.References.AddRange(ProjectReferences(project, include, at, conditional));
                break;
        }
    }

    /// <summary>The Version (or VersionOverride) attribute, overridden by a Version child element.</summary>
    private static string ItemVersion(Context context, XElement item, string directory)
    {
        var value = Attr(item, "Version");
        if (value.Length is 0)
        {
            value = Attr(item, "VersionOverride");
        }

        foreach (var element in item.Elements().Where(e => e.Name.LocalName is "Version" or "VersionOverride"))
        {
            value = element.Value.Trim();
        }

        return Expand(context, value, directory);
    }

    private static void PackageVersion(
        Dictionary<string, string> central,
        string include,
        string update,
        string version
    )
    {
        if (include.Length > 0 || update.Length > 0)
        {
            central[include.Length > 0 ? include : update] = version;
        }
    }

    /// <summary>Updates the version of, removes, or adds the packages; added is the item as read.</summary>
    private static void PackageReference(
        List<EvaluatedPackage> packages,
        string include,
        string update,
        string remove,
        EvaluatedPackage added
    )
    {
        if (update.Length > 0)
        {
            for (var i = 0; i < packages.Count; i++)
            {
                if (packages[i].Name.Equals(update, StringComparison.OrdinalIgnoreCase) && added.Version.Length > 0)
                {
                    packages[i] = packages[i] with { Version = added.Version };
                }
            }
        }
        else if (remove.Length > 0)
        {
            packages.RemoveAll(package => package.Name.Equals(remove, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            packages.AddRange(Split(include).Select(name => added with { Name = name }));
        }
    }

    private IEnumerable<EvaluatedReference> ProjectReferences(
        string project,
        string include,
        string at,
        bool conditional
    ) =>
        Split(include.Replace('\\', '/'))
            .Select(path =>
                new EvaluatedReference(
                    repository.Relative(
                        Path.GetFullPath(path, Path.GetDirectoryName(Path.Combine(repository.Root, project))!)
                    ),
                    at,
                    [],
                    conditional
                )
            );

    /// <summary>A package without a version takes the central one; a version still holding $( is unresolved.</summary>
    private static EvaluatedPackage Centrally(EvaluatedPackage package, Dictionary<string, string> central)
    {
        var version = (
            package.Version.Length is 0 ? central.GetValueOrDefault(package.Name, "") : package.Version
        ).Trim();
        return package with
        {
            Version = version,
            Unresolved = version.Contains("$(", StringComparison.Ordinal),
        };
    }

    internal XElement? Parse(string file)
    {
        if (_xml.TryGetValue(file, out var parsed))
        {
            return parsed;
        }

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(repository.Read(file).TrimStart('\uFEFF')),
                new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }
            );
            parsed = XElement.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            parsed = null;
        }
        _xml.Add(file, parsed);
        return parsed;
    }

    private string Nearest(string name, string directory)
    {
        while (directory == repository.Root || repository.Within(directory))
        {
            var relative = repository.Relative(Path.Combine(directory, name));
            if (repository.Exists(relative))
            {
                return relative;
            }

            if (directory == repository.Root)
            {
                break;
            }

            directory = Path.GetDirectoryName(directory)!;
        }
        return "";
    }

    private string ImportPath(Context context, string path, string directory)
    {
        var above = Above.Match(path.Trim());
        if (above.Success)
        {
            var start = above.Groups[2].Length is 0
                ? directory
                : Path.GetFullPath(Expand(context, above.Groups[2].Value, directory));
            return Nearest(above.Groups[1].Value, start);
        }
        path = Expand(context, path, directory).Replace('\\', Path.DirectorySeparatorChar);
        if (path.Length is 0 || path.Contains("$(", StringComparison.Ordinal) || path.IndexOfAny(['*', '?']) >= 0)
        {
            return "";
        }

        var relative = repository.Relative(Path.GetFullPath(path, directory));
        return repository.Exists(relative) ? relative : "";
    }

    private static string Expand(Context context, string value, string directory)
    {
        for (var i = 0; i < 6 && value.Contains("$(", StringComparison.Ordinal); i++)
        {
            var next = Property.Replace(
                value,
                match =>
                    match.Groups[1].Value.ToUpperInvariant() switch
                    {
                        "MSBUILDTHISFILEDIRECTORY" => directory + Path.DirectorySeparatorChar,
                        "MSBUILDTHISFILE" => "",
                        var name => context.Properties.GetValueOrDefault(name) ?? match.Value,
                    }
            );
            if (next == value)
            {
                break;
            }

            value = next;
        }
        return value;
    }

    private static void FrameworkProperties(string framework, Dictionary<string, string> properties)
    {
        var tf = framework.ToUpperInvariant();
        var dash = tf.IndexOf('-', StringComparison.Ordinal);
        if (dash > 0)
        {
            properties["TargetPlatformIdentifier"] = framework[(dash + 1)..];
            tf = tf[..dash];
        }
        var (identifier, version) = tf switch
        {
            var value when value.StartsWith("NETSTANDARD", StringComparison.Ordinal) => (".NETStandard", value[11..]),
            var value when value.StartsWith("NETCOREAPP", StringComparison.Ordinal) => (".NETCoreApp", value[10..]),
            var value
                when value.StartsWith("NET", StringComparison.Ordinal)
                    && value.Contains('.', StringComparison.Ordinal) => (".NETCoreApp", value[3..]),
            var value when value.StartsWith("NET", StringComparison.Ordinal) && value.Length >= 5 => (
                ".NETFramework",
                value[3..4] + "." + value[4..]
            ),
            _ => ("", ""),
        };
        if (identifier.Length > 0)
        {
            properties["TargetFrameworkIdentifier"] = identifier;
            properties["TargetFrameworkVersion"] = "v" + version;
        }
    }

    private static IReadOnlyList<string> Add(IReadOnlyList<string> values, string value) =>
        value.Length is 0 || values.Contains(value, StringComparer.Ordinal) ? values : [.. values, value];

    private static string[] Split(string value) =>
        value
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.Contains("$(", StringComparison.Ordinal))
            .ToArray();

    private static string Attr(XElement element, string name) =>
        element
            .Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value
        ?? "";
}
