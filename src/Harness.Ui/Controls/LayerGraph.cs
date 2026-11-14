using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Harness.Ui.Controls;

/// <summary>A node of a layered graph: its id, words, a second line, a badge (users), and its tooltip.</summary>
public sealed record GraphNode(string Id, string Label, string? Meta = null, string? Badge = null, string? Tip = null);

/// <summary>A row of the graph, labelled (tests, layer 2, …), its nodes left to right.</summary>
public sealed record GraphRow(string Label, IReadOnlyList<GraphNode> Nodes);

/// <summary>
/// A line from a node to one it references; dashed when it only builds first; quiet when shown only in focus; in its
/// own colour token (a call's style) when it has a <paramref name="Tone"/>, else the axis colour, lit in focus.
/// </summary>
public sealed record GraphEdge(string From, string To, bool Dashed = false, bool Quiet = false, string? Tone = null, string? Tip = null);

/// <summary>
/// A layered graph (app.js's layer map, a workspace's maps): rows of nodes, the highest first, and lines from a node to
/// those it references, drawn under the nodes. A node is selected with a click (again to clear): it is marked, what it
/// references and what references it are lit, the rest dimmed, and only its lines show. Pointing at a node shows its
/// lines while nothing is selected. With <see cref="LinesOnFocus"/> (a large graph) lines show only for the node in
/// focus. Reused wherever the UI draws who-references-whom.
/// </summary>
public sealed class LayerGraph : Panel
{
    public static readonly StyledProperty<IReadOnlyList<GraphRow>?> RowsProperty = AvaloniaProperty.Register<LayerGraph, IReadOnlyList<GraphRow>?>(nameof(Rows));

    public static readonly StyledProperty<IReadOnlyList<GraphEdge>?> EdgesProperty = AvaloniaProperty.Register<LayerGraph, IReadOnlyList<GraphEdge>?>(nameof(Edges));

    public static readonly StyledProperty<string?> SelectedProperty = AvaloniaProperty.Register<LayerGraph, string?>(
        nameof(Selected),
        defaultBindingMode: Avalonia.Data.BindingMode.TwoWay
    );

    public static readonly StyledProperty<bool> LinesOnFocusProperty = AvaloniaProperty.Register<LayerGraph, bool>(nameof(LinesOnFocus));

    /// <summary>The width of the row labels' column.</summary>
    public const double LabelWidth = 64;

    public const double Gap = 10;
    public const double RowGap = 28;

    private readonly Dictionary<string, Button> _nodes = new(StringComparer.Ordinal);
    private readonly EdgeLayer _lines;
    private readonly List<(TextBlock Label, IReadOnlyList<Button> Nodes)> _rows = [];
    private string? _hover;

    static LayerGraph() => AffectsMeasure<LayerGraph>(RowsProperty);

    public LayerGraph()
    {
        _lines = new EdgeLayer(this);
        Children.Add(_lines);
    }

