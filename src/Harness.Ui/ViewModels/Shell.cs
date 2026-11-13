using System.Globalization;
using Harness.Ui.Api;
using Harness.Ui.Navigation;

namespace Harness.Ui.ViewModels;

/// <summary>Small helpers the shell's pages share (app.js plural, ago, stateIcon, workspace cards).</summary>
public static class Shell
{
    /// <summary>"1 project", "3 projects".</summary>
    public static string Plural(int n, string word) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n is 1 ? word : word + "s");

    /// <summary>How long ago an ISO time was (app.js ago()): just now, 5 min ago, 3 h ago, 2 days ago.</summary>
    public static string Ago(string? iso, DateTimeOffset? now = null)
    {
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var then))
        {
            return "";
        }
        var seconds = ((now ?? DateTimeOffset.UtcNow) - then).TotalSeconds;
        return seconds switch
        {
            < 60 => "just now",
            < 3600 => $"{Math.Floor(seconds / 60).ToString(CultureInfo.InvariantCulture)} min ago",
            < 48 * 3600 => $"{Math.Floor(seconds / 3600).ToString(CultureInfo.InvariantCulture)} h ago",
            _ => $"{Math.Floor(seconds / 86400).ToString(CultureInfo.InvariantCulture)} days ago",
        };
    }

    /// <summary>The icon of a state kind (app.js STATE_ICON): a shape per state, never colour alone.</summary>
    public static string StateIcon(string kind) =>
        kind switch
        {
            "ok" => "st-ok",
            "stale" => "refresh",
            "warn" => "st-warn",
            _ => "st-fail",
        };

    /// <summary>
    /// A workspace as the sidebar and Home show it: its first <paramref name="cap"/> projects by name (past the cap,
    /// those waiting and the current one are kept), and how many more there are.
    /// </summary>
    public static NavWorkspace Card(WorkspaceInfo w, IReadOnlyList<ProjectInfo> projects, Route route, int cap)
    {
        var members = w.Members.Select(m => projects.FirstOrDefault(p => p.Id == m.ProjectId)).OfType<ProjectInfo>().ToArray();
        var view = route.Page is "project" ? route.View : "";
        var byName = members.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        var keep = members
            .OrderByDescending(p => p.Waiting)
            .ThenByDescending(p => p.Id == route.Id)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(cap)
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);
        var shown = members.Length <= cap ? byName : byName.Where(p => keep.Contains(p.Id)).ToArray();
        return new NavWorkspace(
            w.Name,
            w.DisplayName,
            Route.WorkspaceHref(w.Name),
            members.Sum(p => p.Waiting),
            route.Page is "workspace" && route.Workspace == w.Name,
            [.. shown.Select(p => NavProject.Of(p, route, view))],
            members.Length - shown.Length
        );
    }
}
