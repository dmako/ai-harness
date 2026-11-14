using Avalonia;
using Avalonia.Controls;

namespace Harness.Ui.Views;

/// <summary>A project's Architecture (<see cref="ViewModels.ArchitectureViewModel"/>).</summary>
public sealed partial class ArchitectureView : UserControl
{
    /// <summary>Narrower than this, the selected unit's panel goes under the layer graph instead of beside it.</summary>
    public const double Narrow = 720;

    public ArchitectureView()
    {
        InitializeComponent();
        LayersGrid.SizeChanged += (_, e) => Place(e.NewSize.Width);
    }

    private void Place(double width)
    {
        var below = width < Narrow;
        Grid.SetRow(Side, below ? 1 : 0);
        Grid.SetColumn(Side, below ? 0 : 1);
        Grid.SetColumnSpan(Side, below ? 2 : 1);
        Side.Width = below ? double.NaN : 300;
        Side.Margin = below ? new Thickness(0, 12, 0, 0) : default;
    }
}
