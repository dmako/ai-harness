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

    /// <summary>The token's brush; no token (a binding not set yet) draws nothing.</summary>
    protected IBrush Brush(string? key)
    {
        if (key is not { Length: > 0 })
        {
            return Brushes.Transparent;
        }
        return this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;
    }

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

/// <summary>A part of a stacked bar: its colour token and how much of the whole it is.</summary>
public sealed record BarSegment(string Brush, int Count);

/// <summary>
/// How a whole divides (app.js taskBar): one rounded bar, a segment per part as wide as its share, in order. The
/// legend beside it names the parts in words.
/// </summary>
public sealed class StackBar : Chart
{
    public static readonly StyledProperty<IReadOnlyList<BarSegment>?> SegmentsProperty = AvaloniaProperty.Register<
        StackBar,
        IReadOnlyList<BarSegment>?
    >(nameof(Segments));

    static StackBar() => AffectsRender<StackBar>(SegmentsProperty);

    public IReadOnlyList<BarSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, 8);

    public override void Render(DrawingContext context)
    {
        var parts = (Segments ?? []).Where(s => s.Count > 0).ToArray();
        var total = parts.Sum(s => s.Count);
        var bounds = new Rect(Bounds.Size);
        using (context.PushClip(new RoundedRect(bounds, 4)))
        {
            context.FillRectangle(Brush("Grid"), bounds);
            var x = 0.0;
            foreach (var part in parts)
            {
                var width = Bounds.Width * part.Count / total;
                context.FillRectangle(Brush(part.Brush), new Rect(x, 0, width, Bounds.Height));
                x += width;
            }
        }
    }
}

/// <summary>A progress meter (app.js meter()): a track, filled as far as <see cref="Value"/> (0 to 1).</summary>
public sealed class Meter : Chart
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Meter, double>(nameof(Value));

    static Meter() => AffectsRender<Meter>(ValueProperty);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(120, availableSize.Width), 6);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brush("Grid"), bounds, 3);
        var value = Math.Clamp(Value, 0, 1);
        if (value > 0)
        {
            context.FillRectangle(Brush("Good"), new Rect(0, 0, Bounds.Width * value, Bounds.Height), 3);
        }
    }
}

/// <summary>A bin of the complexity chart: its range and how many functions fall in it.</summary>
public sealed record ComplexityBar(int From, int To, int Count);

/// <summary>A point of the hotspot chart: a file, how often it changed, its most complex function, and whether named.</summary>
public sealed record HotspotPoint(string Name, int Commits, int Complexity, bool Named);

/// <summary>What the complexity charts share: the level of a complexity against the thresholds, and drawn text.</summary>
public abstract class ThresholdChart : Chart
{
    public static readonly StyledProperty<int> NoteProperty = AvaloniaProperty.Register<ThresholdChart, int>(nameof(Note), 11);
    public static readonly StyledProperty<int> WarnProperty = AvaloniaProperty.Register<ThresholdChart, int>(nameof(Warn), 21);
    public static readonly StyledProperty<int> ProblemProperty = AvaloniaProperty.Register<ThresholdChart, int>(nameof(Problem), 51);

    static ThresholdChart() => AffectsRender<ThresholdChart>(NoteProperty, WarnProperty, ProblemProperty);

    public int Note
    {
        get => GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    public int Warn
    {
        get => GetValue(WarnProperty);
        set => SetValue(WarnProperty, value);
    }

    public int Problem
    {
        get => GetValue(ProblemProperty);
        set => SetValue(ProblemProperty, value);
    }

    /// <summary>The colour token of a complexity: problem, warning, note, or below them.</summary>
    protected string Level(int complexity)
    {
        if (complexity >= Problem)
        {
            return "Critical";
        }
        if (complexity >= Warn)
        {
            return "Warning";
        }
        return complexity >= Note ? "Accent" : "Axis";
    }

    protected void Text(DrawingContext context, string text, Point at, string brush, bool right = false, bool center = false)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            11,
            Brush(brush)
        );
        var x = at.X;
        if (right)
        {
            x -= formatted.Width;
        }
        else if (center)
        {
            x -= formatted.Width / 2;
        }
        context.DrawText(formatted, new Point(x, at.Y - formatted.Height));
    }
}

/// <summary>
/// How a language's functions spread over cyclomatic complexity (app.js complexityChart): a bar per bin on a
/// square-root scale with its count above it, and the note, warning and problem thresholds as lines. The table
/// beside it says the same in words.
/// </summary>
public sealed class ComplexityChart : ThresholdChart
{
    public static readonly StyledProperty<IReadOnlyList<ComplexityBar>?> BinsProperty = AvaloniaProperty.Register<ComplexityChart, IReadOnlyList<ComplexityBar>?>(nameof(Bins));

    public static readonly StyledProperty<int> BinWidthProperty = AvaloniaProperty.Register<ComplexityChart, int>(nameof(BinWidth), 5);

    private const double Band = 30;
    private const double Plot = 120;
    private const double AxisHeight = 18;

