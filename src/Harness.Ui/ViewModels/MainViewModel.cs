using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Harness.Ui.Api;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>
/// The page: the project picker, the chosen project's Board, and the services every part of the page shares (toasts,
/// dialogs, live updates). Live updates reload the sections that show what changed; a change to the registry (or
/// one that says everything changed) reloads the project list too.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private IDisposable? _boardFollow;

    public MainViewModel(UiServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        Services.Live.Applied += Applied;
    }

    public UiServices Services { get; }

    public ObservableCollection<ProjectInfo> Projects { get; } = [];

    [ObservableProperty]
    public partial ProjectInfo? Project { get; set; }

    [ObservableProperty]
    public partial BoardViewModel? Board { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>How many times live changes were applied (tests and the spike's measurements count them).</summary>
    public int LiveReloads => Services.Live.Applies;

    /// <summary>Lists the projects, shows <paramref name="projectId"/> (else the first) and follows live updates.</summary>
    public async Task StartAsync(string? projectId, CancellationToken token)
    {
        await LoadProjectsAsync(projectId, token);
        _ = Services.Live.FollowAsync(Services.Api, token);
    }

    /// <summary>Takes a live change directly (a host or test), as if it came from the stream.</summary>
    public void Changed(LiveChange change) => Services.Live.Changed(change);

    public void Dispose()
    {
        Services.Live.Applied -= Applied;
        _boardFollow?.Dispose();
        Board?.Dispose();
    }

    partial void OnProjectChanged(ProjectInfo? value)
    {
        if (value is not null)
        {
            Services.Host.Navigated?.Invoke(value.Id);
            _ = ShowAsync(value);
        }
    }

    private void Applied(object? sender, LiveAppliedEventArgs e)
    {
        if (e.Change.All || e.Change.Registry)
        {
            _ = LoadProjectsAsync(null, CancellationToken.None);
        }
        Services.Host.LiveReloaded?.Invoke();
    }

    private async Task LoadProjectsAsync(string? projectId, CancellationToken token)
    {
        try
        {
            var projects = await Services.Api.ProjectsAsync(token);
            var wanted = projectId ?? Project?.Id;
            var sorted = projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            Projects.Clear();
            foreach (var project in sorted)
            {
                Projects.Add(project);
            }
            Error = null;
            // the same project keeps its Board (ShowAsync leaves a shown one), so its cards stay
            Project = Projects.FirstOrDefault(p => p.Id == wanted) ?? Projects.FirstOrDefault();
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
        _boardFollow?.Dispose();
        Board?.Dispose();
        var board = new BoardViewModel(Services, project.Id, id => Services.Host.TaskLink(project.Id, id));
        Board = board;
        _boardFollow = Services.Live.Register(board);
        await board.LoadAsync(CancellationToken.None);
        if (Services.Host.BoardShown is { } shown)
        {
            // after the frame that draws the cards
            Dispatcher.UIThread.Post(shown, DispatcherPriority.Background);
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
