using Avalonia.Controls;
using Avalonia.Interactivity;
using Harness.Ui.Controls;
using Harness.Ui.ViewModels;

namespace Harness.Ui.Views;

/// <summary>
/// The page frame. In-page links (AppLink) navigate through the shell; below 760 px the sidebar leaves the page's
/// side and opens over it from the Projects button, closing again when a link in it is followed.
/// </summary>
public sealed partial class MainView : UserControl
{
    private const double Narrow = 760;
    private bool _narrow;

    public MainView()
    {
        InitializeComponent();
        AddHandler(AppLink.NavigateEvent, Followed);
        Menu.Click += (_, _) => Sidebar.IsVisible = !Sidebar.IsVisible;
        SizeChanged += (_, e) => Layout(e.NewSize.Width);
        PageScroller.SizeChanged += (_, _) => Fit();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= PageChanged;
        }
        _model = DataContext as MainViewModel;
        if (_model is not null)
        {
            var model = _model;
            AppLink.Resolve = href => model.Services.Host.PageLink(href);
            _model.PropertyChanged += PageChanged;
        }
    }

    private MainViewModel? _model;

    private void PageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Page) or nameof(MainViewModel.Board))
        {
            Fit();
        }
    }

    /// <summary>
    /// A ScrollViewer gives its content unlimited height. A page that fills the window and scrolls inside itself (the
    /// Board's columns) is pinned to the window, never below PageMinHeight; every other page grows with its content
    /// and the page scrolls.
    /// </summary>
    private void Fit()
    {
        var minimum = this.TryFindResource("PageMinHeight", out var value) && value is double m ? m : 0;
        var window = Math.Max(PageScroller.Bounds.Height - Page.Margin.Top - Page.Margin.Bottom, minimum);
        if (_model?.Board is not null)
        {
            Page.Height = window;
            Page.MinHeight = 0;
        }
        else
        {
            Page.Height = double.NaN;
            Page.MinHeight = window;
        }
    }

    private void Followed(object? sender, NavigateEventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            model.Navigate(e.Href);
            e.Handled = true;
        }
        if (_narrow)
        {
            Sidebar.IsVisible = false;
        }
    }

    private void Layout(double width)
    {
        var narrow = width < Narrow;
        if (narrow == _narrow && Sidebar.Classes.Contains("placed"))
        {
            return;
        }
        _narrow = narrow;
        Sidebar.Classes.Add("placed");
        Menu.IsVisible = narrow;
        Sidebar.Classes.Set("overlay", narrow);
        Grid.SetColumnSpan(Sidebar, narrow ? 2 : 1);
        Sidebar.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
        Sidebar.IsVisible = !narrow;
        Grid.SetColumn(Sidebar, 0);
        Frame.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : GridLength.Auto;
    }
}
