using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Navigation;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>A project's row in the sidebar or a workspace card (app.js navProject): its name, state shape and badge.</summary>
public sealed record NavProject(string Id, string Name, string Href, string StateKind, string StateWords, int Waiting, bool Current)
{
    public bool HasWaiting => Waiting > 0;
    public string WaitingTip => $"{Waiting} waiting for you: proposals, failing checks or unreadable state";
    public string StateIcon => Shell.StateIcon(StateKind);

    public static NavProject Of(ProjectInfo p, Route route, string view) =>
        new(
            p.Id,
            p.DisplayName,
            Route.ProjectHref(p.Id, view),
            p.Live.Kind,
            p.Live.Words,
            p.Waiting,
            route.Page is "project" && route.Id == p.Id
        );
}

/// <summary>A workspace in the sidebar: its name and badge, and its first projects (at most 8, app.js NAV_CAP).</summary>
public sealed record NavWorkspace(string Name, string Title, string Href, int Waiting, bool Current, IReadOnlyList<NavProject> Members, int MoreCount)
{
    public bool HasWaiting => Waiting > 0;
    public bool HasMore => MoreCount > 0;
    public string MoreText => $"All {Members.Count + MoreCount} projects →";
    public string MoreHref => Route.WorkspaceHref(Name, "members");

    public bool Equals(NavWorkspace? other) =>
        other is not null
        && Name == other.Name
        && Title == other.Title
        && Waiting == other.Waiting
        && Current == other.Current
        && MoreCount == other.MoreCount
        && Members.SequenceEqual(other.Members);

    public override int GetHashCode() => HashCode.Combine(Name, Waiting, Current, Members.Count);
}

/// <summary>A tab of a page's views (app.js projectHead/workspaceHead): its badge, red for Health's failing checks.</summary>
public sealed record TabItem(string View, string Label, string Href, int Badge, bool Selected, bool Bad = false)
{
    public bool HasBadge => Badge > 0;
}

/// <summary>A number on Home (app.js tile()).</summary>
public sealed record Tile(string Label, string Value, string Note);

/// <summary>What the shell shows: one of these, by the DataTemplate of its type.</summary>
public abstract class PageViewModel : ObservableObject, IDisposable
{
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) { }
}

/// <summary>A message instead of a page: an unknown project or workspace (app.js's texts), with a link Home.</summary>
public sealed class MessagePage(string message) : PageViewModel
{
    public string Message { get; } = message;
}

/// <summary>A page or view that is not in the new UI yet, with a link to it in the current one.</summary>
public sealed class PlaceholderPage(string title, string task, Uri current) : PageViewModel
{
    public string Title { get; } = title;
    public string Note { get; } = $"Not in the new UI yet ({task}).";
    public Uri Current { get; } = current;
}

/// <summary>Home (app.js renderHome): the tiles, the workspaces' cards and the projects outside workspaces.</summary>
public sealed partial class HomePage : PageViewModel
{
    public ObservableCollection<Tile> Tiles { get; } = [];
    public ObservableCollection<NavWorkspace> WorkspaceCards { get; } = [];
    public ObservableCollection<NavProject> Outside { get; } = [];

    [ObservableProperty]
    public partial bool HasWorkspaces { get; private set; }

    [ObservableProperty]
    public partial string OutsideTitle { get; private set; } = "Projects";

    [ObservableProperty]
    public partial string? OutsideEmpty { get; private set; }

