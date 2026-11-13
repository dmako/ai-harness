using Avalonia.Controls;

namespace Harness.Ui.Views;

/// <summary>Every design-system component and icon once (TASK-0147), for the UI checks and for looking at a change.</summary>
public sealed partial class StyleGallery : UserControl
{
    public StyleGallery()
    {
        InitializeComponent();
        Icons.ItemsSource = IconNames;
    }

    /// <summary>Every icon in Styles/Icons.axaml (app.js's ICONS and the status marks).</summary>
    public static IReadOnlyList<string> IconNames { get; } =
    [
        "home", "search", "knowledge", "machine", "docs", "workspace", "edit", "refresh", "close", "st-ok", "st-warn",
        "st-fail", "trash", "plus", "feature", "bug", "chore", "spike", "test", "process", "type-other", "up", "down",
        "p-low", "p-medium", "p-high", "p-critical", "check", "inbox", "calm", "status-ready", "status-claimed",
        "status-in_progress", "status-review", "status-blocked", "status-backlog", "status-done", "status-deferred",
        "level-info", "copy",
    ];
}
