using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;

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
            var evidence = _service.Current is EvidenceDialog;
            Accept.Classes.Set("evidence-close", evidence);
            // after the dialog is laid out: a detail that only closes (no Cancel, as app.js taskDialog) its Close; a form
            // its first field, else the accept button
            var detail = !_service.Current.HasCancel;
            Dispatcher.UIThread.Post(
                () =>
                    (
                        (
                            detail
                                ? null
                                : Body.GetVisualDescendants()
                                    .OfType<Control>()
                                    .FirstOrDefault(c => c is TextBox or ComboBox or CheckBox && c.IsEffectivelyEnabled)
                        ) ?? Accept
                    ).Focus(evidence ? NavigationMethod.Unspecified : NavigationMethod.Tab),
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
        var source = e.Source as Avalonia.Visual;
        var picker = source as ComboBox ?? source?.GetVisualAncestors().OfType<ComboBox>().FirstOrDefault();
        if (picker is not null && e.Key is Key.Enter)
        {
            // Let the picker open or accept its choice; Enter must not submit the surrounding form.
            return;
        }
        var openPicker = Body.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.IsDropDownOpen);
        if (openPicker is not null && e.Key is Key.Escape)
        {
            openPicker.IsDropDownOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Escape)
        {
            e.Handled = true;
            dialog.Cancel();
        }
        else if (
            e.Key is Key.Enter
            && e.Source is not TextBox { AcceptsReturn: true }
            && e.Source is not Button
            // a field with its own Enter (a criterion adds the next one, a chip field takes its chip)
            && !(e.Source is TextBox field && field.Classes.Contains("own-enter"))
        )
        {
            e.Handled = true;
            dialog.AcceptCommand.Execute(null);
        }
    }
}
