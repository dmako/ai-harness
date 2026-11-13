using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using AwesomeAssertions;
using Harness.Ui.Controls;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The design system (TASK-0147): the style gallery passes the UI checks in both themes, every icon resolves and
/// draws, and every text-on-fill pair the component styles use is WCAG AA by its tokens.
/// </summary>
public sealed class DesignSystemTests
{
    private static readonly string[] BlueprintColors =
    [
        "teal", "orange", "blue", "rose", "green", "violet", "amber", "cyan",
        "pink", "lime", "indigo", "red", "fuchsia", "yellow", "slate", "purple",
    ];

    /// <summary>
    /// The text colours each fill carries in Styles/*.axaml. Critical text is drawn only on Surface and Page: the
    /// dark Critical on Surface2 is 4.38:1 (brand.css has the same value), so Surface2 carries only the inks.
    /// </summary>
    private static readonly Dictionary<string, string[]> Pairs = new(StringComparer.Ordinal)
    {
        ["Surface"] =
        [
            "Ink", "Ink2", "Muted", "Accent", "GoodInk", "GoldInk", "Critical", .. BlueprintColors.Select(b => "Bp." + b),
        ],
        ["Page"] = ["Ink", "Ink2", "Muted", "Accent", "Critical"],
        ["Surface2"] = ["Ink", "Ink2", "Muted", .. BlueprintColors.Select(b => "Bp." + b)],
        ["Chip"] = ["Ink", "Ink2"],
        ["Inset"] = ["Ink", "Ink2", "Muted"],
        ["Selected"] = ["Ink", "Ink2", "Muted"],
        ["AccentWash"] = ["Ink"],
        ["Fill"] = ["OnFill"],
        ["Gold"] = ["OnGold"],
        ["GoldHover"] = ["OnGold"],
        ["Critical"] = ["OnCritical"],
    };

    [Fact]
    public Task TheStyleGalleryPassesTheUiChecks() =>
        Ui.RunAsync(() => UiChecks.AssertAsync((width, height) => Task.FromResult(Ui.Show(new StyleGallery(), width, height))));

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public Task EveryTextColourMeetsWcagAaOnTheFillsItIsDrawnOn(string theme) => Ui.RunAsync(() =>
    {
        var variant = theme is "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var problems = new List<string>();
        foreach (var (surface, inks) in Pairs)
        {
            foreach (var ink in inks)
            {
                var ratio = UiChecks.Contrast(Token(ink, variant), Token(surface, variant));
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
    public Task CriticalOnSurface2IsUnderAaInDarkWhichIsWhyNoStyleDrawsIt() => Ui.RunAsync(() =>
    {
        UiChecks.Contrast(Token("Critical", ThemeVariant.Dark), Token("Surface2", ThemeVariant.Dark))
            .Should()
            .BeLessThan(4.5, "if the token changes to pass, Critical may be drawn on Surface2 again");
        return Task.CompletedTask;
    });

    [Fact]
    public Task EveryIconResolvesAndDraws() => Ui.RunAsync(() =>
    {
        var missing = StyleGallery
            .IconNames.Where(name =>
                !Application.Current!.TryGetResource("Icon." + name, ThemeVariant.Light, out _)
                && !Application.Current.TryGetResource("IconFill." + name, ThemeVariant.Light, out _)
            )
            .ToArray();
        missing.Should().BeEmpty();

        var icon = new Icon
        {
            Kind = "trash",
            Size = 24,
            Foreground = Brushes.Black,
        };
        var window = Ui.Show(new Avalonia.Controls.Border { Background = Brushes.White, Child = icon }, 48, 48);
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
        Dark(frame).Should().BeGreaterThan(20, "the trash icon's strokes are drawn");
        window.Close();
        return Task.CompletedTask;
    });

    private static Color Token(string key, ThemeVariant variant) =>
        Application.Current!.Resources.TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"no {key} in {variant}");

    /// <summary>How many pixels of the frame are dark (drawn on white).</summary>
    private static int Dark(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var count = 0;
        var row = new byte[buffer.RowBytes];
        for (var y = 0; y < buffer.Size.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * buffer.RowBytes), row, 0, row.Length);
            for (var x = 0; x < buffer.Size.Width; x++)
            {
                if (row[(x * 4) + 1] < 128)
                {
                    count++;
                }
            }
        }
        return count;
    }
}
