using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;

namespace Harness.Ui.Controls;

/// <summary>
/// A dependency matrix for large layer graphs. Rows reference columns; a hollow mark is a build-order-only edge.
/// The numbered column headers and row labels select the same node as the layer graph.
/// </summary>
public sealed class LayerMatrix : Panel
{
    public static readonly StyledProperty<IReadOnlyList<GraphRow>?> RowsProperty =
        AvaloniaProperty.Register<LayerMatrix, IReadOnlyList<GraphRow>?>(nameof(Rows));
    public static readonly StyledProperty<IReadOnlyList<GraphEdge>?> EdgesProperty =
        AvaloniaProperty.Register<LayerMatrix, IReadOnlyList<GraphEdge>?>(nameof(Edges));
    public static readonly StyledProperty<string?> SelectedProperty = AvaloniaProperty.Register<LayerMatrix, string?>(
        nameof(Selected), defaultBindingMode: BindingMode.TwoWay
    );

    private const double LabelWidth = 180;
    private const double CellSize = 28;
    private readonly List<(Button Row, Button Column)> _labels = [];
    private readonly List<(Button Button, int Row, int Column)> _marks = [];
    private readonly List<(Border Band, int Row)> _bands = [];

    static LayerMatrix() => AffectsMeasure<LayerMatrix>(RowsProperty, EdgesProperty);

    public IReadOnlyList<GraphRow>? Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public IReadOnlyList<GraphEdge>? Edges { get => GetValue(EdgesProperty); set => SetValue(EdgesProperty, value); }
    public string? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty || change.Property == EdgesProperty)
        {
            Build();
        }
        if (change.Property == SelectedProperty)
        {
            Mark();
        }
    }

    private void Build()
    {
        Children.Clear();
        _labels.Clear();
        _marks.Clear();
        _bands.Clear();
        var nodes = (Rows ?? []).SelectMany(row => row.Nodes).ToArray();
        var index = nodes.Select((node, i) => (node.Id, Index: i)).ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            if (i % 2 is 1)
            {
                var band = new Border { Classes = { "layer-band" }, IsHitTestVisible = false };
                Children.Add(band);
                _bands.Add((band, i));
            }
            var row = Label(node, $"{i + 1}. {node.Label}", "matrix-row");
            var column = Label(node, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "matrix-column");
            Children.Add(row);
            Children.Add(column);
            _labels.Add((row, column));
        }
        foreach (var edge in Edges ?? [])
        {
            if (!index.TryGetValue(edge.From, out var row) || !index.TryGetValue(edge.To, out var column))
            {
                continue;
            }
            var from = nodes[row];
            var to = nodes[column];
            var detail = $"{from.Label} {(edge.Dashed ? "builds first" : "references")} {to.Label}";
            var button = new Button
            {
                Content = new Icon { Kind = edge.Dashed ? "status-backlog" : "status-ready", Size = 14 },
                Tag = edge,
                Classes = { "matrix-edge" },
            };
            button.Classes.Set("back", !edge.Dashed && column < row && from.Role is not "test");
            ToolTip.SetTip(button, detail);
            AutomationProperties.SetName(button, detail);
            button.Click += (_, _) => Selected = edge.From;
            Children.Add(button);
            _marks.Add((button, row, column));
        }
        Mark();
    }

    private Button Label(GraphNode node, string text, string style)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
            Tag = node.Id,
            Classes = { style },
        };
        ToolTip.SetTip(button, node.Tip ?? node.Label);
        AutomationProperties.SetName(button, style is "matrix-column" ? $"Column {text}: {node.Label}" : $"Row {text}");
        button.Click += (_, _) => Selected = Selected == node.Id ? null : node.Id;
        return button;
    }

    private void Mark()
    {
        foreach (var (row, column) in _labels)
        {
            row.Classes.Set("sel", Equals(row.Tag, Selected));
            column.Classes.Set("sel", Equals(column.Tag, Selected));
        }
        foreach (var (button, _, _) in _marks)
        {
            var edge = (GraphEdge)button.Tag!;
            button.Classes.Set("sel", Selected is not null && (edge.From == Selected || edge.To == Selected));
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(LabelWidth, CellSize));
        }
        return new Size(LabelWidth + _labels.Count * CellSize, (_labels.Count + 1) * CellSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (band, row) in _bands)
        {
            band.Arrange(new Rect(0, (row + 1) * CellSize, finalSize.Width, CellSize));
        }
        for (var i = 0; i < _labels.Count; i++)
        {
            _labels[i].Row.Arrange(new Rect(0, (i + 1) * CellSize, LabelWidth, CellSize));
            _labels[i].Column.Arrange(new Rect(LabelWidth + i * CellSize, 0, CellSize, CellSize));
        }
        foreach (var (button, row, column) in _marks)
        {
            button.Arrange(new Rect(LabelWidth + column * CellSize, (row + 1) * CellSize, CellSize, CellSize));
        }
        return finalSize;
    }
}
