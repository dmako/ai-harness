using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
    private readonly List<FindHit> _findHits = [];
    private int _findIndex = -1;
    private IInputElement? _findReturn;
    private static event Action? FindRequested;

    public MainView()
    {
        InitializeComponent();
        AddHandler(AppLink.NavigateEvent, Followed);
        Menu.Click += (_, _) => Sidebar.IsVisible = !Sidebar.IsVisible;
        SizeChanged += (_, e) => Layout(e.NewSize.Width);
        PageScroller.SizeChanged += (_, _) => Fit();
        OpenFind.TextChanged += (_, _) => QuickFind();
        FindClose.Click += (_, _) => HideFind();
        FindNext.Click += (_, _) => StepFind(1);
        FindPrevious.Click += (_, _) => StepFind(-1);
        FindInput.TextChanged += (_, _) => Search();
        AddHandler(KeyDownEvent, FindKeys, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => FindRequested += ShowFind;
        DetachedFromVisualTree += (_, _) => FindRequested -= ShowFind;
    }

    /// <summary>Opens find from a browser host that intercepts the browser's own Ctrl+F.</summary>
    public static void RequestFind() => FindRequested?.Invoke();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= PageChanged;
            _model.Navigation.Changed -= RouteChanged;
            _model.Services.Dialogs.PropertyChanged -= DialogChanged;
        }
        _model = DataContext as MainViewModel;
        if (_model is not null)
        {
            var model = _model;
            AppLink.Resolve = href => model.Services.Host.PageLink(href);
            _model.PropertyChanged += PageChanged;
            _model.Navigation.Changed += RouteChanged;
            _model.Services.Dialogs.PropertyChanged += DialogChanged;
        }
    }

    private MainViewModel? _model;

    private void RouteChanged(object? sender, EventArgs e)
    {
        ClearFind();
        FindInput.Text = "";
    }

    private void DialogChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (FindBar.IsVisible && e.PropertyName is nameof(Services.DialogService.Current))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (FindBar.IsVisible)
                {
                    Search();
                }
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void PageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Page) or nameof(MainViewModel.Board))
        {
            ClearFind();
            Fit();
        }
    }

    private void ShowFind()
    {
        if (!FindBar.IsVisible)
        {
            _findReturn = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        }
        FindBar.IsVisible = true;
        Search();
        FindInput.Focus();
        FindInput.SelectAll();
    }

    private void QuickFind()
    {
        if (OpenFind.Text is not { Length: > 0 } query)
        {
            return;
        }
        OpenFind.Text = "";
        FindInput.Text = query;
        ShowFind();
        FindInput.CaretIndex = query.Length;
    }

    private void HideFind()
    {
        ClearFind();
        FindBar.IsVisible = false;
        if (_findReturn is Control { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } previous)
        {
            previous.Focus();
        }
        _findReturn = null;
    }

    private void FindKeys(object? sender, KeyEventArgs e)
    {
        if ((e.Key is Key.F && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            || (e.Source == OpenFind && e.Key is Key.Enter))
        {
            e.Handled = true;
            ShowFind();
        }
        else if (FindBar.IsVisible && e.Key is Key.Escape)
        {
            e.Handled = true;
            HideFind();
        }
        else if (FindBar.IsVisible && e.Source == FindInput && e.Key is Key.Enter)
        {
            e.Handled = true;
            StepFind((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
        }
    }

    private void Search()
    {
        ClearFind();
        var query = FindInput.Text?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            FindCount.Text = "";
            return;
        }
        var root = _model?.Services.Dialogs.IsOpen is true ? (Control)Dialogs : Page;
        foreach (var control in root.GetVisualDescendants().OfType<Control>().Where(t => t.IsEffectivelyVisible))
        {
            var content = TextOf(control);
            for (var offset = 0; offset <= content.Length - query.Length;)
            {
                var found = content.IndexOf(query, offset, StringComparison.CurrentCultureIgnoreCase);
                if (found < 0)
                {
                    break;
                }
                _findHits.Add(new FindHit(control, content, found, query.Length));
                control.Classes.Add("find-hit");
                offset = found + query.Length;
            }
        }
        if (_findHits.Count > 0)
        {
            StepFind(1);
        }
        else
        {
            FindCount.Text = "No matches";
        }
    }

    private static string TextOf(Control control) => control switch
    {
        TextBlock text => text.Text ?? string.Concat(text.Inlines?.Select(InlineText) ?? []),
        TextBox { PasswordChar: '\0' } input => input.Text ?? "",
        _ => "",
    };

    private static string InlineText(Inline inline) => inline switch
    {
        Run run => run.Text ?? "",
        Span span => string.Concat(span.Inlines.Select(InlineText)),
        _ => "",
    };

    private void StepFind(int direction)
    {
        if (_findHits.Count is 0)
        {
            return;
        }
        if (_findIndex >= 0)
        {
            _findHits[_findIndex].Control.Classes.Remove("find-current");
        }
        _findIndex = (_findIndex + direction + _findHits.Count) % _findHits.Count;
        var match = _findHits[_findIndex];
        match.Control.Classes.Add("find-current");
        match.Control.BringIntoView();
        ShowPreview(match);
        FindCount.Text = $"{_findIndex + 1} of {_findHits.Count}";
    }

    private void ShowPreview(FindHit match)
    {
        var start = Math.Max(0, match.Offset - 24);
        var end = Math.Min(match.Text.Length, match.Offset + match.Length + 24);
        FindPreview.Inlines!.Clear();
        FindPreview.Inlines.Add(new Run((start > 0 ? "…" : "") + match.Text[start..match.Offset]));
        var highlighted = new Run(match.Text.Substring(match.Offset, match.Length));
        if (this.TryFindResource("Gold", out var background) && background is Avalonia.Media.IBrush brush)
        {
            highlighted.Background = brush;
        }
        if (this.TryFindResource("OnGold", out var foreground) && foreground is Avalonia.Media.IBrush ink)
        {
            highlighted.Foreground = ink;
        }
        FindPreview.Inlines.Add(highlighted);
        FindPreview.Inlines.Add(new Run(match.Text[(match.Offset + match.Length)..end] + (end < match.Text.Length ? "…" : "")));
    }

    private void ClearFind()
    {
        foreach (var classes in _findHits.Select(match => match.Control.Classes))
        {
            classes.Remove("find-hit");
            classes.Remove("find-current");
        }
        _findHits.Clear();
        _findIndex = -1;
        FindCount.Text = "";
        FindPreview.Inlines?.Clear();
    }

    private sealed record FindHit(Control Control, string Text, int Offset, int Length);

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
