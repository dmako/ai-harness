using Avalonia.Controls;

namespace Harness.Ui.Views;

public sealed partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        // a ScrollViewer gives its content unlimited height: pin the page to the window, but never below its minimum
        PageScroller.SizeChanged += (_, e) =>
            Page.Height = Math.Max(
                e.NewSize.Height,
                this.TryFindResource("PageMinHeight", out var minimum) && minimum is double m ? m : 0
            );
    }
}
