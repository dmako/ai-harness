using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;

namespace Harness.Ui.Controls;

/// <summary>A rate shown as a ring: its colour token and how far it has come (null: nothing to show yet).</summary>
public sealed record RingValue(string Brush, double? Value);

/// <summary>A day of the activity strip: its agents' calls, the failed ones among them.</summary>
public sealed record DayBar(int Calls, int Failed);

/// <summary>What the charts share: a colour token by name, in the current theme; decorative to screen readers.</summary>
public abstract class Chart : Control
{
    static Chart()
    {
        AutomationProperties.AccessibilityViewProperty.OverrideDefaultValue<Chart>(AccessibilityView.Raw);
        FocusableProperty.OverrideDefaultValue<Chart>(false);
    }

    protected IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            InvalidateVisual();
        }
    }
}

/// <summary>
/// Up to three rates as concentric rings, the outer first (app.js rings()): a track each, and an arc from the top,
/// clockwise, as far as the rate. The legend beside them says the same in words.
/// </summary>
public sealed class Rings : Chart
{
    public static readonly StyledProperty<IReadOnlyList<RingValue>?> ValuesProperty = AvaloniaProperty.Register<
        Rings,
        IReadOnlyList<RingValue>?
    >(nameof(Values));

    private static readonly double[] Radii = [52, 38, 24];

    static Rings() => AffectsRender<Rings>(ValuesProperty);

    public IReadOnlyList<RingValue>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(128, 128);

    public override void Render(DrawingContext context)
    {
        var track = Brush("Grid");
        foreach (var (ring, i) in (Values ?? []).Take(3).Select((r, i) => (r, i)))
        {
            var radius = Radii[i];
            context.DrawEllipse(null, new Pen(track, 11), new Point(64, 64), radius, radius);
            var value = Math.Clamp(ring.Value ?? 0, 0, 1);
            if (value <= 0)
            {
                continue;
            }
            var pen = new Pen(Brush(ring.Brush), 11, lineCap: PenLineCap.Round);
            if (value >= 0.999)
            {
                context.DrawEllipse(null, pen, new Point(64, 64), radius, radius);
                continue;
            }
            var end = (value * 360 - 90) * Math.PI / 180;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(64, 64 - radius), false);
                g.ArcTo(
                    new Point(64 + radius * Math.Cos(end), 64 + radius * Math.Sin(end)),
                    new Size(radius, radius),
                    0,
                    value > 0.5,
                    SweepDirection.Clockwise
                );
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }
}

/// <summary>
/// The window's days in a small picture (app.js sparkline()): bars for counts, or a line for rates where a day
/// without data is a gap, with a dot on the last value.
/// </summary>
public sealed class Sparkline : Chart
{
    public static readonly StyledProperty<IReadOnlyList<double>?> BarsProperty = AvaloniaProperty.Register<
        Sparkline,
        IReadOnlyList<double>?
    >(nameof(Bars));

    public static readonly StyledProperty<IReadOnlyList<double?>?> LineProperty = AvaloniaProperty.Register<
        Sparkline,
        IReadOnlyList<double?>?
    >(nameof(Line));

    public static readonly StyledProperty<string> BrushKeyProperty = AvaloniaProperty.Register<Sparkline, string>(
        nameof(BrushKey),
        "Accent"
    );

    private const double W = 72;
    private const double H = 18;

    static Sparkline() => AffectsRender<Sparkline>(BarsProperty, LineProperty, BrushKeyProperty);

