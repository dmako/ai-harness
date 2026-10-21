using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Harness.Infrastructure.Retrieval;

namespace Harness.Infrastructure.Workspaces;

/// <summary>Reads package identity without loading MSBuild, SDKs, targets or user code.</summary>
internal sealed class PackageIdentityReader(string root, RetrievalFileReader reader)
{
    private static readonly Regex Property = new(@"\$\(([A-Za-z_][\w.]*)\)", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private static readonly Regex Comparison = new("\\A['\"]([^'\"]*)['\"]\\s*(==|!=)\\s*['\"]([^'\"]*)['\"]\\z", RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
    private readonly HashSet<string> _seen = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);
    private bool _uncertain;

    internal async Task<string> ReadAsync(string project, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(project)!;
        _properties["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(project);
        _properties["MSBuildProjectDirectory"] = directory;
        _properties["MSBuildProjectFile"] = Path.GetFileName(project);
        _properties["Configuration"] = "Debug";
        _properties["Platform"] = "AnyCPU";
        foreach (var name in new[] { "Directory.Build.props", "Directory.Packages.props" })
            if (Nearest(directory, name) is { } file)
                await WalkAsync(file, 0, token).ConfigureAwait(false);
        await WalkAsync(project, 0, token).ConfigureAwait(false);
        if (Nearest(directory, "Directory.Build.targets") is { } targets)
            await WalkAsync(targets, 0, token).ConfigureAwait(false);
        var identity = new[] { "PackageId", "AssemblyName", "MSBuildProjectName" }.Select(name => _properties.GetValueOrDefault(name)).First(value => !string.IsNullOrWhiteSpace(value))!;
        return _uncertain || identity.Contains("$(", StringComparison.Ordinal) ? "" : identity;
    }
    private string? Nearest(string directory, string name)
    {
        while (Within(directory))
        {
            var file = Path.Combine(directory, name);
            if (File.Exists(file))
                return file;
            directory = Path.GetDirectoryName(directory) ?? "";
        }
        return null;
    }
    private bool Within(string path) => path.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private async Task WalkAsync(string file, int depth, CancellationToken token)
    {
        if (!_seen.Add(file))
            return;
        if (depth > 8 || !Within(file))
        { _uncertain = true; return; }
        var text = await reader.ReadBodyAsync(Path.GetRelativePath(root, file), 1, int.MaxValue, token).ConfigureAwait(false);
        using var xml = XmlReader.Create(new StringReader(text), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(xml);
        var directory = Path.GetDirectoryName(file)!;
        foreach (var element in document.Root?.Elements() ?? [])
        {
            var allowed = Condition(element, directory);
            if (allowed is false)
                continue;
            switch (element.Name.LocalName)
            {
                case "PropertyGroup":
                    foreach (var property in element.Elements())
                    {
                        var set = Condition(property, directory);
                        if (set is false)
                            continue;
                        if (allowed is null || set is null)
                        { _uncertain = true; continue; }
                        _properties[property.Name.LocalName] = Expand(property.Value.Trim(), directory);
                    }
                    break;
                case "Import":
                    if (allowed is null)
                    { _uncertain = true; break; }
                    var path = Expand(element.Attribute("Project")?.Value ?? "", directory).Replace('\\', Path.DirectorySeparatorChar);
                    if (path.Length is 0 || path.Contains("$(", StringComparison.Ordinal) || path.IndexOfAny(['*', '?']) >= 0)
                    { _uncertain = true; break; }
                    path = Path.GetFullPath(path, directory);
                    if (File.Exists(path))
                        await WalkAsync(path, depth + 1, token).ConfigureAwait(false);
                    break;
                case "ImportGroup" or "Choose":
                    _uncertain = true;
                    break;
            }
        }
    }
    private string Expand(string value, string directory)
    {
        for (var iteration = 0; iteration < 6; iteration++)
        {
            var expanded = Property.Replace(value, match => match.Groups[1].Value.Equals("MSBuildThisFileDirectory", StringComparison.OrdinalIgnoreCase) ? directory + Path.DirectorySeparatorChar : _properties.GetValueOrDefault(match.Groups[1].Value) ?? match.Value);
            if (expanded == value)
                break;
            value = expanded;
        }
        return value;
    }
    private bool? Condition(XElement element, string directory)
    {
        var condition = element.Attribute("Condition")?.Value.Trim() ?? "";
        if (condition.Length is 0)
            return true;
        condition = Property.Replace(Expand(condition, directory), "");
        var match = Comparison.Match(condition);
        if (!match.Success)
            return null;
        var equal = string.Equals(match.Groups[1].Value, match.Groups[3].Value, StringComparison.OrdinalIgnoreCase);
        return match.Groups[2].Value is "==" ? equal : !equal;
    }
}
