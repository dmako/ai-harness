using System.Text.Json.Nodes;
using Harness.Infrastructure.Workspaces;

namespace Harness.Infrastructure.Projects;

/// <summary>
/// What a project name means on this machine, as the Go harness reads it: a registered project's id, folder name,
/// parent/folder, short name or parent/short name, case apart. A name two projects answer to is refused with both
/// named.
/// </summary>
public static class ProjectNames
{
    public static IReadOnlyList<string> Names(string id, string shortName, string root)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        var folder = Path.GetFileName(trimmed);
        var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? "");
        List<string> names = [id, folder, parent + "/" + folder];
        if (shortName.Length is not 0 && !shortName.Equals(folder, StringComparison.Ordinal))
        {
            names.Add(shortName);
            names.Add(parent + "/" + shortName);
        }

        return names;
    }

    public static bool Matches(JsonObject project, string name) =>
        name.Length is not 0
        && Names(Text(project, "id"), WorkspaceStore.ShortName(project), Text(project, "root"))
            .Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The registered project a name means.</summary>
    /// <exception cref="ArgumentException">No project, or two, answer to the name.</exception>
    public static JsonObject Find(JsonObject registry, string name)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var found = (registry["projects"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(p => Matches(p, name))
            .Take(2)
            .ToArray();
        return found switch
        {
            [var one] => one,
            [var first, var second] => throw new ArgumentException(
                $"ambiguous project name \"{name}\": {Label(first)} and {Label(second)} (name one by parent/folder or id)"
            ),
            _ => throw new ArgumentException(
                $"unknown project {name} (a registered project's short name, folder name, parent/folder or id)"
            ),
        };
    }

    private static string Label(JsonObject project) => $"{WorkspaceStore.ShortName(project)} ({Text(project, "root")})";

    private static string Text(JsonObject project, string key) => project[key]?.GetValue<string>() ?? "";
}
