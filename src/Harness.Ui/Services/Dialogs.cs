using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;

namespace Harness.Ui.Services;

/// <summary>
/// The dialog shown over the page (one at a time, as app.js's dialog()). While one is open, live updates wait: a form
/// being filled in is never redrawn under the user.
/// </summary>
public sealed partial class DialogService : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial DialogViewModel? Current { get; private set; }

    public bool IsOpen => Current is not null;

    /// <summary>Raised when the open dialog closes, so what waited for it (live updates) can go on.</summary>
    public event EventHandler? Closed;

    /// <summary>Shows <paramref name="dialog"/> and waits until it is accepted or cancelled.</summary>
    public async Task<TResult?> ShowAsync<TResult>(DialogViewModel<TResult> dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        Current?.Cancel();
        Current = dialog;
        try
        {
            return await dialog.Result;
        }
        finally
        {
            if (ReferenceEquals(Current, dialog))
            {
                Current = null;
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>A yes-or-no question; true when confirmed.</summary>
    public async Task<bool> ConfirmAsync(ConfirmDialog dialog) => await ShowAsync(dialog);
}

/// <summary>What every dialog has: a title and line under it, its error, its width, and how it closes.</summary>
public abstract partial class DialogViewModel : ObservableObject
{
    public required string Title { get; init; }
    public string? Sub { get; init; }

    /// <summary>A wider dialog (680 px instead of 520), for details and forms with long fields.</summary>
    public bool Wide { get; init; }

    /// <summary>The label of the button that accepts.</summary>
    public string AcceptLabel { get; init; } = "OK";

    /// <summary>Whether it has a Cancel button (a detail that only closes has none: its accept button closes it).</summary>
    public bool HasCancel { get; init; } = true;

    /// <summary>Whether accepting destroys something (the accept button is a danger button).</summary>
    public bool Danger { get; init; }

    /// <summary>A refusal of the form or of the write it sent, shown in the dialog, which stays open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; protected set; }

    public bool HasError => Error is { Length: > 0 };

    [ObservableProperty]
    public partial bool Busy { get; protected set; }

    /// <summary>Whether the review of what will change is shown instead of the form (the accept button sends).</summary>
    [ObservableProperty]
    public partial bool Reviewing { get; protected set; }

    /// <summary>What sending will change, while reviewing.</summary>
    [ObservableProperty]
    public partial string? ReviewText { get; protected set; }

    [RelayCommand]
    public abstract void Cancel();

    /// <summary>Back from the review to the form.</summary>
    [RelayCommand]
    private void Back()
    {
        Reviewing = false;
        ReviewText = null;
    }

    [RelayCommand]
    protected abstract Task AcceptAsync();
}

/// <summary>A dialog that ends with a <typeparamref name="TResult"/> (default when cancelled).</summary>
public abstract class DialogViewModel<TResult> : DialogViewModel
{
    private readonly TaskCompletionSource<TResult?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<TResult?> Result => _result.Task;

    public override void Cancel() => _result.TrySetResult(default);

    protected void Close(TResult result) => _result.TrySetResult(result);
}

/// <summary>A question with Cancel and an accept button: a message, or a danger confirmation.</summary>
public sealed class ConfirmDialog : DialogViewModel<bool>
{
    public string? Message { get; init; }

    protected override Task AcceptAsync()
    {
        Close(true);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A form: its fields are the subclass's properties, shown by its own view. Accepting checks it
/// (<see cref="Validate"/>), then shows what will change when <see cref="Review"/> says so (review before send: the
/// accept button then sends), then sends it; a refused send keeps the dialog open with the server's message.
/// </summary>
public abstract partial class FormDialog<TResult> : DialogViewModel<TResult>
{
    /// <summary>The form's problem, or null when it can be sent.</summary>
    protected abstract string? Validate();

    /// <summary>What sending will change, shown before it is sent; null sends at once.</summary>
    protected virtual string? Review() => null;

    /// <summary>Sends the form (a write) and gives the dialog's result.</summary>
    protected abstract Task<TResult> SendAsync(CancellationToken token);

    protected override async Task AcceptAsync()
    {
        if (Busy)
        {
            return;
        }
        Error = Validate();
        if (Error is not null)
        {
            return;
        }
        if (!Reviewing && Review() is { } review)
        {
            ReviewText = review;
            Reviewing = true;
            return;
        }
        Busy = true;
        try
        {
            Close(await SendAsync(CancellationToken.None));
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
