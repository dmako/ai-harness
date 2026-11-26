using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Harness.Ui.Views;

/// <summary>
/// What the window shows before the UI can talk to its API (the desktop app finding or starting harness ui): what it is
/// doing, or why it could not connect, with the command and the server's last output.
/// </summary>
public sealed class StartupView : UserControl
{
    private readonly TextBlock _status = new() { Name = "StartupStatus", TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _error = new() { Name = "StartupError", TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public StartupView()
    {
        Classes.Add("page");
        _status.Classes.Add("muted");
        _error.Classes.Add("mono");
        _error.Classes.Add("small");
        var title = new TextBlock { Text = "harness" };
        title.Classes.Add("brand");
        Content = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 720,
            Margin = new Avalonia.Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { title, _status, _error },
        };
    }

    /// <summary>What it is doing now.</summary>
    public string Status => _status.Text ?? "";

    /// <summary>Why it could not connect, once it failed.</summary>
    public string? Error => _error.IsVisible ? _error.Text : null;

    public void Report(string status) => _status.Text = status;

    public void Fail(string message)
    {
        _status.Text = "Could not reach harness ui.";
        _status.Classes.Remove("muted");
        _status.Classes.Add("error");
        _error.Text = message;
        _error.IsVisible = true;
    }
}
