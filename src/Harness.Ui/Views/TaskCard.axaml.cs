using System.ComponentModel;
using Avalonia.Controls;
using Harness.Ui.ViewModels;

namespace Harness.Ui.Views;

/// <summary>
/// A task's card; its priority and blueprint colour become style classes (p-high, bp-teal), kept current while the
/// card is updated in place by live reloads. Its id opens the task's dialog.
/// </summary>
public sealed partial class TaskCard : UserControl
{
    private TaskCardViewModel? _card;

    public TaskCard()
    {
        InitializeComponent();
        // the id opens the dialog here; Open in new tab and Copy link give the task's page in the current UI
        OpenTask.ContextMenu = Harness.Ui.Controls.Links.MenuFor(OpenTask, () => _card?.Link);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _card?.PropertyChanged -= Changed;
        _card = DataContext as TaskCardViewModel;
        _card?.PropertyChanged += Changed;
        Classify();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskCardViewModel.Priority) or nameof(TaskCardViewModel.BlueprintColor))
        {
            Classify();
        }
    }

    private void Classify()
    {
        Swap(Priority.Classes, "p-", _card?.Priority);
        Swap(BlueprintFrame.Classes, "bp-", _card?.BlueprintColor);
    }

    private static void Swap(Classes classes, string prefix, string? value)
    {
        classes.RemoveAll([.. classes.Where(c => c.StartsWith(prefix, StringComparison.Ordinal))]);
        if (value is { Length: > 0 })
        {
            classes.Add(prefix + value);
        }
    }
}
