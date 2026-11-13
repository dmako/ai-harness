using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;

namespace Harness.Ui.Tests;

/// <summary>
/// The checks UiBrowserTests make of the JavaScript UI, for any Avalonia view, headless. Every problem is reported
/// at once, prefixed with the size and theme it was found at.
/// <list type="bullet">
/// <item><b>overflow</b>: nothing reaches past the window's sides except inside a scroller that scrolls that way;</item>
/// <item><b>clipped text</b>: a text's lines fit the height it was given, and no clipping ancestor cuts it, except a
/// scroller along the direction it scrolls (what lies beyond is reached by scrolling);</item>
/// <item><b>24 px</b>: controls a pointer or finger must hit are at least 24 px in both directions (WCAG 2.5.8);</item>
/// <item><b>contrast</b>: text against the background actually drawn behind it is at least 4.5:1, 3:1 when large
/// (WCAG 1.4.3); disabled controls are exempt.</item>
/// </list>
/// Popups and scroll bar parts are left out, as the browser checks leave native scroll bars out.
/// </summary>
internal static class UiChecks
{
    /// <summary>Wide, a phone, and a window too short for the page (it must scroll, not squeeze).</summary>
    public static IReadOnlyList<(int Width, int Height)> Sizes { get; } = [(1280, 800), (390, 844), (800, 300)];

    public static IReadOnlyList<ThemeVariant> Themes { get; } = [ThemeVariant.Light, ThemeVariant.Dark];

    /// <summary>
    /// Opens the view with <paramref name="open"/> at every size and theme, checks it and closes it; fails with every
    /// problem found. <paramref name="open"/> returns a shown window with its content loaded.
    /// </summary>
    public static async Task AssertAsync(Func<int, int, Task<Window>> open)
    {
        var problems = new List<string>();
        try
        {
            foreach (var theme in Themes)
            {
                Application.Current!.RequestedThemeVariant = theme;
                foreach (var (width, height) in Sizes)
                {
                    var window = await open(width, height);
                    try
                    {
                        problems.AddRange(Check(window).Select(p => $"[{width}x{height} {theme}] {p}"));
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        }
        problems.Should().BeEmpty("the view must pass the UI checks:\n" + string.Join("\n", problems));
    }

    /// <summary>Every problem the window shows now (laid out and drawn first).</summary>
    public static List<string> Check(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var problems = new List<string>();
        var size = window.ClientSize;
        foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(Shown))
        {
            if (control is not TextBlock && !Interactive(control))
            {
                continue;
            }
            var (visible, cutBy) = Visible(control, window);
            if (visible.Right > size.Width + 1 || visible.X < -1)
            {
                problems.Add($"{Describe(control)} reaches past the page's side ({visible.X:0}–{visible.Right:0} of {size.Width:0} px)");
            }
            if (Interactive(control) && (control.Bounds.Height < 24 - 0.5 || control.Bounds.Width < 24 - 0.5))
            {
                problems.Add($"{Describe(control)} is {control.Bounds.Width:0.#}x{control.Bounds.Height:0.#} px, under 24 px");
            }
            if (control is not TextBlock { Text.Length: > 0 } text)
            {
                continue;
            }
            if (text.TextTrimming == TextTrimming.None)
            {
                var lines = text.TextLayout.Height + text.Padding.Top + text.Padding.Bottom;
                if (lines > text.Bounds.Height + 1)
                {
                    problems.Add($"{Describe(text)} needs {lines:0.#} px but has {text.Bounds.Height:0.#}");
                }
                // a line that does not wrap runs past the width it was given and is cut where it ends
                var line = text.TextLayout.Width + text.Padding.Left + text.Padding.Right;
                if (line > text.Bounds.Width + 1)
                {
                    problems.Add($"{Describe(text)} needs {line:0.#} px wide but has {text.Bounds.Width:0.#}");
                }
                if (cutBy is not null)
                {
                    problems.Add($"{Describe(text)} is cut by {cutBy}");
                }
            }
            if (text.IsEffectivelyEnabled && Contrast(text) is { } ratio && ratio < Required(text))
            {
                problems.Add($"{Describe(text)} has contrast {ratio:0.00}:1, under {Required(text)}:1");
            }
        }
        return problems;
    }

    /// <summary>WCAG 2 contrast ratio of two colours, the first composited over the second.</summary>
    public static double Contrast(Color ink, Color surface)
    {
        var over = Over(ink, surface);
        var l1 = Luminance(over);
        var l2 = Luminance(surface);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    /// <summary>
    /// The part of <paramref name="control"/> that can be seen or scrolled to, and the ancestor that cuts it off (one
    /// that clips to its bounds; a scroller only along a direction it cannot scroll).
    /// </summary>
    private static (Rect Visible, string? CutBy) Visible(Control control, Visual root)
    {
        if (control.TranslatePoint(default, root) is not { } origin)
        {
            return (default, null);
        }
        var rect = new Rect(origin, control.Bounds.Size);
        string? cutBy = null;
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
                cutBy is null
                && (
                    (!scrollsAcross && (rect.X < box.X || rect.Right > box.Right))
                    || (!scrollsDown && (rect.Y < box.Y || rect.Bottom > box.Bottom))
                )
            )
            {
                cutBy = Describe(scroller ?? ancestor);
            }
            // beyond a clipping ancestor only its visible part goes on up; what a scroller hides is reached by scrolling
            var visible = rect.Intersect(box);
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                return (default, cutBy);
            }
            rect = visible;
        }
        return (rect, cutBy);
    }

