using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Harness.Ui.Controls;

/// <summary>A database a service's code owns: its name, whether there is one per tenant, and its tooltip.</summary>
public sealed record SystemDatabase(string Name, bool PerTenant, string Tip);

/// <summary>
/// A node of the system map: a member service or front-end (its calls, language mark, databases, whether every other
/// service calls it), an outside system or a broker.
/// </summary>
public sealed record SystemCard(
    string Id,
    string Name,
    string Kind,
    string Meta,
    string Tip,
    string? Mark = null,
    bool Hub = false,
    IReadOnlyList<SystemDatabase>? Databases = null
);

/// <summary>A column of the map (a row when narrow): callers, the called, outside; its label and nodes.</summary>
public sealed record SystemColumn(string Label, IReadOnlyList<SystemCard> Nodes);

/// <summary>A call from one node to another: grpc, http or messaging, and what it says when pointed at.</summary>
public sealed record SystemLine(string From, string To, string Style, string Tip);

/// <summary>
/// The workspace's service map (app.js systemView, style.css .sysmap, .sline, .syscard, .dbchip). On a wide window it
/// reads left to right in tall columns (callers, the called, outside), on a narrow one top to bottom in rows. Each line
/// runs from the caller's edge to the called's, styled by kind: gRPC solid Accent, HTTP dashed Gold, messaging dotted.
/// A click selects a node (again to clear): its lines are hot and flow toward the called, the other lines hide and the
/// nodes unrelated to it dim. Pointing at a node makes its lines flow while nothing is selected.
/// </summary>
public sealed class SystemMap : Panel
{
    public static readonly StyledProperty<IReadOnlyList<SystemColumn>?> ColumnsProperty =
        AvaloniaProperty.Register<SystemMap, IReadOnlyList<SystemColumn>?>(nameof(Columns));

    public static readonly StyledProperty<IReadOnlyList<SystemLine>?> LinesProperty =
        AvaloniaProperty.Register<SystemMap, IReadOnlyList<SystemLine>?>(nameof(Lines));

    public static readonly StyledProperty<string?> SelectedProperty = AvaloniaProperty.Register<SystemMap, string?>(
        nameof(Selected),
        defaultBindingMode: Avalonia.Data.BindingMode.TwoWay
    );

    /// <summary>The window width from which the map reads left to right (app.js: 1100 px).</summary>
    public const double WideFrom = 1100;

    /// <summary>A flowing line's dash and gap, and how far its dashes move per second (style.css .sline.flow: 13 px per 0.9 s).</summary>
    public const double FlowDash = 7;

    public const double FlowGap = 6;
    public const double FlowSpeed = 13 / 0.9;

    private readonly Dictionary<string, Button> _nodes = new(StringComparer.Ordinal);
    private readonly LineLayer _layer;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _started = DateTime.UtcNow;
    private Control? _content;
    private bool? _wide;
    private string? _hover;

    static SystemMap() => AffectsMeasure<SystemMap>(ColumnsProperty);

