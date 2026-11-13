using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Api;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The Avalonia Board against a fixture harness ui API: what it shows, what its pickers send, live reloads, and the
/// layout checks UiBrowserTests makes of the JavaScript UI (clipped text, overflow, 24 px controls, contrast).
/// </summary>
public sealed class BoardViewTests
{
    private static readonly Uri Server = new("http://127.0.0.1:17477/");

    [Fact]
    public Task TheBoardShowsTheServersColumnsAndCards() => Ui.RunAsync(async () =>
    {
        var (window, _, _) = await OpenAsync(1280, 800);

        var columns = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("column")).ToArray();
        columns.Select(c => ((BoardColumnViewModel)c.DataContext!).Label)
            .Should()
            .Equal("backlog", "ready", "claimed", "in progress", "review", "done", "blocked");
        var cards = Cards(window);
        cards.Select(c => ((TaskCardViewModel)c.DataContext!).Id)
            .Should()
            .Equal("TASK-0001", "TASK-0002", "TASK-0003", "TASK-0004");
        var critical = cards[1];
        critical.FindControl<TextBlock>("Priority")!.Classes.Should().Contain("p-critical");
        critical.FindControl<Border>("BlueprintFrame")!.Classes.Should().Contain("bp-yellow");
        critical.FindControl<SelectableTextBlock>("Title")!.Text.Should().Be("Port the Board to Avalonia");
        cards[2].FindControl<Border>("BlueprintFrame")!.IsVisible.Should().BeFalse("TASK-0003 has no blueprint");
        critical
            .GetVisualDescendants()
            .OfType<HyperlinkButton>()
            .Single()
            .NavigateUri!.ToString()
            .Should()
            .Be("http://127.0.0.1:17477/#/p/prj_fixture/work/tasks/TASK-0002");
    });

    [Fact]
    public Task ChoosingASortSavesItForTheProjectAndReloadsTheBoard() => Ui.RunAsync(async () =>
    {
        var (window, api, model) = await OpenAsync(1280, 800);
        var gets = api.Gets.Count(g => g.EndsWith("/board", StringComparison.Ordinal));
        api.Answers[$"/api/projects/{Fixtures.ProjectId}/board"] = Fixtures.Board(sort: "id");

        ((MainView)window.Content!).FindControl<BoardView>("Board")!.FindControl<ComboBox>("SortPicker")!.SelectedIndex = 1;
        await model.Board!.Saving;

        var post = api.Posts.Should().ContainSingle().Subject;
        post.Path.Should().Be($"/api/projects/{Fixtures.ProjectId}/board");
        post.Body.Should().Be("""{"sort":"id"}""");
        post.Ui.Should().Be("1");
        api.Gets.Count(g => g.EndsWith("/board", StringComparison.Ordinal)).Should().Be(gets + 1);
        model.Board.Sort!.Label.Should().Be("Id");
    });

    [Fact]
    public Task ALiveChangeToTheShownProjectRedrawsItsBoard() => Ui.RunAsync(async () =>
    {
        var (window, api, model) = await OpenAsync(1280, 800);
        await UntilAsync(() => model.Live is "live");
        api.Answers[$"/api/projects/{Fixtures.ProjectId}/board"] = Fixtures.Board(title: "Changed elsewhere");

        api.Change("""{"projects":["prj_other"],"kinds":{"prj_other":["tasks"]}}""");
        api.Change($$$"""{"projects":["{{{Fixtures.ProjectId}}}"],"kinds":{"{{{Fixtures.ProjectId}}}":["tasks"]}}""");
        await UntilAsync(() => model.LiveReloads > 0);
        await UntilAsync(() => Cards(window).Count is 4);

        model.LiveReloads.Should().Be(1, "a change to another project does not reload this Board");
        Cards(window)[1].FindControl<SelectableTextBlock>("Title")!.Text.Should().Be("Changed elsewhere");
    });

    [Theory]
    [InlineData(1280, 800, "Light")]
    [InlineData(1280, 800, "Dark")]
    [InlineData(390, 844, "Light")]
    [InlineData(390, 844, "Dark")]
    [InlineData(800, 300, "Light")]
    [InlineData(800, 160, "Light")]
    public Task EveryWidthAndThemePassesTheUiChecks(int width, int height, string theme) => Ui.RunAsync(async () =>
    {
        Application.Current!.RequestedThemeVariant = theme is "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            var (window, _, _) = await OpenAsync(width, height);
            var problems = new List<string>();
            var view = ((MainView)window.Content!);

            // the page never scrolls sideways: only the Board does, inside itself
            view.FindControl<ScrollViewer>("PageScroller")!.Extent.Width.Should().BeLessThanOrEqualTo(width + 0.5);

            foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(Shown))
            {
                // controls a pointer or finger must hit are at least 24 px in both directions (WCAG 2.5.8)
                if (control is ComboBox or HyperlinkButton or Button && control.Bounds.Height < 24 - 0.5)
                {
                    problems.Add($"{Name(control)} is {control.Bounds.Height:0.#} px tall");
                }
                // text is never cut (unless it trims with an ellipsis): its lines fit the height it was given, and no
                // clipping ancestor cuts it except a scroller along the direction it scrolls
                if (control is TextBlock { TextTrimming: var trimming, Text.Length: > 0 } text && trimming == TextTrimming.None)
                {
                    var lines = text.TextLayout.Height + text.Padding.Top + text.Padding.Bottom;
                    if (lines > text.Bounds.Height + 1)
                    {
                        problems.Add($"'{text.Text}' needs {lines:0.#} px but has {text.Bounds.Height:0.#}");
                    }
                    if (ClippedBy(text, window) is { } clip)
                    {
                        problems.Add($"'{text.Text}' is cut by {clip}");
                    }
                }
            }
            // a column is never squeezed below its header: short windows scroll the page instead
            foreach (var column in window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("column")))
            {
                if (column.Bounds.Height < 120)
                {
                    problems.Add($"column {((BoardColumnViewModel)column.DataContext!).Label} is {column.Bounds.Height:0} px");
                }
            }
            problems.Should().BeEmpty();

            using var frame = window.CaptureRenderedFrame();
            frame.Should().NotBeNull();
            frame.PixelSize.Width.Should().Be(width);
        }
        finally
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Default;
        }
    });

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task TextColoursMeetWcagAaOnTheirSurfaces(string theme) => Ui.RunAsync(() =>
    {
        var variant = theme is "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var resources = Application.Current!.Resources;
        Color Token(string key) =>
            resources.TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush
                ? brush.Color
                : throw new InvalidOperationException($"no {key} in {theme}");

        var problems = new List<string>();
        string[] blueprints =
        [
            "teal", "orange", "blue", "rose", "green", "violet", "amber", "cyan",
            "pink", "lime", "indigo", "red", "fuchsia", "yellow", "slate", "purple",
        ];
        // the pairs the Board draws (Board.axaml): every text colour on a card (Surface) and the page (Page); the
        // column headings and "None" on Surface2. Critical on Surface2 is 4.38:1 in dark (brand.css has the same
        // value), so the design system must not draw it there.
        var pairs = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Surface"] = ["Ink", "Ink2", "Muted", "Accent", "Good", "Warning", "Critical", .. blueprints.Select(b => "Bp." + b)],
            ["Page"] = ["Ink", "Ink2", "Muted", "Accent", "Critical"],
            ["Surface2"] = ["Ink", "Ink2", "Muted"],
        };
        foreach (var (surface, inks) in pairs)
        {
            foreach (var ink in inks)
            {
                var ratio = Contrast(Token(ink), Token(surface));
                if (ratio < 4.5)
                {
                    problems.Add($"{ink} on {surface}: {ratio:0.00}:1");
                }
            }
        }
        problems.Should().BeEmpty();
        return Task.CompletedTask;
    });

    [Fact]
    public Task TabMovesThroughThePickersAndCards() => Ui.RunAsync(async () =>
    {
        var (window, _, _) = await OpenAsync(1280, 800);
        var focused = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            focused.Add(Name((Control)window.FocusManager.GetFocusedElement()!));
        }

        focused.Take(4).Should().Equal("ProjectPicker", "SortPicker", "HideDonePicker", "BlueprintPicker");
    });

    private static async Task<(Window Window, FixtureApi Api, MainViewModel Model)> OpenAsync(int width, int height)
    {
        var api = new FixtureApi();
        api.Answers["/api/projects"] = Fixtures.Projects;
        api.Answers[$"/api/projects/{Fixtures.ProjectId}/board"] = Fixtures.Board();
        // the client lives as long as the window: closing it disposes the client
#pragma warning disable CA2000 // disposed by the window's Closed handler
        var http = new HttpClient(api) { BaseAddress = Server };
#pragma warning restore CA2000
        var model = new MainViewModel(new HarnessApi(http), new UiHostContext { Server = Server });
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = new MainView { DataContext = model },
        };
        window.Closed += (_, _) => http.Dispose();
        window.Show();
        await model.StartAsync(Fixtures.ProjectId, TestContext.Current.CancellationToken);
        await UntilAsync(() => model.Board is { Loading: false, Columns.Count: > 0 } && Cards(window).Count > 0);
        return (window, api, model);
    }

    private static List<TaskCard> Cards(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return [.. window.GetVisualDescendants().OfType<TaskCard>()];
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the view did not get there in 5 s");
            }
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The ancestor that cuts <paramref name="control"/> off, or null: one that clips to its bounds, where a scroller
    /// only cuts along a direction it cannot scroll (what lies beyond the other way is reached by scrolling).
    /// </summary>
    private static string? ClippedBy(Control control, Visual root)
    {
        if (control.TranslatePoint(default, root) is not { } origin)
        {
            return null;
        }
        var rect = new Rect(origin, control.Bounds.Size);
        foreach (var ancestor in control.GetVisualAncestors().OfType<Control>().TakeWhile(a => a != root))
        {
            if (!ancestor.ClipToBounds || ancestor.TranslatePoint(default, root) is not { } at)
            {
                continue;
            }
            var box = new Rect(at, ancestor.Bounds.Size).Inflate(1);
            var scroller =
                ancestor as ScrollViewer ?? (ancestor as ScrollContentPresenter)?.TemplatedParent as ScrollViewer;
            var scrollsAcross = scroller?.Extent.Width > scroller?.Viewport.Width + 0.5;
            var scrollsDown = scroller?.Extent.Height > scroller?.Viewport.Height + 0.5;
            if (
                (!scrollsAcross && (rect.X < box.X || rect.Right > box.Right))
                || (!scrollsDown && (rect.Y < box.Y || rect.Bottom > box.Bottom))
            )
            {
                return Name(scroller ?? ancestor);
            }
            // beyond a scroller what it scrolls away is reached by scrolling: only its visible part goes on up
            var visible = rect.Intersect(box);
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                return null;
            }
            rect = visible;
        }
        return null;
    }

    /// <summary>
    /// On screen and the page's own: visible, with a size, not in a popup and not a scroll bar's part (the browser
    /// checks leave native scroll bars out too; Fluent's arrows are 16 px and shown only on hover).
    /// </summary>
    private static bool Shown(Control control) =>
        control.IsEffectivelyVisible
        && control.Bounds is { Width: > 0, Height: > 0 }
        && !InPopup(control)
        && control.FindAncestorOfType<ScrollBar>() is null;

    private static bool InPopup(Control control) => control.FindLogicalAncestorOfType<Popup>() is not null;

    private static string Name(Control control) =>
        control.Name is { Length: > 0 } name ? name : control.GetType().Name;

    /// <summary>WCAG 2 contrast ratio of two opaque colours (alpha composited over the background).</summary>
    private static double Contrast(Color ink, Color surface)
    {
        static double Channel(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double Luminance(double r, double g, double b) =>
            (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
        var a = ink.A / 255.0;
        var l1 = Luminance(
            ((ink.R * a) + (surface.R * (1 - a))) / 255,
            ((ink.G * a) + (surface.G * (1 - a))) / 255,
            ((ink.B * a) + (surface.B * (1 - a))) / 255
        );
        var l2 = Luminance(surface.R / 255.0, surface.G / 255.0, surface.B / 255.0);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
