using Avalonia;
using Avalonia.Controls;

namespace Harness.Ui.Views;

/// <summary>
/// Docs (<see cref="ViewModels.DocsPage"/>): the sections beside the text when there is room (style.css .docs),
/// above it when narrow.
/// </summary>
public sealed partial class DocsView : UserControl
{
    /// <summary>From this width the list of sections stands beside the text.</summary>
    public const double Wide = 900;

    private int _arranged = -1;

    public DocsView() => InitializeComponent();

    protected override Size MeasureOverride(Size availableSize)
    {
        var wide = double.IsInfinity(availableSize.Width) || availableSize.Width >= Wide ? 1 : 0;
        if (wide != _arranged)
        {
            _arranged = wide;
            Columns.ColumnDefinitions = ColumnDefinitions.Parse(wide is 1 ? "240,*" : "*");
            Columns.RowDefinitions = RowDefinitions.Parse(wide is 1 ? "Auto" : "Auto,Auto");
            Grid.SetColumn(Article, wide);
            Grid.SetRow(Article, 1 - wide);
        }
        return base.MeasureOverride(availableSize);
    }
}