    public SystemMap()
    {
        _layer = new LineLayer(this);
        Children.Add(_layer);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => _layer.InvalidateVisual());
    }

    public IReadOnlyList<SystemColumn>? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public IReadOnlyList<SystemLine>? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    /// <summary>The selected node's id; null when none is.</summary>
    public string? Selected
    {
        get => GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    /// <summary>Whether the map reads left to right (a wide window); null before it is laid out.</summary>
    public bool? Wide => _wide;

    /// <summary>Whether lines flow now (something selected or pointed at): the animation runs only then.</summary>
    public bool Flowing => _timer.IsEnabled;

    /// <summary>How far the flowing dashes have moved, in pixels (for tests: it grows with time).</summary>
    public double FlowOffset => (DateTime.UtcNow - _started).TotalSeconds * FlowSpeed % (FlowDash + FlowGap);

    /// <summary>The node control drawn for <paramref name="id"/>.</summary>
    public Button? NodeFor(string id) => _nodes.GetValueOrDefault(id);

    /// <summary>The lines as they are drawn now: each with its pen, whether it is hot and whether it flows (for tests).</summary>
    public IReadOnlyList<(SystemLine Line, Pen Pen, bool Hot, bool Flow)> Drawn { get; private set; } = [];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColumnsProperty)
        {
            Build();
        }
        else if (change.Property == SelectedProperty || change.Property == LinesProperty)
        {
            Mark();
        }
        else if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            _layer.InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Mark();
    }

    private bool WindowIsWide() => (TopLevel.GetTopLevel(this)?.Bounds.Width ?? 0) >= WideFrom;

    private void Build()
    {
        Children.Clear();
        Children.Add(_layer);
        _nodes.Clear();
        _wide = WindowIsWide();
        var columns = Columns ?? [];
        Panel content;
        if (_wide is true)
        {
            // left to right: equal columns, 24 px apart (closer than the original 3.5rem: the side panel sits beside the map), every other one shaded (style.css .sysmap.horiz)
            var grid = new Grid { ColumnSpacing = 24 };
            for (var i = 0; i < columns.Count; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var stack = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var node in columns[i].Nodes)
                {
                    stack.Children.Add(Node(node));
                }
                var column = new DockPanel();
                var label = new TextBlock { Text = columns[i].Label.ToUpperInvariant(), Classes = { "syslabel" }, HorizontalAlignment = HorizontalAlignment.Center };
                DockPanel.SetDock(label, Dock.Top);
                column.Children.Add(label);
                column.Children.Add(stack);
                var band = new Border { Classes = { "sysband" }, Child = column };
                band.Classes.Set("shaded", i % 2 is 0);
                Grid.SetColumn(band, i);
                grid.Children.Add(band);
            }
            content = grid;
        }
        else
        {
            // top to bottom: a row per layer, its label on the left, every other one shaded
            var rows = new StackPanel { Spacing = 24 };
            for (var i = 0; i < columns.Count; i++)
            {
                var nodes = new WrapPanel { ItemSpacing = 14, LineSpacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var node in columns[i].Nodes)
                {
                    nodes.Children.Add(Node(node));
                }
                var row = new DockPanel();
                var label = new TextBlock { Text = columns[i].Label.ToUpperInvariant(), Classes = { "syslabel" }, Width = 84, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(label, Dock.Left);
                row.Children.Add(label);
                row.Children.Add(nodes);
                var band = new Border { Classes = { "sysband", "row" }, Child = row };
                band.Classes.Set("shaded", i % 2 is 0);
                rows.Children.Add(band);
            }
            content = rows;
        }
        _content = content;
        Children.Add(content);
        Mark();
    }

    private Button Node(SystemCard node)
    {
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new TextBlock { Text = node.Name, Classes = { "sysname" }, TextWrapping = TextWrapping.Wrap });
        var meta = new DockPanel();
        if (node.Mark is { Length: > 0 } mark)
        {
            var badge = new Border { Classes = { "language-mark", "small" }, Child = new TextBlock { Text = mark }, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(badge, Dock.Right);
            meta.Children.Add(badge);
        }
        meta.Children.Add(new TextBlock { Text = node.Meta, Classes = { "sysmeta" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(meta);
        if (node.Hub)
        {
            body.Children.Add(new TextBlock { Text = "every other service calls it", Classes = { "syshub" }, TextWrapping = TextWrapping.Wrap });
        }
        if (node.Databases is { Count: > 0 } databases)
        {
            body.Children.Add(new Border { Classes = { "sysdbhead" }, Child = new TextBlock { Text = "DATABASES" } });
            var chips = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            foreach (var database in databases)
            {
                chips.Children.Add(DatabaseChip(database.Name, database.PerTenant, database.Tip));
            }
            body.Children.Add(chips);
        }
        // the card's left edge in its kind's colour (style.css .anode.sys-*: service, front-end, outside, broker)
        var card = new DockPanel();
        var strip = new Border { Classes = { "sysstrip" } };
        DockPanel.SetDock(strip, Dock.Left);
        card.Children.Add(strip);
        body.Margin = new Thickness(10, 7, 12, 8);
        card.Children.Add(body);
        var button = new Button { Content = card, Classes = { "syscard", "sys-" + node.Kind }, Tag = node.Id };
        button.Classes.Set("hub", node.Hub);
        ToolTip.SetTip(button, node.Tip);
        Avalonia.Automation.AutomationProperties.SetName(button, node.Name);
        var id = node.Id;
        button.Click += (_, _) => Selected = Selected == id ? null : id;
        button.PointerEntered += (_, _) =>
        {
            _hover = id;
            Mark();
        };
        button.PointerExited += (_, _) =>
        {
            if (_hover == id)
            {
                _hover = null;
                Mark();
            }
        };
        _nodes[id] = button;
        return button;
    }

    /// <summary>
    /// A database chip (style.css .dbchip): a small cylinder outline, its name and, for one per tenant, "per tenant" with
    /// two more chips stacked behind it. <paramref name="name"/> null draws the bare mark (the legend's).
    /// </summary>
    public static Control DatabaseChip(string? name, bool perTenant, string? tip = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        row.Children.Add(new Border { Classes = { "dbicon" }, VerticalAlignment = VerticalAlignment.Center });
        if (name is not null)
        {
            row.Children.Add(new TextBlock { Text = name, Classes = { "dbname" }, VerticalAlignment = VerticalAlignment.Center });
            if (perTenant)
            {
                row.Children.Add(new TextBlock { Text = "per tenant", Classes = { "dbnote" }, VerticalAlignment = VerticalAlignment.Center });
            }
        }
        var chip = new Border { Classes = { "dbchip" }, Child = row };
        chip.Classes.Set("mini", name is null);
        if (tip is not null)
        {
            ToolTip.SetTip(chip, tip);
        }
        if (!perTenant)
        {
            return chip;
        }
        // one database per tenant: the chip with two more behind it, each 3 px further down and right
        chip.Margin = new Thickness(0, 0, 6, 6);
        var stack = new Panel { HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(new Border { Classes = { "dbchip", "behind" }, Margin = new Thickness(6, 6, 0, 0) });
        stack.Children.Add(new Border { Classes = { "dbchip", "behind" }, Margin = new Thickness(3, 3, 3, 3) });
        stack.Children.Add(chip);
        return stack;
    }

    /// <summary>Marks the selected node, what it calls and what calls it, dims the rest, and starts or stops the flow.</summary>
    private void Mark()
    {
        var selected = Selected;
        var lines = Lines ?? [];
        var outs = selected is null ? [] : lines.Where(l => l.From == selected).Select(l => l.To).ToHashSet(StringComparer.Ordinal);
        var ins = selected is null ? [] : lines.Where(l => l.To == selected).Select(l => l.From).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, button) in _nodes)
        {
            button.Classes.Set("sel", id == selected);
            button.Classes.Set("rel-out", outs.Contains(id));
            button.Classes.Set("rel-in", ins.Contains(id));
            button.Classes.Set("dim", selected is not null && id != selected && !outs.Contains(id) && !ins.Contains(id));
        }
        var focus = selected ?? _hover;
        var flowing = focus is not null && lines.Any(l => l.From == focus || l.To == focus) && VisualRoot is not null;
        if (flowing && !_timer.IsEnabled)
        {
            _timer.Start();
        }
        else if (!flowing && _timer.IsEnabled)
        {
            _timer.Stop();
        }
        _layer.InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_content is null)
        {
            return default;
        }
        _content.Measure(availableSize);
        _layer.Measure(Size.Infinity);
        return _content.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _content?.Arrange(new Rect(finalSize));
        _layer.Arrange(new Rect(finalSize));
        _layer.InvalidateVisual();
        // a window crossing the wide width lays the map out the other way
        if (_wide is { } wide && wide != WindowIsWide())
        {
            Dispatcher.UIThread.Post(Build, DispatcherPriority.Background);
        }
        return finalSize;
    }

    /// <summary>The line's pen: its kind's colour and dashes (style.css .sline), thicker when hot, dashed and moving when it flows.</summary>
    private Pen Pen(string style, bool hot, bool flow)
    {
        var width = hot || flow ? 2.2 : 1.4;
        IBrush brush = Brush(Tone(style));
        if (!hot && !flow)
        {
            brush = new SolidColorBrush(brush is ISolidColorBrush solid ? solid.Color : Colors.Gray, 0.75);
        }
        // Avalonia's dashes are in multiples of the pen's width
        var dashes = flow ? [FlowDash, FlowGap] : Dashes(style);
        if (dashes is null)
        {
            return new Pen(brush, width, null, PenLineCap.Flat);
        }
        // a flowing line's dashes move toward the called: the offset grows backwards with time
        var offset = flow ? -FlowOffset / width : 0;
        return new Pen(brush, width, new DashStyle(dashes.Select(d => d / width), offset), PenLineCap.Flat);
    }

    /// <summary>The dash and gap of a call's kind, in pixels (style.css .sline.st-*); null for a solid line.</summary>
    public static double[]? Dashes(string style) =>
        style switch
        {
            "http" => [7, 4],
            "messaging" => [2, 4],
            "database" => [1, 3],
            _ => null,
        };

    /// <summary>The colour token of a call's kind (style.css .sline.st-*).</summary>
    public static string Tone(string style) =>
        style switch
        {
            "grpc" => "Accent",
            "http" => "Gold",
            "messaging" => "CConvention",
            _ => "Axis",
        };

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    /// <summary>The lines, under the nodes and the shaded columns: from the caller's edge to the called's, an arrow at the called.</summary>
    private void DrawLines(DrawingContext context)
    {
        var selected = Selected;
        var focus = selected ?? _hover;
        var drawn = new List<(SystemLine, Pen, bool, bool)>();
        foreach (var line in Lines ?? [])
        {
            if (!_nodes.TryGetValue(line.From, out var from) || !_nodes.TryGetValue(line.To, out var to) || !from.IsVisible || !to.IsVisible)
            {
                continue;
            }
            var hot = selected is not null && (line.From == selected || line.To == selected);
            if (selected is not null && !hot)
            {
                continue;
            }
            var flow = focus is not null && (line.From == focus || line.To == focus);
            if (from.TranslatePoint(default, _layer) is not { } fp || to.TranslatePoint(default, _layer) is not { } tp)
            {
                continue;
            }
            var a = new Rect(fp, from.Bounds.Size);
            var b = new Rect(tp, to.Bounds.Size);
            var pen = Pen(line.Style, hot, flow);
            var (geometry, end, direction) = Route(a, b);
            context.DrawGeometry(null, pen, geometry);
            context.DrawGeometry(Brush(Tone(line.Style)), null, Arrow(end, direction));
            drawn.Add((line, pen, hot, flow));
        }
        Drawn = drawn;
    }

    /// <summary>The curve from <paramref name="a"/> to <paramref name="b"/>, where it ends and the way it points there.</summary>
    private (StreamGeometry Geometry, Point End, Vector Direction) Route(Rect a, Rect b)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        if (_wide is true && (b.Left > a.Right - 4 || b.Right < a.Left + 4))
        {
            // side by side: from the caller's edge to the called's edge
            var right = b.Left > a.Right - 4;
            var start = new Point(right ? a.Right : a.Left, a.Center.Y);
            var end = new Point(right ? b.Left : b.Right, b.Center.Y);
            var m = (start.X + end.X) / 2;
            g.BeginFigure(start, false);
            g.CubicBezierTo(new Point(m, start.Y), new Point(m, end.Y), end);
            g.EndFigure(false);
            return (geometry, end, new Vector(right ? 1 : -1, 0));
        }
        var down = b.Top > a.Bottom - 4;
        var up = b.Bottom < a.Top + 4;
        var x1 = a.Center.X;
        var x2 = b.Center.X;
        var y1 = up ? a.Top : a.Bottom;
        var y2 = down ? b.Top : b.Bottom;
        var tip = new Point(x2, y2);
        g.BeginFigure(new Point(x1, y1), false);
        if (down && y2 - y1 > 170)
        {
            // a line skipping a row goes around the side, not behind the cards between
            var toRight = x2 >= x1;
            var cx = toRight ? Math.Max(a.Right, b.Right) + 36 : Math.Min(a.Left, b.Left) - 36;
            var ym = (y1 + y2) / 2;
            g.CubicBezierTo(new Point(x1, y1 + 40), new Point(cx, y1 + 20), new Point(cx, ym));
            g.CubicBezierTo(new Point(cx, y2 - 20), new Point(x2, y2 - 40), tip);
        }
        else
        {
            var m = down || up ? (y1 + y2) / 2 : y1 + Math.Min(40, 14 + (Math.Abs(x2 - x1) / 12));
            g.CubicBezierTo(new Point(x1, m), new Point(x2, m), tip);
        }
        g.EndFigure(false);
        return (geometry, tip, new Vector(0, down ? 1 : -1));
    }

    /// <summary>An arrow head with its tip at <paramref name="tip"/>, pointing along <paramref name="direction"/>.</summary>
    private static StreamGeometry Arrow(Point tip, Vector direction)
    {
        var back = tip - (direction * 7);
        var side = new Vector(-direction.Y, direction.X) * 3.5;
        var head = new StreamGeometry();
        using var g = head.Open();
        g.BeginFigure(tip, true);
        g.LineTo(back + side);
        g.LineTo(back - side);
        g.EndFigure(true);
        return head;
    }

    /// <summary>The lines' layer, the first child: under the columns and nodes, never hit by the pointer.</summary>
    private sealed class LineLayer : Control
    {
        private readonly SystemMap _owner;

        public LineLayer(SystemMap owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context) => _owner.DrawLines(context);
    }
}