    public void Update(IReadOnlyList<ProjectInfo> projects, IReadOnlyList<WorkspaceInfo> workspaces, Route route)
    {
        var inWorkspace = workspaces.SelectMany(w => w.Members.Select(m => m.ProjectId)).ToHashSet(StringComparer.Ordinal);
        var knowledge = projects.Sum(p => p.Live.KnowledgeWaiting);
        var work = projects.Sum(p => p.Live.WorkProposals);
        var failing = projects.Count(p => p.Live.ChecksFailed > 0 || p.Live.State is not "ok");
        var waiting = projects.Where(p => p.Waiting > 0).ToArray();
        var states = new[] { ("bad", "unreadable"), ("warn", "needs a fix"), ("stale", "stale index") }
            .Select(k => (Count: projects.Count(p => p.Live.Kind == k.Item1), Word: k.Item2))
            .Where(k => k.Count > 0)
            .Select(k => $"{k.Count} {k.Word}")
            .ToArray();
        Lists.Sync(
            Tiles,
            [
                new("Projects", Text(projects.Count), states.Length > 0 ? string.Join(" · ", states) : "all healthy"),
                new(
                    "Waiting for you",
                    Text(waiting.Sum(p => p.Waiting)),
                    waiting.Length > 0 ? $"in {Shell.Plural(waiting.Length, "project")}: the badges in the sidebar" : "nothing waiting"
                ),
                new(
                    "Proposals to review",
                    Text(knowledge + work),
                    knowledge + work > 0 ? $"{knowledge} knowledge · {work} tasks" : "nothing waiting"
                ),
                new(
                    "Failing checks",
                    Text(failing),
                    failing > 0
                        ? $"{Shell.Plural(failing, "project")} with a failing check or unreadable state"
                        : "every project passes 'harness doctor'"
                ),
            ]
        );
        Lists.Sync(WorkspaceCards, workspaces.Select(w => Shell.Card(w, projects, route, 6)));
        HasWorkspaces = workspaces.Count > 0;
        var outside = projects.Where(p => !inWorkspace.Contains(p.Id)).ToArray();
        Lists.Sync(Outside, outside.Select(p => NavProject.Of(p, route, "")));
        OutsideTitle = HasWorkspaces ? "Projects outside workspaces" : "Projects";
        OutsideEmpty = null;
        if (outside.Length is 0)
        {
            OutsideEmpty = projects.Count > 0
                ? "Every project is in a workspace."
                : "No projects registered yet. 'harness init' in a repository registers it.";
        }
    }

