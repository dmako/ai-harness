using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Navigation;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>
/// The page frame (app.js renderProjects/renderMain): the sidebar of workspaces and projects with what waits in each,
/// and the page the address names (Home, a project, a workspace, the machine-wide pages). Navigation goes through the
/// host's history (<see cref="UiHostContext.Navigation"/>); live changes reload the lists (and so the badges and the
/// headers) and the sections that show what changed.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private IReadOnlyList<ProjectInfo> _projects = [];
    private IReadOnlyList<WorkspaceInfo> _workspaces = [];
    private bool _loaded;

    public MainViewModel(UiServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        Navigation = services.Host.Navigation;
        Services.Live.Applied += Applied;
        Navigation.Changed += Navigated;
    }

    public UiServices Services { get; }
    public INavigation Navigation { get; }

    /// <summary>The sidebar's workspaces, each with its first projects.</summary>
    public ObservableCollection<NavWorkspace> Workspaces { get; } = [];

    /// <summary>The sidebar's projects outside any workspace.</summary>
    public ObservableCollection<NavProject> Outside { get; } = [];

    [ObservableProperty]
    public partial PageViewModel? Page { get; private set; }

    [ObservableProperty]
    public partial Route Route { get; private set; } = Route.Home;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool HasOutside { get; private set; }

    [ObservableProperty]
    public partial string? SidebarEmpty { get; private set; }

    /// <summary>The project page's Board, when the page shows it (tests and the browser's timing look at it).</summary>
    public BoardViewModel? Board => (Page as ProjectPage)?.Body as BoardViewModel;

    /// <summary>How many times live changes were applied (tests and the spike's measurements count them).</summary>
    public int LiveReloads => Services.Live.Applies;

    /// <summary>Loads the lists, shows the address the history starts at, and follows live updates.</summary>
    public async Task StartAsync(CancellationToken token)
    {
        await LoadListsAsync(token);
        Show(Navigation.Current);
        _ = Services.Live.FollowAsync(Services.Api, token);
    }

    /// <summary>Goes to <paramref name="href"/> (a sidebar row, a tab, a link inside a page).</summary>
    [RelayCommand]
    public void Navigate(string href) => Navigation.Go(href);

    /// <summary>Takes a live change directly (a host or test), as if it came from the stream.</summary>
    public void Changed(LiveChange change) => Services.Live.Changed(change);

    public void Dispose()
    {
        Services.Live.Applied -= Applied;
        Navigation.Changed -= Navigated;
        Page?.Dispose();
    }

    private void Navigated(object? sender, EventArgs e) => Show(Navigation.Current);

    private async void Applied(object? sender, LiveAppliedEventArgs e)
    {
        var change = e.Change;
        // a project's summary changes with everything but its agents' activity
        if (
            change.All
            || change.Registry
            || change.Workspaces
            || change.Kinds.Values.Any(kinds => kinds.Any(k => k is not "activity"))
        )
        {
            await LoadListsAsync(CancellationToken.None);
            Show(Route);
        }
        Services.Host.LiveReloaded?.Invoke();
    }

    private async Task LoadListsAsync(CancellationToken token)
    {
        try
        {
            var projects = Services.Api.ProjectsAsync(token);
            var workspaces = Services.Api.WorkspacesAsync(token);
            _projects = [.. (await projects).OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)];
            _workspaces = [.. (await workspaces).OrderBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase)];
            _loaded = true;
            Error = null;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
    }

    /// <summary>Shows <paramref name="route"/>: the same page is kept and told the new view, so its header stays.</summary>
    private void Show(Route route)
    {
        if (route.Moved)
        {
            Navigation.Replace(route.Href);
            return;
        }
        Route = route;
        Sidebar(route);
        if (!_loaded)
        {
            return;
        }
        switch (route.Page)
        {
            case "project" when _projects.FirstOrDefault(p => p.Id == route.Id) is { } project:
                if (Page is ProjectPage shown && shown.Id == project.Id)
                {
                    shown.Update(project, route);
                    shown.Show(route);
                }
                else
                {
                    Replace(new ProjectPage(Services, project, route));
                }
                break;
            case "project":
                Replace(new MessagePage($"Project {route.Id} is not registered on this machine."));
                break;
            case "workspace" when _workspaces.FirstOrDefault(w => w.Name == route.Workspace) is { } workspace:
                if (Page is WorkspacePage open && open.Name == workspace.Name)
                {
                    open.Update(workspace, _projects, route);
                }
                else
                {
                    Replace(new WorkspacePage(Services, workspace, _projects, route));
                }
                break;
            case "workspace":
                Replace(new MessagePage($"There is no workspace {route.Workspace} on this machine."));
                break;
            case "knowledge":
                Replace(new PlaceholderPage("Your knowledge", "TASK-0153", Services.Host.Current(route)));
                break;
            case "machine" or "search" or "docs":
                Replace(
                    new PlaceholderPage(
                        char.ToUpperInvariant(route.Page[0]) + route.Page[1..],
                        "TASK-0159",
                        Services.Host.Current(route)
                    )
                );
                break;
            default:
                if (Page is not HomePage home)
                {
                    home = new HomePage();
                    Replace(home);
                }
                home.Update(_projects, _workspaces, route);
                break;
        }
        OnPropertyChanged(nameof(Board));
    }

    private void Replace(PageViewModel page)
    {
        Page?.Dispose();
        Page = page;
    }

    private void Sidebar(Route route)
    {
        var inWorkspace = _workspaces
            .SelectMany(w => w.Members.Select(m => m.ProjectId))
            .ToHashSet(StringComparer.Ordinal);
        var view = route.Page is "project" ? route.View : "";
        Lists.Sync(Workspaces, _workspaces.Select(w => Shell.Card(w, _projects, route, 8)));
        Lists.Sync(
            Outside,
            _projects.Where(p => !inWorkspace.Contains(p.Id)).Select(p => NavProject.Of(p, route, view))
        );
        HasOutside = Outside.Count > 0;
        SidebarEmpty =
            _projects.Count is 0 ? "No projects registered yet. 'harness init' in a repository registers it." : null;
    }
}

