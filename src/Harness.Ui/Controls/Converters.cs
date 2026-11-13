using System.Globalization;
using Avalonia.Data.Converters;

namespace Harness.Ui.Controls;

/// <summary>Small value converters the views share.</summary>
public static class Converters
{
    /// <summary>true (reloading) dims content to 0.6 opacity, as app.js's .busy does.</summary>
    public static IValueConverter Dimmed { get; } =
        new FuncValueConverter<bool, double>(busy => busy ? 0.6 : 1.0);

    /// <summary>A workspace's short name to its page's address.</summary>
    public static IValueConverter WorkspaceHref { get; } =
        new FuncValueConverter<string?, string?>(name => name is null ? null : Navigation.Route.WorkspaceHref(name));

    /// <summary>Whether the value's text is the parameter's (a style class for one state, one page).</summary>
    public static IValueConverter Is { get; } = new EqualsConverter();

    private sealed class EqualsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
