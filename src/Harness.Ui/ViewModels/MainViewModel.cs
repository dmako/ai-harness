using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Harness.Ui.Api;

namespace Harness.Ui.ViewModels;

/// <summary>
/// The page: the project picker, the chosen project's Board and the live-update state. Live updates follow
/// /api/events: changes naming the shown project reload its Board once they have been quiet for 500 ms (Rx
/// Throttle on the UI thread's scheduler); a dropped stream reconnects and reloads.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Reconnect = TimeSpan.FromSeconds(3);

    private readonly HarnessApi _api;
    private readonly UiHostContext _host;
    private readonly Subject<LiveChange> _changes = new();
    private readonly IDisposable _reloads;

    /// <summary>Created on the UI thread: the reloads run on its scheduler.</summary>
    public MainViewModel(HarnessApi api, UiHostContext host)
    {
        _api = api;
        _host = host;
        var ui = new SynchronizationContextScheduler(
            SynchronizationContext.Current ?? throw new InvalidOperationException("create on the UI thread")
        );
        // true: the project list changed (and with it maybe the shown project); false: the shown project's data
        var projects = _changes.Where(c => c.All || c.Registry).Throttle(Quiet, ui).Select(_ => true);
        var board = _changes
            .Where(c => !c.All && !c.Registry && Project is { } shown && c.Projects.Contains(shown.Id, StringComparer.Ordinal))
            .Throttle(Quiet, ui)
            .Select(_ => false);
        // one reload at a time, in order, on the UI thread
        _reloads = projects
            .Merge(board)
            .ObserveOn(ui)
            .Select(list => Observable.FromAsync(() => LiveReloadAsync(list)))
            .Concat()
            .Subscribe();
    }

    public ObservableCollection<ProjectInfo> Projects { get; } = [];

    [ObservableProperty]
    public partial ProjectInfo? Project { get; set; }

    [ObservableProperty]
    public partial BoardViewModel? Board { get; private set; }

    [ObservableProperty]
    public partial string Live { get; private set; } = "connecting";

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The number of reloads live updates caused (the spike measures and tests it).</summary>
    public int LiveReloads { get; private set; }

    /// <summary>Lists the projects, shows <paramref name="projectId"/> (else the first) and follows live updates.</summary>
    public async Task StartAsync(string? projectId, CancellationToken token)
    {
        await LoadProjectsAsync(projectId, token);
        _ = FollowAsync(token);
    }

    /// <summary>Called for each live change; public so a host or test can feed changes in directly.</summary>
    public void Changed(LiveChange change)
    {
        Live = "live";
        _changes.OnNext(change);
    }

    public void Dispose()
    {
        _reloads.Dispose();
        _changes.Dispose();
        Board?.Dispose();
    }

    partial void OnProjectChanged(ProjectInfo? value)
    {
        if (value is not null)
        {
            _host.Navigated?.Invoke(value.Id);
            _ = ShowAsync(value);
        }
    }

    private async Task LoadProjectsAsync(string? projectId, CancellationToken token)
    {
        try
        {
            var projects = await _api.ProjectsAsync(token);
            var wanted = projectId ?? Project?.Id;
            var sorted = projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            Projects.Clear();
            foreach (var project in sorted)
            {
                Projects.Add(project);
            }
            Error = null;
            var chosen = Projects.FirstOrDefault(p => p.Id == wanted) ?? Projects.FirstOrDefault();
            if (chosen is not null && chosen.Id == Board?.ProjectId)
            {
                // the same project: keep its Board (and its cards), only refresh it; ShowAsync keeps a shown Board
                Project = chosen;
                await Board.LoadAsync(token);
                return;
            }
            Project = chosen;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
    }

    private async Task ShowAsync(ProjectInfo project)
    {
        if (Board?.ProjectId == project.Id)
        {
            return;
        }
        Board?.Dispose();
        var board = new BoardViewModel(_api, project.Id, id => _host.TaskLink(project.Id, id));
        Board = board;
        await board.LoadAsync(CancellationToken.None);
        if (_host.BoardShown is { } shown)
        {
            // after the frame that draws the cards
            Dispatcher.UIThread.Post(shown, DispatcherPriority.Background);
        }
    }

    private async Task LiveReloadAsync(bool projects)
    {
        LiveReloads++;
        if (projects)
        {
            await LoadProjectsAsync(null, CancellationToken.None);
        }
        else if (Board is { } board)
        {
            await board.LoadAsync(CancellationToken.None);
        }
        _host.LiveReloaded?.Invoke();
    }

    private async Task FollowAsync(CancellationToken token)
    {
        var first = true;
        while (!token.IsCancellationRequested)
        {
            try
            {
                Live = "connecting";
                await foreach (var change in _api.ChangesAsync(() => Live = "live", token))
                {
                    Changed(change);
                }
            }
            catch (Exception exception) when (exception is HarnessApiException or HttpRequestException or IOException)
            {
                Error = "live updates: " + exception.Message;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            Live = "offline";
            await Task.Delay(Reconnect, token);
            if (!first)
            {
                await LoadProjectsAsync(null, token);
            }
            first = false;
        }
    }
}

/// <summary>What the host (browser page, desktop window, test) tells the shared UI about where it runs.</summary>
public sealed class UiHostContext
{
    /// <summary>The harness ui address, ending with '/'.</summary>
    public required Uri Server { get; init; }

    /// <summary>Called with the project shown, so a browser can keep its address a deep link.</summary>
    public Action<string>? Navigated { get; init; }

    /// <summary>Called after each reload a live change caused.</summary>
    public Action? LiveReloaded { get; init; }

    /// <summary>Called once a project's Board has been loaded and drawn (first-load timing).</summary>
    public Action? BoardShown { get; init; }

    /// <summary>A task's page in the current JavaScript UI (opened from a card's id).</summary>
    public string TaskLink(string projectId, string taskId) =>
        new Uri(Server, $"#/p/{Uri.EscapeDataString(projectId)}/work/tasks/{Uri.EscapeDataString(taskId)}").ToString();
}