    public IReadOnlyList<GraphRow>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public IReadOnlyList<GraphEdge>? Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    /// <summary>The selected node's id; null when none is.</summary>
    public string? Selected
    {
        get => GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    public bool LinesOnFocus
    {
        get => GetValue(LinesOnFocusProperty);
        set => SetValue(LinesOnFocusProperty, value);
    }

    /// <summary>The node control drawn for <paramref name="id"/> (for tests and focus).</summary>
    public Button? NodeFor(string id) => _nodes.GetValueOrDefault(id);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty)
        {
            Build();
        }
        if (change.Property == SelectedProperty || change.Property == EdgesProperty)
        {
            Mark();
        }
    }

    private void Build()
    {
        Children.Clear();
        Children.Add(_lines);
        _nodes.Clear();
        _rows.Clear();
        foreach (var row in Rows ?? [])
        {
            var label = new TextBlock { Text = row.Label, Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center };
            Children.Add(label);
            var buttons = new List<Button>();
            foreach (var node in row.Nodes)
            {
                var text = new StackPanel { Spacing = 1 };
                text.Children.Add(new TextBlock { Text = node.Label, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                if (node.Meta is { Length: > 0 } || node.Badge is { Length: > 0 })
                {
                    var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    if (node.Meta is { Length: > 0 } m)
                    {
                        meta.Children.Add(new TextBlock { Text = m, Classes = { "small" }, TextTrimming = TextTrimming.CharacterEllipsis });
                    }
                    if (node.Badge is { Length: > 0 } b)
                    {
                        meta.Children.Add(new TextBlock { Text = b, Classes = { "small" }, TextTrimming = TextTrimming.CharacterEllipsis });
                    }
                    text.Children.Add(meta);
                }
                var button = new Button { Content = text, Classes = { "anode" }, MaxWidth = 220, Tag = node.Id };
                ToolTip.SetTip(button, node.Tip ?? node.Label);
                Avalonia.Automation.AutomationProperties.SetName(button, node.Label);
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
                Children.Add(button);
                buttons.Add(button);
                _nodes[id] = button;
            }
            _rows.Add((label, buttons));
        }
        Mark();
    }

    /// <summary>Marks the selected node, what it references (out) and what references it (in), and dims the rest.</summary>
    private void Mark()
    {
        var selected = Selected;
        var edges = Edges ?? [];
        var outs = selected is null ? [] : edges.Where(e => e.From == selected).Select(e => e.To).ToHashSet(StringComparer.Ordinal);
        var ins = selected is null ? [] : edges.Where(e => e.To == selected).Select(e => e.From).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, button) in _nodes)
        {
            button.Classes.Set("sel", id == selected);
            button.Classes.Set("rel-out", outs.Contains(id));
            button.Classes.Set("rel-in", ins.Contains(id));
            button.Classes.Set("dim", selected is not null && id != selected && !outs.Contains(id) && !ins.Contains(id));
        }
        _lines.InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _lines.Measure(Size.Infinity);
        var width = double.IsInfinity(availableSize.Width) ? 960 : availableSize.Width;
        var y = 0.0;
        foreach (var (label, nodes) in _rows)
        {
            label.Measure(new Size(LabelWidth, double.PositiveInfinity));
            var x = LabelWidth;
            var line = 0.0;
            foreach (var node in nodes)
            {
                node.Measure(new Size(Math.Max(80, width - LabelWidth), double.PositiveInfinity));
                if (x + node.DesiredSize.Width > width && x > LabelWidth)
                {
                    y += line + Gap;
                    x = LabelWidth;
                    line = 0;
                }
                x += node.DesiredSize.Width + Gap;
                line = Math.Max(line, node.DesiredSize.Height);
            }
            y += Math.Max(line, label.DesiredSize.Height) + RowGap;
        }
        return new Size(width, Math.Max(0, y - RowGap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        foreach (var (label, nodes) in _rows)
        {
            var x = LabelWidth;
            var top = y;
            var line = 0.0;
            foreach (var node in nodes)
            {
                if (x + node.DesiredSize.Width > finalSize.Width && x > LabelWidth)
                {
                    y += line + Gap;
                    x = LabelWidth;
                    line = 0;
                }
                node.Arrange(new Rect(new Point(x, y), node.DesiredSize));
                x += node.DesiredSize.Width + Gap;
                line = Math.Max(line, node.DesiredSize.Height);
            }
            var height = Math.Max(line, label.DesiredSize.Height);
            label.Arrange(new Rect(0, top, LabelWidth, Math.Max(height, (y + height) - top)));
            y += height + RowGap;
        }
        _lines.Arrange(new Rect(finalSize));
        _lines.InvalidateVisual();
        return finalSize;
    }

    /// <summary>The lines, under the nodes: from the bottom of the referencing node to the top of the referenced one.</summary>
    private void DrawEdges(DrawingContext context)
    {
        var focus = Selected ?? _hover;
        var axis = Brush("Axis");
        var outBrush = Brush("Accent");
        var inBrush = Brush("CConvention");
        foreach (var edge in Edges ?? [])
        {
            if (!_nodes.TryGetValue(edge.From, out var from) || !_nodes.TryGetValue(edge.To, out var to))
            {
                continue;
            }
            var hot = focus is not null && (edge.From == focus || edge.To == focus);
            if (focus is not null && !hot)
            {
                continue;
            }
            if (focus is null && (edge.Quiet || LinesOnFocus))
            {
                continue;
            }
            var a = from.Bounds;
            var b = to.Bounds;
            var down = b.Top > a.Bottom;
            var start = new Point(a.Center.X, down ? a.Bottom : a.Top);
            var end = new Point(b.Center.X, down ? b.Top : b.Bottom);
            var middle = (start.Y + end.Y) / 2;
            var brush = edge.Tone is { } tone ? Brush(tone) : axis;
            if (hot && edge.Tone is null)
            {
                brush = edge.From == focus ? outBrush : inBrush;
            }
            var pen = new Pen(brush, hot ? 1.8 : 1.2, edge.Dashed ? new DashStyle([4, 3], 0) : null);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(start, false);
                g.CubicBezierTo(new Point(start.X, middle), new Point(end.X, middle), end);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
            // the arrowhead at the referenced node
            var tip = end;
            var dir = down ? -1 : 1;
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(tip, true);
                g.LineTo(new Point(tip.X - 4, tip.Y + (dir * 7)));
                g.LineTo(new Point(tip.X + 4, tip.Y + (dir * 7)));
                g.EndFigure(true);
            }
            context.DrawGeometry(brush, null, head);
        }
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    /// <summary>The lines' layer, the first child: under the nodes, never hit by the pointer.</summary>
    private sealed class EdgeLayer : Control
    {
        private readonly LayerGraph _owner;

        public EdgeLayer(LayerGraph owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext context) => _owner.DrawEdges(context);
    }
}