    /// <summary>The contrast of a text's colour against what is drawn behind it, or null when that is not a solid colour.</summary>
    private static double? Contrast(TextBlock text)
    {
        if (text.Foreground is not ISolidColorBrush ink)
        {
            return null;
        }
        // the backgrounds behind the text, nearest first, down to the first opaque one
        var layers = new List<Color>();
        foreach (var visual in new Visual[] { text }.Concat(text.GetVisualAncestors()))
        {
            var brush = visual switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                ContentPresenter presenter => presenter.Background,
                TemplatedControl templated => templated.Background,
                TextBlock block => block.Background,
                _ => null,
            };
            if (brush is null)
            {
                continue;
            }
            if (brush is not ISolidColorBrush solid)
            {
                return null;
            }
            var color = WithAlpha(solid.Color, solid.Opacity);
            if (color.A is 0)
            {
                continue;
            }
            layers.Add(color);
            if (color.A is 255)
            {
                break;
            }
        }
        if (layers.Count is 0 || layers[^1].A is not 255)
        {
            return null;
        }
        var surface = layers[^1];
        for (var i = layers.Count - 2; i >= 0; i--)
        {
            surface = Over(layers[i], surface);
        }
        return Contrast(WithAlpha(ink.Color, ink.Opacity * text.Opacity), surface);
    }

    private static double Required(TextBlock text) =>
        text.FontSize >= 24 || (text.FontSize >= 18.66 && text.FontWeight >= FontWeight.Bold) ? 3 : 4.5;

    private static bool Interactive(Control control) =>
        control is Button or ComboBox or HyperlinkButton or ToggleButton or TextBox or CheckBox or RadioButton
        && control.FindAncestorOfType<ComboBox>() is null;

    /// <summary>
    /// On screen and the page's own: visible, with a size, not in a popup and not a scroll bar's part (the browser
    /// checks leave native scroll bars out too; Fluent's arrows are 16 px and shown only on hover).
    /// </summary>
    private static bool Shown(Control control) =>
        control.IsEffectivelyVisible
        && control.Bounds is { Width: > 0, Height: > 0 }
        && control.FindLogicalAncestorOfType<Popup>() is null
        && control.FindAncestorOfType<ScrollBar>() is null
        && control is not ScrollBar;

    private static string Describe(Control control) =>
        control switch
        {
            TextBlock { Text: { } t } => $"'{(t.Length > 40 ? t[..40] + "…" : t)}'",
            { Name: { Length: > 0 } name } => name,
            _ => control.GetType().Name,
        };

    private static Color WithAlpha(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1), MidpointRounding.AwayFromZero), color.R, color.G, color.B);

    private static Color Over(Color top, Color bottom)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte b) => (byte)Math.Round((t * a) + (b * (1 - a)), MidpointRounding.AwayFromZero);
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }
}
