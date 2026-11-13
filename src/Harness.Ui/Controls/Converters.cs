using Avalonia.Data.Converters;

namespace Harness.Ui.Controls;

/// <summary>Small value converters the views share.</summary>
public static class Converters
{
    /// <summary>true (reloading) dims content to 0.6 opacity, as app.js's .busy does.</summary>
    public static IValueConverter Dimmed { get; } =
        new FuncValueConverter<bool, double>(busy => busy ? 0.6 : 1.0);
}