/// <summary>What the host (browser page, desktop window, test) tells the shared UI about where it runs.</summary>
public sealed class UiHostContext
{
    /// <summary>The harness ui address, ending with '/'.</summary>
    public required Uri Server { get; init; }

    /// <summary>The page's history: the address bar in the browser, in memory on the desktop and in tests.</summary>
    public INavigation Navigation { get; init; } = new InMemoryNavigation();

    /// <summary>What this page keeps for its user (the report window, the last visits).</summary>
    public Services.IPreferences Preferences { get; init; } = new Services.InMemoryPreferences();

    /// <summary>
    /// The last visit each page started from in this page's life (app.js visitFrom): a live redraw or a return keeps
    /// the time the visit began from, while the stored one moves on to now.
    /// </summary>
    public Dictionary<string, string> Visits { get; } = new(StringComparer.Ordinal);

    /// <summary>The window, in days, of the reports and trends (app.js state.days; the JavaScript UI sets it).</summary>
    public int Days =>
        int.TryParse(Preferences.Get("days"), System.Globalization.CultureInfo.InvariantCulture, out var days) && days > 0 ? days : 30;

    /// <summary>The new UI's own page (its links open there in a new tab); harness ui serves it at /next/.</summary>
    public Uri? Page { get; init; }

    /// <summary>The address of <paramref name="href"/> (a hash) on the new UI's page.</summary>
    public Uri PageLink(string href) => new(Page ?? new Uri(Server, "next/"), href);

    /// <summary>Called after each reload a live change caused.</summary>
    public Action? LiveReloaded { get; init; }

    /// <summary>Called once a project's Board has been loaded and drawn (first-load timing).</summary>
    public Action? BoardShown { get; init; }

    /// <summary>A task's page in the current JavaScript UI (opened from a card's id).</summary>
    public string TaskLink(string projectId, string taskId) =>
        new Uri(Server, $"#/p/{Uri.EscapeDataString(projectId)}/work/tasks/{Uri.EscapeDataString(taskId)}").ToString();

    /// <summary>Where the current (JavaScript) harness ui shows <paramref name="route"/>, for views not ported yet.</summary>
    public Uri Current(Route route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new Uri(Server, route.Href);
    }
}
