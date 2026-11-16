using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AwesomeAssertions;
using Harness.Ui.Controls;
using Xunit;

namespace Harness.Ui.Tests;

public sealed class SparklineTests
{
    [Theory]
    [InlineData(48, 18)]
    [InlineData(120, 40)]
    public Task SparseRatesFillTheAvailableWidthWithoutDrawingOutside(int width, int height) => Ui.RunAsync(() =>
    {
        var chart = new Sparkline
        {
            Width = width,
            Height = height,
            BrushKey = "SparkTest",
            Line = [null, 0.5, null, 0.5, null],
        };
        chart.Resources.Add("SparkTest", Brushes.Black);
        Canvas.SetLeft(chart, 10);
        Canvas.SetTop(chart, 10);
        var canvas = new Canvas { Background = Brushes.White, Children = { chart } };
        var window = Ui.Show(canvas, width + 20, height + 20);
        Ui.Settle();
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
        var y = 10 + (height / 2);
        IsDark(frame, 12, y).Should().BeTrue("the line starts near the left edge");
        IsDark(frame, 10 + (width / 2), y).Should().BeTrue("missing days do not split the line");
        IsDark(frame, 10 + width - 3, y).Should().BeTrue("the line reaches the right edge");
        IsDark(frame, 9, y).Should().BeFalse("the line stays within the control");
        IsDark(frame, 10 + width, y).Should().BeFalse("the endpoint stays within the control");
        window.Close();
        return Task.CompletedTask;
    });

    private static bool IsDark(WriteableBitmap frame, int x, int y)
    {
        using var buffer = frame.Lock();
        return System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, (y * buffer.RowBytes) + (x * 4) + 1) < 128;
    }
}