    public IReadOnlyList<double>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public IReadOnlyList<double?>? Line
    {
        get => GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    /// <summary>The colour token it is drawn in.</summary>
    public string BrushKey
    {
        get => GetValue(BrushKeyProperty);
        set => SetValue(BrushKeyProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(W, H);

    public override void Render(DrawingContext context)
    {
        var brush = Brush(BrushKey);
        if (Bars is { Count: > 0 } bars)
        {
            var top = Math.Max(1, bars.Max());
            var width = W / bars.Count;
            var zero = Brush("Axis");
            for (var i = 0; i < bars.Count; i++)
            {
                var height = bars[i] > 0 ? Math.Max(2, bars[i] / top * H) : 1;
                context.FillRectangle(
                    bars[i] > 0 ? brush : zero,
                    new Rect(i * width + 0.5, H - height, Math.Max(width - 1, 1), height)
                );
            }
            return;
        }
        if (Line is not { Count: > 0 } line)
        {
            return;
        }
        var step = W / Math.Max(1, line.Count - 1);
        var pen = new Pen(brush, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        Point? last = null;
        Point? previous = null;
        for (var i = 0; i < line.Count; i++)
        {
            if (line[i] is not { } v)
            {
                previous = null;
                continue;
            }
            var point = new Point(i * step, H - 1 - v * (H - 2));
            if (previous is { } from)
            {
                context.DrawLine(pen, from, point);
            }
            previous = point;
            last = point;
        }
        if (last is { } dot)
        {
            context.DrawEllipse(brush, null, dot, 2, 2);
        }
    }
}

/// <summary>
/// The activity strip's bars (app.js activityStrip()): a column per day as high as its agents' calls against the
/// busiest day, the failed share on top in the critical colour; a day without calls is a short line.
/// </summary>
public sealed class DayBars : Chart
{
    public static readonly StyledProperty<IReadOnlyList<DayBar>?> DaysProperty = AvaloniaProperty.Register<
        DayBars,
        IReadOnlyList<DayBar>?
    >(nameof(Days));

    static DayBars() => AffectsRender<DayBars>(DaysProperty);

    public IReadOnlyList<DayBar>? Days
    {
        get => GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width, 56);

    public override void Render(DrawingContext context)
    {
        if (Days is not { Count: > 0 } days)
        {
            return;
        }
        var most = Math.Max(1, days.Max(d => d.Calls));
        var column = Bounds.Width / days.Count;
        var width = Math.Min(22, column - 6);
        var height = Bounds.Height;
        var calls = Brush("Accent");
        var failed = Brush("Critical");
        var none = Brush("Axis");
        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var x = i * column + (column - width) / 2;
            if (day.Calls is 0)
            {
                context.FillRectangle(none, new Rect(x, height - 2, width, 2));
                continue;
            }
            var bar = Math.Max(height * 0.06, day.Calls / (double)most * height);
            var bad = bar * day.Failed / day.Calls;
            context.FillRectangle(calls, new Rect(x, height - bar + bad, width, bar - bad), 2);
            if (bad > 0)
            {
                context.FillRectangle(failed, new Rect(x, height - bar, width, bad), 2);
            }
        }
    }
}

/// <summary>A small dot in a colour token (a ring's legend, the activity strip's key).</summary>
public sealed class Swatch : Chart
{
    public static readonly StyledProperty<string> BrushKeyProperty = AvaloniaProperty.Register<Swatch, string>(
        nameof(BrushKey),
        "Accent"
    );

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Swatch, double>(nameof(Size), 10);

    static Swatch()
    {
        AffectsRender<Swatch>(BrushKeyProperty);
        AffectsMeasure<Swatch>(SizeProperty);
    }

    public string BrushKey
    {
        get => GetValue(BrushKeyProperty);
        set => SetValue(BrushKeyProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context) =>
        context.DrawEllipse(Brush(BrushKey), null, new Point(Size / 2, Size / 2), Size / 2, Size / 2);
}

/// <summary>A few dots in a colour token, one per thing that happened (the activity strip's changes, up to 4).</summary>
public sealed class Dots : Chart
{
    public static readonly StyledProperty<int> CountProperty = AvaloniaProperty.Register<Dots, int>(nameof(Count));

    public static readonly StyledProperty<string> BrushKeyProperty = AvaloniaProperty.Register<Dots, string>(
        nameof(BrushKey),
        "CDecision"
    );

    static Dots() => AffectsRender<Dots>(CountProperty, BrushKeyProperty);

    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public string BrushKey
    {
        get => GetValue(BrushKeyProperty);
        set => SetValue(BrushKeyProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(4 * 7, 6);

    public override void Render(DrawingContext context)
    {
        var brush = Brush(BrushKey);
        var count = Math.Min(Count, 4);
        var start = (Bounds.Width - count * 7 + 1) / 2;
        for (var i = 0; i < count; i++)
        {
            context.DrawEllipse(brush, null, new Point(start + i * 7 + 3, 3), 3, 3);
        }
    }
}
