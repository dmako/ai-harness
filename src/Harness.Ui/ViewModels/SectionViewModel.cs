using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>
/// A part of a page that loads on its own (app.js mount()): a loading state the first time, its content kept while it
/// reloads, and its own error with Retry; nothing on the page waits for it. It follows live updates for its project
/// and kinds while registered (<see cref="Follow"/>). Subclasses load in <see cref="LoadCoreAsync"/> and are shown by
/// the DataTemplate of their type inside a SectionView.
/// </summary>
public abstract partial class SectionViewModel : ObservableObject, ILiveSection, IDisposable
{
    private IDisposable? _registration;
    private int _sequence;

    protected SectionViewModel(string title, string? projectId = null, IReadOnlyList<string>? kinds = null)
    {
        Title = title;
        ProjectId = projectId;
        Kinds = kinds ?? [];
    }

    public string Title { get; }

    /// <summary>A line under the title saying what the section shows (style.css .section .hint).</summary>
    public string? Hint { get; init; }

    public string? ProjectId { get; }
    public IReadOnlyList<string> Kinds { get; }

    /// <summary>Loading for the first time: the content is not there yet.</summary>
    [ObservableProperty]
    public partial bool Loading { get; private set; }

    /// <summary>Loading again with the content still shown.</summary>
    [ObservableProperty]
    public partial bool Refreshing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    public partial bool HasContent { get; private set; }

    public bool HasHint => Hint is { Length: > 0 };

    /// <summary>Follows <paramref name="live"/> until disposed.</summary>
    public void Follow(LiveUpdates live)
    {
        ArgumentNullException.ThrowIfNull(live);
        _registration?.Dispose();
        _registration = live.Register(this);
    }

    /// <summary>Loads; a newer load wins over an older one still running, and a failure is shown, not thrown.</summary>
    public async Task RefreshAsync(bool force)
    {
        var sequence = ++_sequence;
        Loading = !HasContent;
        Refreshing = HasContent;
        try
        {
            await LoadCoreAsync(CancellationToken.None);
            if (sequence == _sequence)
            {
                Error = null;
                HasContent = true;
            }
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            if (sequence == _sequence)
            {
                Error = exception.Message;
            }
        }
        finally
        {
            if (sequence == _sequence)
            {
                Loading = false;
                Refreshing = false;
            }
        }
    }

    public Task LoadAsync() => RefreshAsync(force: true);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops following live updates; a subclass releases what it holds and calls this.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _registration?.Dispose();
            _registration = null;
        }
    }

    protected abstract Task LoadCoreAsync(CancellationToken token);

    [RelayCommand]
    private Task RetryAsync() => RefreshAsync(force: true);
}
