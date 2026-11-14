using Avalonia;
using Avalonia.Controls;

namespace Harness.Ui.Controls;

/// <summary>
/// One style class from a binding (c:StyleClass.Name="{Binding Css}"): a value such as bp-teal, st-ready or p-high
/// replaces the one it set before, so a row updated in place keeps the right colour.
/// </summary>
public static class StyleClass
{
    public static readonly AttachedProperty<string?> NameProperty = AvaloniaProperty.RegisterAttached<
        StyledElement,
        string?
    >("Name", typeof(StyleClass));

    static StyleClass() =>
        NameProperty.Changed.AddClassHandler<StyledElement>(
            (element, change) =>
            {
                if (change.OldValue is string { Length: > 0 } old)
                {
                    element.Classes.Remove(old);
                }
                if (change.NewValue is string { Length: > 0 } value)
                {
                    element.Classes.Add(value);
                }
            }
        );

    public static string? GetName(StyledElement element) => element.GetValue(NameProperty);

    public static void SetName(StyledElement element, string? value) => element.SetValue(NameProperty, value);
}
