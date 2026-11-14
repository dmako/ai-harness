namespace Harness.Ui.Navigation;

/// <summary>
/// A page of harness ui by its hash address, as app.js route() reads it: home (#/), a project (#/p/&lt;id&gt;/&lt;view&gt;
/// [/&lt;item&gt;]), a workspace (#/w/&lt;name&gt;/&lt;view&gt;[/&lt;item&gt;]), your knowledge, the machine, search (#/search?q=)
/// and the docs (#/docs/&lt;doc&gt;). An address of the earlier layout (#/&lt;project&gt;/&lt;tab&gt;, #/workspaces/…) is read as
/// the page it moved to, with <see cref="Moved"/> set so the host replaces the address.
/// </summary>
public sealed record Route
{
    public static readonly IReadOnlyList<(string View, string Label)> ProjectViews =
    [
        ("overview", "Overview"),
        ("architecture", "Architecture"),
        ("knowledge", "Knowledge"),
        ("work", "Work"),
        ("board", "Board"),
        ("agents", "Agents"),
        ("health", "Health"),
    ];

    public static readonly IReadOnlyList<(string View, string Label)> WorkspaceViews =
    [
        ("overview", "Overview"),
        ("architecture", "Architecture"),
        ("knowledge", "Knowledge"),
        ("issues", "Issues"),
        ("members", "Members"),
    ];

    /// <summary>Workspace views of the earlier layout, now sub-views (app.js WORKSPACE_MOVED).</summary>
    private static readonly Dictionary<string, (string View, string Item)> WorkspaceMoved = new(StringComparer.Ordinal)
    {
        ["shared"] = ("issues", "shared"),
        ["libraries"] = ("architecture", "libraries"),
    };

    /// <summary>Project tabs of the earlier layout and where they are now (app.js LEGACY_TABS).</summary>
    private static readonly Dictionary<string, string> LegacyTabs = new(StringComparer.Ordinal)
    {
        ["findings"] = "overview",
        ["context"] = "agents/context",
        ["providers"] = "agents/tools",
        ["metrics"] = "agents/tools",
        ["knowledge"] = "knowledge",
        ["proposals"] = "knowledge/inbox",
        ["tasks"] = "work",
        ["health"] = "health",
    };

    /// <summary>home, project, workspace, knowledge, machine, search or docs.</summary>
    public string Page { get; init; } = "home";

    /// <summary>The project's id (project pages).</summary>
    public string Id { get; init; } = "";

    /// <summary>The workspace's short name (workspace pages).</summary>
    public string Workspace { get; init; } = "";

    public string View { get; init; } = "";

    /// <summary>What inside the view: a section, an item's id; for docs the document.</summary>
    public string Item { get; init; } = "";

    /// <summary>The search text (#/search?q=).</summary>
    public string Query { get; init; } = "";

    /// <summary>The address was one of the earlier layout's; <see cref="Href"/> is where it moved.</summary>
    public bool Moved { get; init; }

    public static Route Home { get; } = new();

    /// <summary>
    /// Reads <paramref name="hash"/> (with or without '#'); a project page without a known view gets
    /// <paramref name="defaultProjectView"/> (the one the user last chose).
    /// </summary>
    public static Route Parse(string? hash, string? defaultProjectView = null)
    {
        defaultProjectView ??= DefaultProjectView;
        var raw = (hash ?? "").TrimStart('#').TrimStart('/');
        var at = raw.IndexOf('?', StringComparison.Ordinal);
        var path = at < 0 ? raw : raw[..at];
        var query = at < 0 ? "" : raw[(at + 1)..];
        var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Decode).ToArray();
        string Seg(int i) => i < seg.Length ? seg[i] : "";
        if (seg.Length is 0)
        {
            return Home;
        }
        switch (seg[0])
        {
            case "p":
                return new Route
                {
                    Page = "project",
                    Id = Seg(1),
                    View = ProjectViews.Any(v => v.View == Seg(2)) ? Seg(2) : DefaultView(defaultProjectView),
                    Item = string.Join('/', seg.Skip(3)),
                };
            case "w":
                var (view, item) = WorkspaceMoved.TryGetValue(Seg(2), out var moved) ? moved : WorkspaceView(Seg(2), Seg(3));
                return new Route
                {
                    Page = "workspace",
                    Workspace = Seg(1),
                    View = view,
                    Item = item,
                };
            case "knowledge" or "machine":
                return new Route { Page = seg[0] };
            case "search":
                return new Route { Page = "search", Query = QueryValue(query, "q") };
            case "docs":
                return new Route { Page = "docs", Item = seg.Length > 1 ? seg[1] : "guide" };
            default:
                return Legacy(seg, defaultProjectView) with { Moved = true };
        }
    }

    /// <summary>The address of this page (app.js projectHref/workspaceHref and the fixed pages).</summary>
    public string Href =>
        Page switch
        {
            "project" => ProjectHref(Id, View, Item),
            "workspace" => WorkspaceHref(Workspace, View, Item),
            "knowledge" or "machine" => "#/" + Page,
            "search" => Query.Length > 0 ? "#/search?q=" + Uri.EscapeDataString(Query) : "#/search",
            "docs" => "#/docs/" + (Item.Length > 0 ? Item : "guide"),
            _ => "#/",
        };

    /// <summary>A project's view (app.js projectHref): the item is encoded as one piece.</summary>
    public static string ProjectHref(string id, string? view = null, string? item = null) =>
        $"#/p/{Uri.EscapeDataString(id)}/{(view is { Length: > 0 } ? view : "overview")}"
        + (item is { Length: > 0 } ? "/" + Uri.EscapeDataString(item) : "");

    /// <summary>A workspace's view (app.js workspaceHref): the item is kept as written.</summary>
    public static string WorkspaceHref(string name, string? view = null, string? item = null) =>
        $"#/w/{Uri.EscapeDataString(name)}/{(view is { Length: > 0 } ? view : "overview")}"
        + (item is { Length: > 0 } ? "/" + item : "");

    /// <summary>The page an earlier layout's address moved to (app.js legacyHash).</summary>
    private static Route Legacy(string[] seg, string defaultProjectView)
    {
        if (seg[0] is "workspaces")
        {
            return seg.Length > 1 ? Parse(WorkspaceHref(seg[1])) : Home;
        }
        var to = seg.Length > 1 && LegacyTabs.TryGetValue(seg[1], out var tab) ? tab : "overview";
        var parts = to.Split('/');
        var sub = parts.Length > 1 ? parts[1] : "";
        var item = seg.Length > 2 ? string.Join('/', seg.Skip(2)) : sub;
        return Parse(ProjectHref(seg[0], parts[0], item), defaultProjectView);
    }

    private static (string View, string Item) WorkspaceView(string view, string item) =>
        (WorkspaceViews.Any(v => v.View == view) ? view : "overview", item);

    /// <summary>The view a project address without one opens on (Machine › Settings; Overview by default).</summary>
    public static string DefaultProjectView { get; set; } = "overview";

    private static string DefaultView(string view) => ProjectViews.Any(v => v.View == view) ? view : "overview";

    private static string Decode(string segment)
    {
        try
        {
            return Uri.UnescapeDataString(segment);
        }
        catch (UriFormatException)
        {
            return segment;
        }
    }

    private static string QueryValue(string query, string name)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? pair : pair[..eq];
            if (key == name)
            {
                return Decode((eq < 0 ? "" : pair[(eq + 1)..]).Replace('+', ' '));
            }
        }
        return "";
    }
}