    static ComplexityChart() => AffectsRender<ComplexityChart>(BinsProperty, BinWidthProperty);

    public IReadOnlyList<ComplexityBar>? Bins
    {
        get => GetValue(BinsProperty);
        set => SetValue(BinsProperty, value);
    }

    public int BinWidth
    {
        get => GetValue(BinWidthProperty);
        set => SetValue(BinWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 720 : availableSize.Width, Band + Plot + AxisHeight);

    public override void Render(DrawingContext context)
    {
        if (Bins is not { Count: > 0 } bins)
        {
            return;
        }
        var width = Bounds.Width;
        var bw = width / bins.Count;
        var top = Math.Max(1, bins.Max(b => b.Count));
        double Y(int count) => Band + Plot - (count > 0 ? Math.Max(2, Math.Sqrt(count / (double)top) * (Plot - 14)) : 0);
        var every = Math.Max(1, (int)Math.Ceiling(bins.Count / 12.0));
        for (var i = 0; i < bins.Count; i++)
        {
            var bin = bins[i];
            var y = Y(bin.Count);
            context.FillRectangle(Brush(Level(bin.From)), new Rect((i * bw) + 1, y, Math.Max(bw - 2, 1), Band + Plot - y));
            if (bin.Count > 0)
            {
                Text(context, bin.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), new Point((i * bw) + (bw / 2), y - 2), "Ink2", center: true);
            }
            if (i % every == 0)
            {
                Text(context, bin.From.ToString(System.Globalization.CultureInfo.InvariantCulture), new Point((i * bw) + 2, Band + Plot + AxisHeight), "Muted");
            }
        }
        var row = 0;
        var lastEnd = double.NegativeInfinity;
        foreach (var (value, name, brush) in new[] { (Note, "note", "Accent"), (Warn, "warning", "Warning"), (Problem, "problem", "Critical") })
        {
            if ((value - 1) / (double)BinWidth >= bins.Count)
            {
                continue;
            }
            var x = (value - 1) / (double)BinWidth * bw;
            context.DrawLine(new Pen(Brush(brush), 1.2, new DashStyle([4, 3], 0)), new Point(x, Band - 2), new Point(x, Band + Plot));
            var label = $"{name} {value}";
            row = x < lastEnd ? row + 1 : 0;
            lastEnd = x + (label.Length * 6.2) + 6;
            Text(context, label, new Point(x + 3, Band - 4 - (row * 12)), "Ink2");
        }
        context.DrawLine(new Pen(Brush("Axis"), 1), new Point(0, Band + Plot), new Point(width, Band + Plot));
    }
}

/// <summary>
/// Changing files by how often they changed (across) and their most complex function (up) (app.js hotspotChart): top
/// right is complex code that keeps changing. The warning band is shaded and the worst few files are named.
/// </summary>
public sealed class HotspotChart : ThresholdChart
{
    public static readonly StyledProperty<IReadOnlyList<HotspotPoint>?> PointsProperty = AvaloniaProperty.Register<HotspotChart, IReadOnlyList<HotspotPoint>?>(nameof(Points));

    private const double L = 34;
    private const double B = 26;
    private const double T = 10;
    private const double R = 10;

    static HotspotChart() => AffectsRender<HotspotChart>(PointsProperty);

    public IReadOnlyList<HotspotPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width, 220);

    public override void Render(DrawingContext context)
    {
        if (Points is not { Count: > 0 } points)
        {
            return;
        }
        var w = Bounds.Width;
        var h = Bounds.Height;
        var cmax = Math.Max(1, points.Max(p => p.Commits));
        var kmax = Math.Max(Problem + 5, points.Max(p => p.Complexity));
        double X(int c) => L + (c / (double)cmax * (w - L - R));
        double Y(int k) => h - B - (k / (double)kmax * (h - B - T));
        context.FillRectangle(Brush("AccentWash"), new Rect(L, T, w - L - R, Math.Max(0, Y(Warn) - T)));
        context.DrawLine(new Pen(Brush("Warning"), 1, new DashStyle([4, 3], 0)), new Point(L, Y(Warn)), new Point(w - R, Y(Warn)));
        Text(context, $"warning {Warn}", new Point(w - R - 4, Y(Warn) - 3), "Ink2", right: true);
        var axis = new Pen(Brush("Axis"), 1);
        context.DrawLine(axis, new Point(L, h - B), new Point(w - R, h - B));
        context.DrawLine(axis, new Point(L, T), new Point(L, h - B));
        Text(context, $"commits →  ({cmax} at most)", new Point(w - R, h - 4), "Muted", right: true);
        Text(context, "cx", new Point(4, T + 10), "Muted");
        foreach (var p in points)
        {
            var radius = p.Named ? 6 : 4.5;
            context.DrawEllipse(Brush(Level(p.Complexity)), null, new Point(X(p.Commits), Y(p.Complexity)), radius, radius);
        }
        foreach (var p in points.Where(p => p.Named))
        {
            Text(context, p.Name, new Point(X(p.Commits) - 9, Y(p.Complexity) + 5), "Ink", right: true);
        }
    }
}
