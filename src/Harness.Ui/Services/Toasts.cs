using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Harness.Ui.Services;

/// <summary>A moment that something happened (app.js toast()): a word, and for a failure the server's detail.</summary>
public sealed record Toast(string Text, string? Detail, bool Bad)
{
    public bool HasDetail => Detail is { Length: > 0 };
}

/// <summary>
/// The toasts shown at the bottom corner. A success stays briefly (1.6 s), a failure long enough to read its detail
/// (6 s), as app.js's toast() does.
/// </summary>
public sealed class ToastService(TimeSpan? good = null, TimeSpan? bad = null)
{
    private readonly TimeSpan _good = good ?? TimeSpan.FromMilliseconds(1600);
    private readonly TimeSpan _bad = bad ?? TimeSpan.FromSeconds(6);

    public ObservableCollection<Toast> Items { get; } = [];

    public void Show(string text) => Add(new Toast(text, null, false));

    public void Fail(string text, string? detail = null) => Add(new Toast(text, detail, true));

    private void Add(Toast toast)
    {
        Items.Add(toast);
        DispatcherTimer.RunOnce(() => Items.Remove(toast), toast.Bad ? _bad : _good);
    }
}
