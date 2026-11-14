using Avalonia;
using Avalonia.Controls;

namespace Harness.Ui.Controls;

/// <summary>
/// A panel beside a map (a layer graph, the build map, a list) that goes under it on a narrow page: set
/// <c>SidePanel.Follows</c> on a grid with the map in column 0 and the panel, its last child, in column 1; the grid
/// needs a second row for the narrow layout. The panel keeps the width it was given while beside the map.
/// </summary>
public sealed class SidePanel : AvaloniaObject
{
    /// <summary>Narrower than this, the panel goes under the map.</summary>
    public const double Narrow = 720;

    public static readonly AttachedProperty<bool> FollowsProperty = AvaloniaProperty.RegisterAttached<SidePanel, Grid, bool>("Follows");

    private static readonly AttachedProperty<double> WideProperty = AvaloniaProperty.RegisterAttached<SidePanel, Control, double>("Wide", double.NaN);

    static SidePanel() =>
        FollowsProperty.Changed.AddClassHandler<Grid>((grid, e) =>
        {
            if (e.NewValue is true)
            {
                grid.SizeChanged += Place;
            }
            else
            {
                grid.SizeChanged -= Place;
            }
        });

    private SidePanel() { }

    public static bool GetFollows(Grid grid) => grid.GetValue(FollowsProperty);

    public static void SetFollows(Grid grid, bool value) => grid.SetValue(FollowsProperty, value);

    private static void Place(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid { Children.Count: > 1 } grid || grid.Children[^1] is not Control side)
        {
            return;
        }
        if (double.IsNaN(side.GetValue(WideProperty)))
        {
            side.SetValue(WideProperty, side.Width);
        }
        var below = e.NewSize.Width < Narrow;
        Grid.SetRow(side, below ? 1 : 0);
        Grid.SetColumn(side, below ? 0 : 1);
        Grid.SetColumnSpan(side, below ? 2 : 1);
        side.Width = below ? double.NaN : side.GetValue(WideProperty);
        side.Margin = below ? new Thickness(0, 12, 0, 0) : default;
    }
}
