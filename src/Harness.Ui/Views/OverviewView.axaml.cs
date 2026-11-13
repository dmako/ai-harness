using Avalonia;
using Avalonia.Controls;

namespace Harness.Ui.Views;

/// <summary>
/// A project's Overview (<see cref="ViewModels.OverviewViewModel"/>). Its grids follow the width as style.css's
/// .ovtop and .withside do: the rings, the Now card and the proposals tile side by side when wide, the rings over the
/// other two when medium, one column when narrow; the activity beside Needs attention only when wide.
/// </summary>
public sealed partial class OverviewView : UserControl
{
    /// <summary>From this width the activity goes beside Needs attention, and the top is one row.</summary>
    public const double Wide = 1000;

    /// <summary>From this width the Now card and the proposals tile share a row under the rings.</summary>
    public const double Medium = 640;

    private int _arranged = -1;

    public OverviewView()
    {
        InitializeComponent();
        SeeActivity.Click += (_, _) => ActivitySection.BringIntoView();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Arrange(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    private void Arrange(double width)
    {
        var kind = 0;
        if (double.IsInfinity(width) || width >= Wide)
        {
            kind = 2;
        }
        else if (width >= Medium)
        {
            kind = 1;
        }
        if (kind == _arranged)
        {
            return;
        }
        _arranged = kind;
        switch (kind)
        {
            case 2:
                Place(Top, "Auto,*,200", "Auto", (RingCard, 0, 0, 1), (NowCard, 0, 1, 1), (ProposalsTile, 0, 2, 1));
                Place(Columns, "*,340", "Auto", (Main, 0, 0, 1), (Side, 0, 1, 1));
                break;
            case 1:
                Place(Top, "*,200", "Auto,Auto", (RingCard, 0, 0, 2), (NowCard, 1, 0, 1), (ProposalsTile, 1, 1, 1));
                Place(Columns, "*", "Auto,Auto", (Main, 0, 0, 1), (Side, 1, 0, 1));
                break;
            default:
                Place(Top, "*", "Auto,Auto,Auto", (RingCard, 0, 0, 1), (NowCard, 1, 0, 1), (ProposalsTile, 2, 0, 1));
                Place(Columns, "*", "Auto,Auto", (Main, 0, 0, 1), (Side, 1, 0, 1));
                break;
        }
    }

    private static void Place(Grid grid, string columns, string rows, params (Control Child, int Row, int Column, int Span)[] cells)
    {
        grid.ColumnDefinitions = ColumnDefinitions.Parse(columns);
        grid.RowDefinitions = RowDefinitions.Parse(rows);
        foreach (var (child, row, column, span) in cells)
        {
            Grid.SetRow(child, row);
            Grid.SetColumn(child, column);
            Grid.SetColumnSpan(child, span);
        }
    }
}
