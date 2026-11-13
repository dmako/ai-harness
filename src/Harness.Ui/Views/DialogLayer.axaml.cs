using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Harness.Ui.Services;

namespace Harness.Ui.Views;

/// <summary>Shows the DialogService's dialog; keyboard: Esc cancels, Enter accepts, focus goes to the first field.</summary>
public sealed partial class DialogLayer : UserControl
{
    private DialogService? _service;

    public DialogLayer()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, Keys, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _service?.PropertyChanged -= Opened;
        _service = DataContext as DialogService;
        _service?.PropertyChanged += Opened;
    }

    private void Opened(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DialogService.Current) && _service?.Current is not null)
        {
            // after the dialog is laid out: its first field, else the accept button
            Dispatcher.UIThread.Post(
                () =>
                    (
                        Body.GetVisualDescendants()
                            .OfType<Control>()
                            .FirstOrDefault(c => c is TextBox or ComboBox or CheckBox && c.IsEffectivelyEnabled)
                        ?? Accept
                    ).Focus(NavigationMethod.Tab),
                DispatcherPriority.Loaded
            );
        }
    }

    private void Keys(object? sender, KeyEventArgs e)
    {
        if (_service?.Current is not { } dialog)
        {
            return;
        }
        if (e.Key is Key.Escape)
        {
            e.Handled = true;
            dialog.Cancel();
        }
        else if (e.Key is Key.Enter && e.Source is not TextBox { AcceptsReturn: true } && e.Source is not Button)
        {
            e.Handled = true;
            dialog.AcceptCommand.Execute(null);
        }
    }
}