/// <summary>
/// The map's legend (app.js .syslegend): a line sample and name per call kind shown, then the database chip and the
/// stacked one for a database per tenant.
/// </summary>
public sealed class SystemLegend : WrapPanel
{
    public static readonly StyledProperty<IReadOnlyList<string>?> KindsProperty = AvaloniaProperty.Register<SystemLegend, IReadOnlyList<string>?>(nameof(Kinds));

    public SystemLegend()
    {
        ItemSpacing = 16;
        LineSpacing = 4;
        Build();
    }

    /// <summary>The call kinds to show (grpc, http, messaging), in order.</summary>
    public IReadOnlyList<string>? Kinds
    {
        get => GetValue(KindsProperty);
        set => SetValue(KindsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindsProperty)
        {
            Build();
        }
    }

    private void Build()
    {
        Children.Clear();
        foreach (var kind in Kinds ?? [])
        {
            Children.Add(Entry(new LineSample { StyleKind = kind, VerticalAlignment = VerticalAlignment.Center }, Label(kind)));
        }
        Children.Add(Entry(SystemMap.DatabaseChip(null, false), "database"));
        Children.Add(Entry(SystemMap.DatabaseChip(null, true), "one database per tenant"));
    }

    private static string Label(string kind) =>
        kind switch
        {
            "grpc" => "gRPC",
            "http" => "HTTP",
            _ => kind,
        };

    private static StackPanel Entry(Control mark, string text)
    {
        var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        entry.Children.Add(mark);
        entry.Children.Add(new TextBlock { Text = text, Classes = { "syslegend" }, VerticalAlignment = VerticalAlignment.Center });
        return entry;
    }
}

/// <summary>A line sample of a call kind for the legend (app.js .syslegend): 28 × 10, the kind's colour and dashes.</summary>
public sealed class LineSample : Chart
{
    public static readonly StyledProperty<string> StyleKindProperty = AvaloniaProperty.Register<LineSample, string>(nameof(StyleKind), "grpc");

    static LineSample() => AffectsRender<LineSample>(StyleKindProperty);

    public string StyleKind
    {
        get => GetValue(StyleKindProperty);
        set => SetValue(StyleKindProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(28, 10);

    public override void Render(DrawingContext context)
    {
        const double Width = 2.2;
        var dashes = SystemMap.Dashes(StyleKind);
        var pen = new Pen(Brush(SystemMap.Tone(StyleKind)), Width, dashes is null ? null : new DashStyle(dashes.Select(d => d / Width), 0));
        context.DrawLine(pen, new Point(1, 5), new Point(27, 5));
    }
}
