using Avalonia;
using Avalonia.Controls;

namespace Harness.Ui.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private bool? _wide;

    protected override Size MeasureOverride(Size availableSize)
    {
        var wide = double.IsInfinity(availableSize.Width) || availableSize.Width >= 1000;
        if (_wide != wide)
        {
            _wide = wide;
            Columns.ColumnDefinitions = ColumnDefinitions.Parse(wide ? "*,340" : "*");
            Columns.RowDefinitions = RowDefinitions.Parse(wide ? "Auto" : "Auto,Auto");
            Grid.SetColumn(ActivitySection, wide ? 1 : 0);
            Grid.SetRow(ActivitySection, wide ? 0 : 1);
            MainColumn.Margin = wide ? new Thickness(0, 0, 12, 0) : default;
        }
        return base.MeasureOverride(availableSize);
    }
}