    private static string Text(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A project's page (app.js projectHead/projectBody): its header (name, rename, short name to copy, where it lives,
/// what it belongs to, its code's state) and tabs, then the chosen view, or why the view cannot be shown.
/// </summary>
public sealed partial class ProjectPage : PageViewModel
{
    private readonly UiServices _services;

    public ProjectPage(UiServices services, ProjectInfo project, Route route)
    {
        _services = services;
        Id = project.Id;
        Project = project;
        Show(route);
    }

    public string Id { get; }

    public ObservableCollection<TabItem> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Copy), nameof(ShortNote), nameof(Facts), nameof(HasFacts), nameof(Detail))]
    public partial ProjectInfo Project { get; private set; }

    [ObservableProperty]
    public partial string View { get; private set; } = "overview";

    [ObservableProperty]
    public partial object? Body { get; private set; }

    /// <summary>The name agents and commands use for it: its short name, else its folder's name.</summary>
    public string Copy => Project.Name is { Length: > 0 } name ? name : Project.Folder;

    /// <summary>The short name, when it is not the folder's.</summary>
    public string? ShortNote =>
        Project.Name is { Length: > 0 } name && name != Project.Folder ? $"short name {name}" : null;

    /// <summary>One line of what changes: branch and commit, last sync, agents' last call.</summary>
    public string Facts
    {
        get
        {
            var facts = new List<string>();
            if (Project.Live.Branch is { } branch)
            {
                var commit = Project.Live.Commit is { Length: >= 7 } c ? $" {c[..7]}" : "";
                facts.Add($"⎇ {branch}{commit}");
            }
            if (Project.Live.SyncedAt is { } synced)
            {
                facts.Add($"↻ synced {Shell.Ago(synced)}");
            }
            if (Project.LastMcp is { } mcp)
            {
                var clients = Project.Clients.Count > 0 ? $" · {string.Join(", ", Project.Clients)}" : "";
                facts.Add($"agents {Shell.Ago(mcp)}{clients}");
            }
            return string.Join("  ·  ", facts);
        }
    }

    public bool HasFacts => Facts.Length > 0;

    public string? Detail => Project.Live.Detail;

    /// <summary>The project's newer list entry (a rename, a live change) without leaving the view.</summary>
    public void Update(ProjectInfo project, Route route)
    {
        Project = project;
        (Body as OverviewViewModel)?.Update(project);
        (Body as WorkViewModel)?.Update(project, route);
        (Body as KnowledgeViewModel)?.Update(project, route);
        Tabs.Clear();
        foreach (var tab in TabsOf(project, route.View))
        {
            Tabs.Add(tab);
        }
    }

    /// <summary>Shows <paramref name="route"/>'s view: the same view keeps its body (its scroll, its loaded data).</summary>
    public void Show(Route route)
    {
        Lists.Sync(Tabs, TabsOf(Project, route.View));
        if (View == route.View && Body is not null)
        {
            (Body as WorkViewModel)?.Show(route);
            (Body as KnowledgeViewModel)?.Show(route);
            (Body as AgentsViewModel)?.Show(route);
            return;
        }
        View = route.View;
        (Body as IDisposable)?.Dispose();
        Body = BodyOf(route);
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        var dialog = new RenameDialog(_services, "project", $"api/projects/{Uri.EscapeDataString(Id)}/rename", Project.Title ?? "", Project.Name ?? "", Project.Folder)
        {
            Title = "Rename this project",
            Sub = "Only the machine's project registry changes; the repository and its .harness state are untouched.",
            AcceptLabel = "Save",
        };
        await _services.Dialogs.ShowAsync(dialog);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
                (Body as IDisposable)?.Dispose();
        }
        base.Dispose(disposing);
    }

    private object BodyOf(Route route)
    {
        var live = Project.Live;
        // Health and Architecture read no project state (Architecture reads the repository's files)
        if (live.State is not "ok" && route.View is not "health" && !(route.View is "architecture" && live.State is not "missing"))
        {
            return new UnreadableBody(Route.ProjectHref(Id, "health"));
        }
        if (route.View is "board")
        {
            var board = new BoardViewModel(_services, Id, task => _services.Host.TaskLink(Id, task));
            board.Follow(_services.Live);
            _ = ShowBoardAsync(board);
            return board;
        }
        if (route.View is "health")
        {
            var health = new HealthViewModel(_services, Id);
            health.Follow(_services.Live);
            _ = health.LoadAsync();
            return health;
        }
        if (route.View is "overview")
        {
            var overview = new OverviewViewModel(_services, Project);
            overview.Start();
            return overview;
        }
        if (route.View is "agents")
        {
            var agents = new AgentsViewModel(_services, Id, route);
            agents.Start();
            return agents;
        }
        if (route.View is "knowledge")
        {
            var knowledge = new KnowledgeViewModel(_services, Project, route);
            knowledge.Start();
            return knowledge;
        }
        if (route.View is "work")
        {
            var work = new WorkViewModel(_services, Project, route);
            work.Start();
            return work;
        }
        // Architecture is the one view still to port
        return new PlaceholderPage("Architecture", "TASK-0156", _services.Host.Current(route));
    }

    private async Task ShowBoardAsync(BoardViewModel board)
    {
        await board.LoadAsync();
        if (_services.Host.BoardShown is { } shown)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(shown, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private static IEnumerable<TabItem> TabsOf(ProjectInfo p, string view) =>
        Route.ProjectViews.Select(v =>
            new TabItem(
                v.View,
                v.Label,
                Route.ProjectHref(p.Id, v.View),
                v.View switch
                {
                    "knowledge" => p.Live.KnowledgeWaiting,
                    "work" => p.Live.WorkProposals,
                    "health" => p.Live.ChecksFailed,
                    _ => 0,
                },
                v.View == view,
                v.View is "health"
            )
        );
}

/// <summary>Why a project view is not shown (app.js projectBody): the state cannot be read; Health says why.</summary>
public sealed class UnreadableBody(string healthHref)
{
    public static string Message => "This project's state cannot be read until the problem above is fixed.";
    public string HealthHref { get; } = healthHref;
}

/// <summary>A workspace's page (app.js workspaceHead): its name, rename, workspace:&lt;name&gt; to copy, and tabs.</summary>
public sealed partial class WorkspacePage : PageViewModel
{
    private readonly UiServices _services;

    public WorkspacePage(UiServices services, WorkspaceInfo workspace, IReadOnlyList<ProjectInfo> projects, Route route)
    {
        _services = services;
        Name = workspace.Name;
        Workspace = workspace;
        Update(workspace, projects, route);
    }

    public string Name { get; }
    public ObservableCollection<TabItem> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Copy))]
    public partial WorkspaceInfo Workspace { get; private set; }

    [ObservableProperty]
    public partial string Path { get; private set; } = "";

    [ObservableProperty]
    public partial object? Body { get; private set; }

    public string Copy => "workspace:" + Workspace.Name;

    public void Update(WorkspaceInfo workspace, IReadOnlyList<ProjectInfo> projects, Route route)
    {
        Workspace = workspace;
        var members = workspace.Members.Select(m => projects.FirstOrDefault(p => p.Id == m.ProjectId)).OfType<ProjectInfo>().ToArray();
        var waiting = members.Sum(p => p.Waiting);
        Path = $"{Shell.Plural(workspace.Members.Count, "project")}, created {Shell.Ago(workspace.Created)}"
            + (waiting > 0 ? $" · {waiting} waiting for you" : "");
        Lists.Sync(
            Tabs,
            Route.WorkspaceViews.Select(v => new TabItem(v.View, v.Label, Route.WorkspaceHref(workspace.Name, v.View), 0, v.View == route.View))
        );
        if (route.View is "knowledge")
        {
            if (Body is not LevelKnowledgeViewModel)
            {
                (Body as IDisposable)?.Dispose();
                var knowledge = new LevelKnowledgeViewModel(
                    _services,
                    "workspace:" + workspace.Name,
                    "Knowledge for every project of this workspace: it applies to each of them unless a project states otherwise (then the project's own takes precedence)."
                );
                knowledge.Follow(_services.Live);
                _ = knowledge.LoadAsync();
                Body = knowledge;
            }
        }
        else if (Body is not PlaceholderPage { } shown || shown.Title != Label(route.View))
        {
            (Body as IDisposable)?.Dispose();
            Body = new PlaceholderPage(Label(route.View), "TASK-0158", _services.Host.Current(route));
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        var dialog = new RenameDialog(_services, "workspace", $"api/workspaces/{Uri.EscapeDataString(Name)}/rename", Workspace.Title ?? "", Workspace.Name, null)
        {
            Title = "Rename this workspace",
            Sub = "Only the machine's workspace list changes.",
            AcceptLabel = "Save",
        };
        if (await _services.Dialogs.ShowAsync(dialog) is { Name: { Length: > 0 } renamed } && renamed != Name)
        {
            _services.Host.Navigation.Replace(Route.WorkspaceHref(renamed));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            (Body as IDisposable)?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string Label(string view) => Route.WorkspaceViews.FirstOrDefault(v => v.View == view).Label ?? "Overview";
}

/// <summary>
/// Renaming a project or workspace (app.js renameDialog): its display name and short name, checked as the server
/// checks them, with the changes reviewed before they are saved.
/// </summary>
public sealed partial class RenameDialog : FormDialog<RenameChange>
{
    private readonly UiServices _services;
    private readonly string _path;
    private readonly string _display;
    private readonly string _short;
    private readonly string? _folder;

    public RenameDialog(UiServices services, string what, string path, string display, string shortName, string? folder)
    {
        _services = services;
        _path = path;
        _display = display;
        _short = shortName;
        _folder = folder;
        What = what;
        Display = display;
        Short = shortName;
    }

    public string What { get; }

    [ObservableProperty]
    public partial string Display { get; set; }

    [ObservableProperty]
    public partial string Short { get; set; }

    public string DisplayHint => What is "project" ? _folder ?? "" : _short;

    public string ShortHint => _folder ?? "";

    protected override string? Validate()
    {
        var change = Change();
        var name = Short.Trim();
        var valid = What is "workspace"
            ? System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9._-]{0,63}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))
            : name.Length is 0 || System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!valid)
        {
            return What is "workspace"
                ? "Short name: lower-case letters, digits, '.', '_' or '-'."
                : "Short name: letters, digits, '.', '_' or '-' (empty: the folder's name).";
        }
        return change.Title is null && change.Name is null ? "Nothing changed yet." : null;
    }

    protected override string? Review()
    {
        var change = Change();
        var lines = new List<string>();
        if (change.Title is { } title)
        {
            var before = _display.Length > 0 ? _display : "(none)";
            lines.Add(title.Length > 0 ? $"Display name: {before} → {title}" : $"Display name: {_display} → removed");
        }
        if (change.Name is { } name)
        {
            var before = _short.Length > 0 ? _short : _folder;
            lines.Add($"Short name: {before} → {(name.Length > 0 ? name : _folder)}");
        }
        return string.Join("\n", lines);
    }

    protected override async Task<RenameChange> SendAsync(CancellationToken token)
    {
        var change = Change();
        await _services.Api.PostAsync(_path, change, UiJsonContext.Default.RenameChange, token);
        _services.Toasts.Show("Renamed");
        _services.Live.RefreshAll();
        return change;
    }

    private RenameChange Change()
    {
        var title = System.Text.RegularExpressions.Regex.Replace(Display.Trim(), @"\s+", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        var name = Short.Trim();
        return new RenameChange { Title = title != _display ? title : null, Name = name != _short ? name : null };
    }
}
