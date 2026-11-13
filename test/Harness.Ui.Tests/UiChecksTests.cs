using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AwesomeAssertions;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>UiChecks itself: it must find seeded problems, or a view passing it proves nothing.</summary>
public sealed class UiChecksTests
{
    [Fact]
    public Task TextSqueezedBelowItsLinesIsReported() => Ui.RunAsync(() =>
    {
        var squeezed = new Border
        {
            Height = 8,
            ClipToBounds = true,
            Child = new TextBlock { Text = "Squeezed heading", FontSize = 14 },
        };
        var window = Ui.Show(new StackPanel { Children = { squeezed } }, 400, 300);

        UiChecks.Check(window).Should().Contain(p => p.Contains("'Squeezed heading'", StringComparison.Ordinal));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task TextCutByAClippingAncestorIsReported() => Ui.RunAsync(() =>
    {
        var cut = new Border
        {
            Width = 60,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = "A label wider than its box", TextWrapping = TextWrapping.NoWrap },
        };
        var window = Ui.Show(new StackPanel { Children = { cut } }, 400, 300);

        UiChecks.Check(window).Should().Contain(p => p.Contains("'A label wider than its box' needs", StringComparison.Ordinal));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task ContentReachingPastThePageIsReported() => Ui.RunAsync(() =>
    {
        var wide = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = "far right", Margin = new Avalonia.Thickness(600, 0, 0, 0) } },
        };
        var window = Ui.Show(wide, 390, 300);

        UiChecks.Check(window).Should().Contain(p => p.Contains("reaches past the page's side", StringComparison.Ordinal));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task WideContentInsideASidewaysScrollerIsNotOverflow() => Ui.RunAsync(() =>
    {
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = "scrolled to", Margin = new Avalonia.Thickness(600, 0, 0, 0) },
        };
        var window = Ui.Show(scroller, 390, 300);

        UiChecks.Check(window).Should().BeEmpty();
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task SmallControlsAndLowContrastAreReported() => Ui.RunAsync(() =>
    {
        var panel = new StackPanel
        {
            Background = Brushes.White,
            Children =
            {
                new Button { Content = "x", Height = 16, MinHeight = 0, Padding = default },
                new TextBlock { Text = "Faint text", Foreground = new SolidColorBrush(Color.Parse("#c8c8c8")) },
                new TextBlock { Text = "Readable text", Foreground = Brushes.Black },
            },
        };
        var window = Ui.Show(panel, 400, 300);

        var problems = UiChecks.Check(window);
        problems.Should().Contain(p => p.Contains("under 24 px", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("'Faint text' has contrast", StringComparison.Ordinal));
        problems.Should().NotContain(p => p.Contains("'Readable text'", StringComparison.Ordinal));
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task AssertReportsEveryProblemWithItsSizeAndTheme() => Ui.RunAsync(async () =>
    {
        var failure = await FluentActions
            .Awaiting(() =>
                UiChecks.AssertAsync(
                    (width, height) =>
                        Task.FromResult(
                            Ui.Show(
                                new Border
                                {
                                    Height = 8,
                                    ClipToBounds = true,
                                    VerticalAlignment = VerticalAlignment.Top,
                                    Child = new TextBlock { Text = "Squeezed" },
                                },
                                width,
                                height
                            )
                        )
                )
            )
            .Should()
            .ThrowAsync<Exception>();

        var message = failure.Which.Message;
        foreach (var where in new[] { "[1280x800 Light]", "[390x844 Light]", "[800x300 Dark]" })
        {
            message.Should().Contain(where);
        }
    });
}
