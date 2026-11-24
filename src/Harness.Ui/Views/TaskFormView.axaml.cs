using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Harness.Ui.ViewModels;

namespace Harness.Ui.Views;

/// <summary>
/// The body of its dialog (the dialog layer draws its title, buttons and error), with the original form's keys
/// (app.js listBuilder, tokenInput, taskPicker): in a criterion Enter adds the next one and Backspace in an empty one
/// removes it; in Scope Enter takes the typed path; in Depends on Enter picks; Backspace in an empty chip field drops
/// its last chip.
/// </summary>
public sealed partial class TaskFormView : UserControl
{
    public TaskFormView() => InitializeComponent();

    private TaskFormDialog? Form => DataContext as TaskFormDialog;

    private void CriterionKey(object? sender, KeyEventArgs e)
    {
        if (Form is not { } form || sender is not TextBox { DataContext: LineItem line })
        {
            return;
        }
        if (e.Key is Key.Enter)
        {
            e.Handled = true;
            FocusCriterion(form.InsertCriterionAfter(line));
        }
        else if (e.Key is Key.Back && form.RemoveEmptyCriterion(line) is { } previous)
        {
            e.Handled = true;
            FocusCriterion(previous);
        }
    }

    /// <summary>Puts the caret in <paramref name="line"/>'s field once it is laid out.</summary>
    private void FocusCriterion(LineItem line) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                var field = CriteriaField.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => ReferenceEquals(t.DataContext, line));
                field?.Focus(NavigationMethod.Tab);
                field?.CaretIndex = field.Text?.Length ?? 0;
            },
            DispatcherPriority.Loaded
        );

    private void ScopeKey(object? sender, KeyEventArgs e)
    {
        if (Form is not { } form)
        {
            return;
        }
        if (e.Key is Key.Enter)
        {
            e.Handled = true;
            form.AddScopeCommand.Execute(null);
        }
        else if (e.Key is Key.Back && form.ScopeText.Length is 0)
        {
            e.Handled = true;
            form.RemoveLastScope();
        }
    }

    private void ScopeBlur(object? sender, RoutedEventArgs e) => Form?.AddScopeCommand.Execute(null);

    private void DependsKey(object? sender, KeyEventArgs e)
    {
        if (Form is not { } form)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                form.PickDependency();
                break;
            case Key.Back when form.DependsQuery.Length is 0:
                e.Handled = true;
                form.RemoveLastDependency();
                break;
            case Key.Escape when form.DependsOpen:
                e.Handled = true;
                form.DependsOpen = false;
                break;
        }
    }

    private void DependsFocus(object? sender, FocusChangedEventArgs e) => Form?.DependsOpen = true;

    private void DependsBlur(object? sender, RoutedEventArgs e) => Form?.DependsOpen = false;
}
