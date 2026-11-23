using Avalonia;
using Avalonia.Controls;

namespace Harness.Ui.Controls;

/// <summary>
/// Cards in as many equal columns as fit (style.css <c>grid-template-columns: repeat(auto-fit, minmax(min(W, 100%),
/// 1fr))</c>, <c>align-items: start</c>): each column at least <see cref="MinColumnWidth"/> wide, or one column when
/// even that does not fit; a row is as tall as its tallest child and each child keeps its own height.
/// </summary>
public sealed class AutoGrid : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<AutoGrid, double>(nameof(MinColumnWidth), 320);

    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<AutoGrid, double>(nameof(Spacing), 12);

    static AutoGrid() => AffectsMeasure<AutoGrid>(MinColumnWidthProperty, SpacingProperty);

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private (int Columns, double Width) Columns(double available)
    {
        var shown = Children.Count(c => c.IsVisible);
        if (double.IsInfinity(available) || shown is 0)
        {
            return (Math.Max(1, shown), MinColumnWidth);
        }
        var columns = Math.Max(1, (int)Math.Floor((available + Spacing) / (MinColumnWidth + Spacing)));
        return (columns, Math.Max(0, (available - (Spacing * (columns - 1))) / columns));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, width) = Columns(availableSize.Width);
        var height = 0.0;
        var row = 0.0;
        var index = 0;
        foreach (var child in Children.Where(c => c.IsVisible))
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            row = Math.Max(row, child.DesiredSize.Height);
            if (++index % columns is 0)
            {
                height += row + Spacing;
                row = 0;
            }
        }
        height += index % columns is 0 ? -Spacing : row;
        var used = double.IsInfinity(availableSize.Width) ? (width * columns) + (Spacing * (columns - 1)) : availableSize.Width;
        return new Size(used, Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, width) = Columns(finalSize.Width);
        var shown = Children.Where(c => c.IsVisible).ToArray();
        var y = 0.0;
        for (var start = 0; start < shown.Length; start += columns)
        {
            var row = shown.Skip(start).Take(columns).ToArray();
            for (var i = 0; i < row.Length; i++)
            {
                row[i].Arrange(new Rect(i * (width + Spacing), y, width, row[i].DesiredSize.Height));
            }
            y += row.Max(c => c.DesiredSize.Height) + Spacing;
        }
        return finalSize;
    }
}
