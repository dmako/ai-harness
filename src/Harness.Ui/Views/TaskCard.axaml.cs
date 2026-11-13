using Avalonia.Controls;
using Harness.Ui.ViewModels;

namespace Harness.Ui.Views;

/// <summary>A task's card; its priority and blueprint colour become style classes (p-high, bp-teal).</summary>
public sealed partial class TaskCard : UserControl
{
    public TaskCard() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Swap(Priority.Classes, "p-", (DataContext as TaskCardViewModel)?.Priority);
        Swap(BlueprintFrame.Classes, "bp-", (DataContext as TaskCardViewModel)?.BlueprintColor);
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
