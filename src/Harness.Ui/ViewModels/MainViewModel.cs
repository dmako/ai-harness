using System.Collections.ObjectModel;
using Avalonia.Threading;
using Harness.Ui.Api;

namespace Harness.Ui.ViewModels;

/// <summary>
/// The page: the project picker, the chosen project's Board and the live-update state. Live updates follow
/// /api/events: a change naming the shown project reloads its Board after a short quiet period; a dropped stream
/// reconnects and reloads.
/// </summary>
public sealed class MainViewModel(HarnessApi api, UiHostContext host) : ViewModel
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Reconnect = TimeSpan.FromSeconds(3);

    private ProjectInfo? _project;
    private BoardViewModel? _board;
    private string _live = "connecting";
    private string? _error;
    private DispatcherTimer? _pending;

    public ObservableCollection<ProjectInfo> Projects { get; } = [];

    public ProjectInfo? Project
    {
        get => _project;
        set
        {
            if (Set(ref _project, value) && value is not null)
            {
                host.Navigated?.Invoke(value.Id);
                _ = ShowAsync(value);
            }
        }
    }

    public BoardViewModel? Board
    {
        get => _board;
        private set => Set(ref _board, value);
    }

    public string Live
    {
        get => _live;
        private set => Set(ref _live, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>The number of Board reloads live updates caused (the spike measures and tests it).</summary>
    public int LiveReloads { get; private set; }

    /// <summary>Lists the projects, shows <paramref name="projectId"/> (else the first) and follows live updates.</summary>
    public async Task StartAsync(string? projectId, CancellationToken token)
    {
        await LoadProjectsAsync(projectId, token);
        _ = FollowAsync(token);
    }

    private async Task LoadProjectsAsync(string? projectId, CancellationToken token)
    {
        try
        {
            var projects = await api.ProjectsAsync(token);
            Projects.Clear();
            foreach (var project in projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                Projects.Add(project);
            }
            Error = null;
            var wanted = projectId ?? _project?.Id;
            var chosen = Projects.FirstOrDefault(p => p.Id == wanted) ?? Projects.FirstOrDefault();
            if (chosen is not null && chosen.Id == _project?.Id)
            {
                _project = chosen;
                Notify(nameof(Project));
                await ReloadAsync(token);
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
        var board = new BoardViewModel(api, project.Id, id => host.TaskLink(project.Id, id));
        Board = board;
        await board.LoadAsync(CancellationToken.None);
        if (host.BoardShown is { } shown)
        {
            // after the frame that draws the cards
            Dispatcher.UIThread.Post(shown, DispatcherPriority.Background);
        }
    }

    private Task ReloadAsync(CancellationToken token) => Board?.LoadAsync(token) ?? Task.CompletedTask;

    private async Task FollowAsync(CancellationToken token)
    {
        var first = true;
        while (!token.IsCancellationRequested)
        {
            try
            {
                Live = "connecting";
                await foreach (var change in api.ChangesAsync(() => Live = "live", token))
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

    /// <summary>Called for each live change; public so a host or test can feed changes in directly.</summary>
    public void Changed(LiveChange change)
    {
        Live = "live";
        if (change.All || change.Registry)
        {
            Schedule(() => LoadProjectsAsync(null, CancellationToken.None));
        }
        else if (_project is { } shown && change.Projects.Contains(shown.Id, StringComparer.Ordinal))
        {
            Schedule(() => ReloadAsync(CancellationToken.None));
        }
    }

    private void Schedule(Func<Task> reload)
    {
        _pending?.Stop();
        _pending = new DispatcherTimer { Interval = Quiet };
        _pending.Tick += async (_, _) =>
        {
            _pending?.Stop();
            _pending = null;
            LiveReloads++;
            await reload();
            host.LiveReloaded?.Invoke();
        };
        _pending.Start();
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
