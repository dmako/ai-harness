using Avalonia;
using Avalonia.Controls;

namespace Harness.Ui.Views;

/// <summary>
/// A project's Board (<see cref="ViewModels.BoardViewModel"/>). Its columns are as wide as style.css's .board makes
/// them: sharing the width, at least ColumnWidth (13.5rem) each; below 47.5em of window width each is 85% of the
/// window (at least 15rem), so one column and the edge of the next are in view.
/// </summary>
public sealed partial class BoardView : UserControl
{
    public static readonly StyledProperty<double> ColumnSizeProperty = AvaloniaProperty.Register<BoardView, double>(
        nameof(ColumnSize),
        216
    );

    /// <summary>The window width below which columns are 85% of it (style.css: max-width 47.5em).</summary>
    public const double Narrow = 760;

    /// <summary>A column's least width on a narrow screen (15rem).</summary>
    public const double NarrowMinimum = 240;

    /// <summary>What the scroller's frame takes of the width the Board is given.</summary>
    public const double Slack = 8;

    /// <summary>The gap between columns (StackPanel spacing).</summary>
    public const double Gap = 12;

    public BoardView() => InitializeComponent();

    /// <summary>Each column's width, set from the space the Board has and the window's width.</summary>
    public double ColumnSize
    {
        get => GetValue(ColumnSizeProperty);
        set => SetValue(ColumnSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var least = this.TryFindResource("ColumnWidth", out var value) && value is double width ? width : 216;
        var window = TopLevel.GetTopLevel(this)?.ClientSize.Width ?? availableSize.Width;
        var count = (DataContext as ViewModels.BoardViewModel)?.Columns.Count ?? 0;
        double size;
        if (window < Narrow)
        {
            size = Math.Max(NarrowMinimum, window * 0.85);
        }
        else if (count > 0 && !double.IsInfinity(availableSize.Width))
        {
            // a few pixels of slack: the scroller's frame takes them, and a scrollbar for nothing is noise
            size = Math.Max(least, Math.Floor((availableSize.Width - Slack - Gap * (count - 1)) / count));
        }
        else
        {
            size = least;
        }
        if (Math.Abs(size - ColumnSize) > 0.5)
        {
            ColumnSize = size;
        }
        return base.MeasureOverride(availableSize);
    }
}
