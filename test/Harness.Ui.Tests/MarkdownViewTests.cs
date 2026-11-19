using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Controls;
using Xunit;

namespace Harness.Ui.Tests;

public sealed class MarkdownViewTests
{
    [Fact]
    public Task ViewerRendersMarkdownAndMermaidUsingThemeResources() =>
        Ui.RunAsync(async () =>
        {
            var viewer = new HarnessMarkdown
            {
                Markdown =
                    "# Heading 😀\n\n| A | B |\n|---|---|\n| one | two |\n\n```mermaid\nflowchart LR\nA --> B\n```",
                IsLazyRenderingEnabled = false,
            };
            var window = Ui.Show(viewer, 900, 700);
            await Ui.UntilAsync(() =>
                viewer
                    .GetVisualDescendants()
                    .OfType<Border>()
                    .Any(border => border.Classes.Contains("markdown-mermaid"))
            );
            await Ui.UntilAsync(() =>
                viewer
                    .GetVisualDescendants()
                    .OfType<Border>()
                    .Where(border => border.Classes.Contains("markdown-mermaid"))
                    .Select(border => border.Child)
                    .OfType<Image>()
                    .Any(image => image.Source is not null)
            );
            viewer
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Should()
                .Contain(text => text.Classes.Contains("markdown-h1"));
            viewer
                .GetVisualDescendants()
                .OfType<Grid>()
                .Should()
                .Contain(grid => grid.Classes.Contains("markdown-table"));
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = theme;
                Ui.Settle();
                viewer.TryFindResource("MarkdownMermaidAccent", theme, out var accent).Should().BeTrue();
                viewer.TryFindResource("Accent", theme, out var expected).Should().BeTrue();
                accent
                    .Should()
                    .BeAssignableTo<ISolidColorBrush>()
                    .Which.Color.Should()
                    .Be(expected.Should().BeAssignableTo<ISolidColorBrush>().Which.Color);
            }
            window.Close();
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        });
}
