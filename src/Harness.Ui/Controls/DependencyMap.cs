using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Harness.Ui.Controls;

/// <summary>A member of the build map: its id, name and whether it is a library (no host or app of its own).</summary>
public sealed record DepNode(string Id, string Label, bool Library);

/// <summary>A member compiling against another member's packages: which packages, and the manifests naming them.</summary>
public sealed record DepEdge(string From, string To, IReadOnlyList<string> Packages, string Via = "");

/// <summary>
/// The build map's model (app.js depModel): who uses whom, the foundations a large workspace leaves out (used by 60%
/// or more of over 12 members), the cycles (strongly connected components), and the columns: a member sits one step
/// right of the furthest member it uses, cycles sharing a step, ordered by sweeps over the neighbours' mean row.
/// </summary>
public sealed class DependencyModel
{
    public DependencyModel(IReadOnlyList<DepNode> nodes, IReadOnlyList<DepEdge> edges)
    {
        Nodes = nodes;
        var ids = nodes.Select(n => n.Id).ToArray();
        var known = ids.ToHashSet(StringComparer.Ordinal);
        Edges = [.. edges.Where(e => known.Contains(e.From) && known.Contains(e.To))];
        foreach (var id in ids)
        {
            Uses[id] = [];
            UsedBy[id] = [];
        }
        foreach (var e in Edges)
        {
            Uses[e.From].Add(e);
            UsedBy[e.To].Add(e);
        }
        Foundation = ids.Length <= 12
            ? []
            : ids.Where(id => UsedBy[id].Count >= Math.Max(3, (int)Math.Ceiling(ids.Length * 0.6))).ToHashSet(StringComparer.Ordinal);
        Library = nodes.Where(n => n.Library).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Drawn = [.. Edges.Where(e => !Foundation.Contains(e.To))];
        Cycles = Strong(ids);
        var comp = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _components.Count; i++)
        {
            foreach (var id in _components[i])
            {
                comp[id] = i;
            }
        }
        Columns = Arrange(ids, comp);
    }

    public IReadOnlyList<DepNode> Nodes { get; }
    public IReadOnlyList<DepEdge> Edges { get; }
    public IReadOnlyList<DepEdge> Drawn { get; }
    public IReadOnlySet<string> Foundation { get; }
    public IReadOnlySet<string> Library { get; }
    public IReadOnlyList<IReadOnlyList<string>> Cycles { get; }
    public IReadOnlyList<IReadOnlyList<string>> Columns { get; }
    public Dictionary<string, List<DepEdge>> Uses { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<DepEdge>> UsedBy { get; } = new(StringComparer.Ordinal);

    private readonly List<List<string>> _components = [];

    public string Label(string id) => Nodes.FirstOrDefault(n => n.Id == id)?.Label ?? id;

    public IReadOnlyList<string>? CycleOf(string id) => Cycles.FirstOrDefault(c => c.Contains(id));

    /// <summary>What a member uses (or is used by), directly, or also through others with <paramref name="indirect"/>.</summary>
    public IReadOnlySet<string> Reach(string id, bool uses, bool indirect)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>([id]);
        while (todo.Count > 0)
        {
            var x = todo.Pop();
            foreach (var e in uses ? Uses[x] : UsedBy[x])
            {
                var y = uses ? e.To : e.From;
                if (y != id && seen.Add(y) && indirect)
                {
                    todo.Push(y);
                }
            }
        }
        return seen;
    }

    /// <summary>Tarjan's strongly connected components over every edge; the cycles are those of two or more.</summary>
    private List<IReadOnlyList<string>> Strong(IReadOnlyList<string> ids)
    {
        var index = 0;
        var idx = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var on = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        void Visit(string v)
        {
            idx[v] = low[v] = index++;
            stack.Push(v);
            on.Add(v);
            foreach (var w in Uses[v].Select(e => e.To))
            {
                if (!idx.TryGetValue(w, out var seen))
                {
                    Visit(w);
                    low[v] = Math.Min(low[v], low[w]);
                }
                else if (on.Contains(w))
                {
                    low[v] = Math.Min(low[v], seen);
                }
            }
            if (low[v] != idx[v])
            {
                return;
            }
            var c = new List<string>();
            string w2;
            do
            {
                w2 = stack.Pop();
                on.Remove(w2);
                c.Add(w2);
            } while (w2 != v);
            _components.Add(c);
        }
        foreach (var id in ids.Where(id => !idx.ContainsKey(id)))
        {
            Visit(id);
        }
        return [.. _components.Where(c => c.Count > 1)];
    }

    private List<IReadOnlyList<string>> Arrange(IReadOnlyList<string> ids, Dictionary<string, int> comp)
    {
        var rest = ids.Where(id => !Foundation.Contains(id)).ToArray();
        var col = rest.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        for (var round = 0; round < rest.Length; round++)
        {
            var moved = false;
            foreach (var e in Drawn)
            {
                if (comp[e.From] == comp[e.To] || Foundation.Contains(e.From) || !col.ContainsKey(e.From) || !col.ContainsKey(e.To))
                {
                    continue;
                }
                if (col[e.From] < col[e.To] + 1)
                {
                    col[e.From] = col[e.To] + 1;
                    moved = true;
                }
            }
            if (!moved)
            {
                break;
            }
        }
        foreach (var c in Cycles)
        {
            var inside = c.Where(col.ContainsKey).ToArray();
            if (inside.Length > 0)
            {
                var m = inside.Max(id => col[id]);
                foreach (var id in inside)
                {
                    col[id] = m;
                }
            }
        }
        var columns = new List<List<string>>();
        foreach (var id in rest)
        {
            while (columns.Count <= col[id])
            {
                columns.Add([]);
            }
            columns[col[id]].Add(id);
        }
        columns.RemoveAll(c => c.Count is 0);
        foreach (var c in columns)
        {
            c.Sort((a, b) => string.Compare(Label(a), Label(b), StringComparison.OrdinalIgnoreCase));
        }
        var row = new Dictionary<string, double>(StringComparer.Ordinal);
        void Place()
        {
            foreach (var c in columns)
            {
                for (var i = 0; i < c.Count; i++)
                {
                    row[c[i]] = i / (double)Math.Max(1, c.Count - 1);
                }
            }
        }
        Place();
        for (var sweep = 0; sweep < 6; sweep++)
        {
            foreach (var c in columns)
            {
                var key = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var id in c)
                {
                    var nb = Uses[id].Select(e => e.To).Concat(UsedBy[id].Select(e => e.From)).Where(x => row.ContainsKey(x) && x != id).ToArray();
                    key[id] = nb.Length > 0 ? nb.Average(x => row[x]) : row[id];
                }
                var sorted = c.OrderBy(id => key[id]).ToList();
                c.Clear();
                c.AddRange(sorted);
            }
            Place();
        }
        // members of one cycle sit next to each other
        foreach (var c in columns)
        {
            var groups = new List<List<string>>();
            foreach (var id in c)
            {
                var g = groups.FirstOrDefault(x => comp[x[0]] == comp[id] && Cycles.Any(cy => cy.Contains(id)));
                if (g is not null)
                {
                    g.Add(id);
                }
                else
                {
                    groups.Add([id]);
                }
            }
            c.Clear();
            c.AddRange(groups.SelectMany(g => g));
        }
        return [.. columns.Cast<IReadOnlyList<string>>()];
    }
}

/// <summary>
/// The build map (app.js dependencyMap drawMap): members in layers, users above what they use; in a small workspace
/// (12 or fewer) a second axis, right by how many members use one, and a bus where three or more run into one member:
/// a trunk they join, or, for a library, a rail on the right its users run into as one cable. Lines that would cross a
/// member go around the side, others skipping a layer as elbows. A member is focused with a click (again or Esc
/// clears): what it uses and what uses it are lit, the rest dimmed; the filter dims what does not match.
/// </summary>
public sealed class DependencyMap : Panel
{
    public static readonly StyledProperty<DependencyModel?> ModelProperty = AvaloniaProperty.Register<DependencyMap, DependencyModel?>(nameof(Model));

    public static readonly StyledProperty<string?> FocusedProperty = AvaloniaProperty.Register<DependencyMap, string?>(
        nameof(Focused),
        defaultBindingMode: Avalonia.Data.BindingMode.TwoWay
    );

    public static readonly StyledProperty<bool> IndirectProperty = AvaloniaProperty.Register<DependencyMap, bool>(nameof(Indirect));
    public static readonly StyledProperty<string?> FilterProperty = AvaloniaProperty.Register<DependencyMap, string?>(nameof(Filter));

    public const double NodeWidth = 172;
    public const double NodeHeight = 46;
    private const double Py = 14;
    private const double Ga = 16;
    private const double Gl = 58;
    private const double LaneWidth = 16;
    private const double Rail = 84;
    private const double Cable = 5;

    private readonly Dictionary<string, Button> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Point> _pos = new(StringComparer.Ordinal);
    private readonly Dictionary<DepEdge, (int Side, int N)> _lane = [];
    private readonly Dictionary<DepEdge, (int K, int N)> _elbow = [];
    private readonly HashSet<string> _bus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Jx, Dictionary<string, double> Track)> _rail = new(StringComparer.Ordinal);
    private readonly Lines _lines;
    private Size _size;

    static DependencyMap() => AffectsMeasure<DependencyMap>(ModelProperty);

    public DependencyMap()
    {
        _lines = new Lines(this);
        Children.Add(_lines);
        Focusable = true;
    }

    public DependencyModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>The focused member; null when none is.</summary>
    public string? Focused
    {
        get => GetValue(FocusedProperty);
        set => SetValue(FocusedProperty, value);
    }

    public bool Indirect
    {
        get => GetValue(IndirectProperty);
        set => SetValue(IndirectProperty, value);
    }

    public string? Filter
    {
        get => GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    /// <summary>A member's node (for tests and focus).</summary>
    public Button? NodeFor(string id) => _nodes.GetValueOrDefault(id);

    /// <summary>A member's place on the map (its top left corner).</summary>
    public Point? PlaceOf(string id) => _pos.TryGetValue(id, out var p) ? p : null;

    /// <summary>Whether three or more users run into <paramref name="id"/> as one bus (a rail when it is a library).</summary>
    public bool HasBus(string id) => _bus.Contains(id);

    public bool HasRail(string id) => _rail.ContainsKey(id);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            Build();
        }
        else if (change.Property == FocusedProperty || change.Property == IndirectProperty || change.Property == FilterProperty)
        {
            Mark();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Escape && Focused is not null)
        {
            Focused = null;
            e.Handled = true;
        }
    }

    private void Build()
    {
        Children.Clear();
        Children.Add(_lines);
        _nodes.Clear();
        if (Model is not { } m)
        {
            return;
        }
        Layout(m);
        foreach (var id in m.Columns.SelectMany(c => c))
        {
            var sub = (m.Library.Contains(id) ? "library · " : "")
                + string.Join(
                    " · ",
                    new[] { m.Uses[id].Count > 0 ? $"uses {m.Uses[id].Count}" : null, m.UsedBy[id].Count > 0 ? $"used by {m.UsedBy[id].Count}" : null }.OfType<string>()
                );
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = m.Label(id), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = sub.Length > 0 ? sub : "uses none here", Classes = { "small" }, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button
            {
                Content = text,
                Classes = { "dnode" },
                Width = NodeWidth,
                Height = NodeHeight,
                Tag = id,
            };
            button.Classes.Set("lib", m.Library.Contains(id));
            button.Classes.Set("cycle", m.Cycles.Any(c => c.Contains(id)));
            var tip = $"{m.Label(id)}: uses {m.Uses[id].Count}, used by {m.UsedBy[id].Count}";
            ToolTip.SetTip(button, tip);
            Avalonia.Automation.AutomationProperties.SetName(button, tip);
            var node = id;
            button.Click += (_, _) => Focused = Focused == node ? null : node;
            Children.Add(button);
            _nodes[id] = button;
        }
        Mark();
    }

    /// <summary>The places (app.js drawMap): layers top down, the second axis, side lanes, elbows, buses and rails.</summary>
    private void Layout(DependencyModel m)
    {
        _pos.Clear();
        _lane.Clear();
        _elbow.Clear();
        _bus.Clear();
        _rail.Clear();
        var layers = m.Columns.Count;
        var layerOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var l = 0; l < m.Columns.Count; l++)
        {
            foreach (var id in m.Columns[l])
            {
                layerOf[id] = l;
            }
        }
        var ids = m.Columns.SelectMany(c => c).ToArray();
        var twoAxis = ids.Length <= 12;
        int FanIn(string id) => twoAxis ? m.UsedBy[id].Count : 0;
        var buckets = ids.Select(FanIn).Distinct().Order().ToList();
        int ColIx(string id) => buckets.IndexOf(FanIn(id));
        var cell = new Dictionary<(int Layer, int Col), List<string>>();
        foreach (var id in ids)
        {
            var k = (layerOf[id], ColIx(id));
            if (!cell.TryGetValue(k, out var list))
            {
                cell[k] = list = [];
            }
            list.Add(id);
        }
        var colW = buckets.Select((_, c) => Math.Max(1, cell.Where(x => x.Key.Col == c).Select(x => x.Value.Count).DefaultIfEmpty(1).Max()) * (NodeWidth + Ga)).ToArray();
        var colX = colW.Select((_, c) => colW.Take(c).Sum()).ToArray();
        var xOf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (k, v) in cell)
        {
            for (var i = 0; i < v.Count; i++)
            {
                xOf[v[i]] = twoAxis ? colX[k.Col] + (i * (NodeWidth + Ga)) : ((colW[0] - (v.Count * (NodeWidth + Ga))) / 2) + (i * (NodeWidth + Ga));
            }
        }
        var gap = (DepEdge e) => Math.Abs(layerOf[e.From] - layerOf[e.To]);
        var placed = m.Drawn.Where(e => xOf.ContainsKey(e.From) && xOf.ContainsKey(e.To)).ToArray();
        var skips = !twoAxis ? [] : placed.Where(e => Math.Abs(xOf[e.From] - xOf[e.To]) < 0.5 && gap(e) > 1).OrderByDescending(gap).ToArray();
        for (var i = 0; i < skips.Length; i++)
        {
            _lane[skips[i]] = (i % 2 == 1 ? -1 : 1, i / 2);
        }
        var into = new Dictionary<string, List<DepEdge>>(StringComparer.Ordinal);
        foreach (var e in placed.Where(e => twoAxis && !_lane.ContainsKey(e) && gap(e) >= 2))
        {
            if (!into.TryGetValue(e.To, out var list))
            {
                into[e.To] = list = [];
            }
            list.Add(e);
        }
        foreach (var es in into.Values)
        {
            var sorted = es.OrderBy(e => xOf[e.From]).ToArray();
            for (var k = 0; k < sorted.Length; k++)
            {
                _elbow[sorted[k]] = (k, sorted.Length);
            }
        }
        if (twoAxis)
        {
            foreach (var g in placed.Where(e => layerOf[e.From] > layerOf[e.To]).GroupBy(e => e.To).Where(g => g.Count() >= 3))
            {
                _bus.Add(g.Key);
            }
        }
        var sideRoom = skips.Length > 0 ? 26 + (Math.Ceiling(skips.Length / 2.0) * LaneWidth) : 0;
        var px = 14 + sideRoom;
        foreach (var id in ids)
        {
            _pos[id] = new Point(px + xOf[id], Py + ((layers - 1 - layerOf[id]) * (NodeHeight + Gl)));
        }
        var railIds = _bus.Where(m.Library.Contains).ToArray();
        var gridRight = ids.Where(id => !railIds.Contains(id)).Select(id => _pos[id].X + NodeWidth).DefaultIfEmpty(px).Max();
        for (var i = 0; i < railIds.Length; i++)
        {
            var id = railIds[i];
            var from = m.Drawn.Where(e => e.To == id && _pos.ContainsKey(e.From))
                .Select(e => e.From)
                .OrderBy(u => _pos[u].Y)
                .ThenBy(u => xOf[u])
                .ToArray();
            var ys = from.Select(u => _pos[u].Y + (NodeHeight / 2)).ToArray();
            var mid = (ys[0] + ys[^1]) / 2;
            var x = gridRight + Rail + (i * (NodeWidth + Rail));
            _pos[id] = new Point(x, mid - (NodeHeight / 2));
            var track = new Dictionary<string, double>(StringComparer.Ordinal);
            for (var k = 0; k < from.Length; k++)
            {
                track[from[k]] = from.Length > 1 ? ((k / (double)(from.Length - 1)) - 0.5) * (Cable - 1.5) : 0;
            }
            _rail[id] = (x - Rail + 18, track);
        }
        var width = _pos.Values.Select(p => p.X).DefaultIfEmpty(0).Max() + NodeWidth + px;
        var height = _pos.Values.Select(p => p.Y).DefaultIfEmpty(0).Max() + NodeHeight + Py;
        _size = new Size(width, height);
    }

    /// <summary>What is lit: the focused member, what it uses and what uses it, matching the filter.</summary>
    private void Mark()
    {
        if (Model is not { } m)
        {
            return;
        }
        var f = Focused;
        var uses = f is null ? new HashSet<string>() : m.Reach(f, true, Indirect);
        var by = f is null ? new HashSet<string>() : m.Reach(f, false, Indirect);
        foreach (var (id, button) in _nodes)
        {
            button.Classes.Set("focus", id == f);
            button.Classes.Set("uses", uses.Contains(id));
            button.Classes.Set("usedby", by.Contains(id));
            button.Classes.Set("dim", !Lit(id, f, uses, by));
        }
        _lines.InvalidateVisual();
    }

    private bool Matches(string id) =>
        Filter is not { Length: > 0 } q || Model!.Label(id).Contains(q.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool Lit(string id, string? f, IReadOnlySet<string> uses, IReadOnlySet<string> by) =>
        (f is null || id == f || uses.Contains(id) || by.Contains(id)) && Matches(id);

    /// <summary>on uses, on usedby, dim or plain (app.js edgeClass).</summary>
    internal string EdgeClass(DepEdge e)
    {
        var f = Focused;
        if (f is not null)
        {
            var uses = Model!.Reach(f, true, Indirect);
            var by = Model.Reach(f, false, Indirect);
            if ((e.From == f || uses.Contains(e.From)) && uses.Contains(e.To) && (e.From == f || Indirect))
            {
                return "uses";
            }
            if ((e.To == f || by.Contains(e.To)) && by.Contains(e.From) && (e.To == f || Indirect))
            {
                return "usedby";
            }
            return "dim";
        }
        return Filter is { Length: > 0 } && !(Matches(e.From) || Matches(e.To)) ? "dim" : "";
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(NodeWidth, NodeHeight));
        }
        return _size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (id, button) in _nodes)
        {
            button.Arrange(new Rect(_pos[id], new Size(NodeWidth, NodeHeight)));
        }
        _lines.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private static string I(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>A line's path and how it enters the used member (for the arrowhead): its end and direction.</summary>
    internal (string Path, Point End, Vector Direction) PathOf(DepEdge e)
    {
        var a = _pos[e.From];
        var b = _pos[e.To];
        const double NW = NodeWidth;
        const double NH = NodeHeight;
        if (_rail.TryGetValue(e.To, out var r))
        {
            var bx = r.Jx;
            var ay = a.Y + (NH / 2);
            var ly = b.Y + (NH / 2) + r.Track.GetValueOrDefault(e.From);
            var d = ly > ay ? 10 : -10;
            if (Math.Abs(ly - ay) < 1)
            {
                return (I($"M {a.X + NW} {ay} L {bx + 16} {ly}"), new Point(bx + 16, ly), new Vector(1, 0));
            }
            return (
                I($"M {a.X + NW} {ay} L {bx - 10} {ay} Q {bx} {ay} {bx} {ay + d} L {bx} {ly - d} Q {bx} {ly} {bx + 10} {ly} L {bx + 16} {ly}"),
                new Point(bx + 16, ly),
                new Vector(1, 0)
            );
        }
        if (_bus.Contains(e.To) && a.Y < b.Y)
        {
            var bx = b.X + (NW * 0.72);
            var y = a.Y + (NH / 2);
            if (bx > a.X + NW)
            {
                return (I($"M {a.X + NW} {y} L {bx - 10} {y} Q {bx} {y} {bx} {y + 10} L {bx} {b.Y}"), new Point(bx, b.Y), new Vector(0, 1));
            }
            if (bx < a.X)
            {
                return (I($"M {a.X} {y} L {bx + 10} {y} Q {bx} {y} {bx} {y + 10} L {bx} {b.Y}"), new Point(bx, b.Y), new Vector(0, 1));
            }
            return (I($"M {bx} {a.Y + NH} L {bx} {b.Y}"), new Point(bx, b.Y), new Vector(0, 1));
        }
        if (_elbow.TryGetValue(e, out var el) && a.Y < b.Y)
        {
            var off = (el.K - ((el.N - 1) / 2.0)) * 10;
            var x = a.X + (NW / 2) + off;
            var y = b.Y + (NH / 2) + ((el.K - ((el.N - 1) / 2.0)) * 5);
            if (x < b.X - 12)
            {
                return (I($"M {x} {a.Y + NH} L {x} {y - 10} Q {x} {y} {x + 10} {y} L {b.X} {y}"), new Point(b.X, y), new Vector(1, 0));
            }
            return (
                I($"M {x} {a.Y + NH} C {x} {a.Y + NH + (Gl / 2)}, {b.X + (NW / 2)} {b.Y - (Gl / 2)}, {b.X + (NW / 2)} {b.Y}"),
                new Point(b.X + (NW / 2), b.Y),
                new Vector(0, 1)
            );
        }
        if (_lane.TryGetValue(e, out var l))
        {
            var left = _pos.Values.Min(p => p.X);
            var right = _pos.Values.Max(p => p.X) + NW;
            var x = l.Side > 0 ? right + 22 + (l.N * LaneWidth) : left - 22 - (l.N * LaneWidth);
            var ax = l.Side > 0 ? a.X + NW : a.X;
            var bx = l.Side > 0 ? b.X + NW : b.X;
            var shift = l.N % 2 == 1 ? 6 : -6;
            var ay = a.Y + (NH / 2) + shift;
            var by = b.Y + (NH / 2) + shift;
            var rr = 10 * l.Side;
            var dir = by > ay ? 1 : -1;
            return (
                I($"M {ax} {ay} L {x - rr} {ay} Q {x} {ay} {x} {ay + (dir * 10)} L {x} {by - (dir * 10)} Q {x} {by} {x - rr} {by} L {bx} {by}"),
                new Point(bx, by),
                new Vector(-l.Side, 0)
            );
        }
        if (Math.Abs(a.Y - b.Y) < 0.5)
        {
            var y = a.Y + NH;
            return (
                I($"M {a.X + (NW / 2)} {y} C {a.X + (NW / 2)} {y + 34}, {b.X + (NW / 2)} {y + 34}, {b.X + (NW / 2)} {y}"),
                new Point(b.X + (NW / 2), y),
                new Vector(0, -1)
            );
        }
        var down = b.Y > a.Y;
        var x1 = a.X + (NW / 2);
        var y1 = down ? a.Y + NH : a.Y;
        var x2 = b.X + (NW / 2);
        var y2 = down ? b.Y : b.Y + NH;
        var dd = down ? Gl / 2 : -Gl / 2;
        return (I($"M {x1} {y1} C {x1} {y1 + dd}, {x2} {y2 - dd}, {x2} {y2}"), new Point(x2, y2), new Vector(0, down ? 1 : -1));
    }

    private void DrawLines(DrawingContext context)
    {
        if (Model is not { } m)
        {
            return;
        }
        var edges = m.Drawn.Where(e => _pos.ContainsKey(e.From) && _pos.ContainsKey(e.To))
            .Select(e => (Edge: e, Class: EdgeClass(e)))
            .OrderBy(x => x.Class is "uses" or "usedby" ? 1 : 0)
            .ToArray();
        foreach (var (e, cls) in edges)
        {
            var brush = Brush(cls);
            var (path, end, direction) = PathOf(e);
            context.DrawGeometry(null, new Pen(brush, cls is "uses" or "usedby" ? 1.8 : 1.2), Geometry.Parse(path));
            if (!_rail.ContainsKey(e.To))
            {
                Arrow(context, brush, end, direction, 7);
            }
        }
        // the buses: where two or more lines run the same way, one trunk (or a library's cable) and one arrowhead
        foreach (var id in _bus.Where(_pos.ContainsKey))
        {
            var b = _pos[id];
            var classes = m.Drawn.Where(e => e.To == id && _pos.ContainsKey(e.From)).Select(EdgeClass).ToArray();
            var on = classes.FirstOrDefault(c => c is "uses" or "usedby");
            var cls = on ?? (classes.All(c => c is "dim") ? "dim" : "");
            var brush = Brush(cls);
            if (_rail.TryGetValue(id, out var r))
            {
                var ly = b.Y + (NodeHeight / 2);
                var hx = b.X - 9;
                context.DrawLine(new Pen(brush, Cable), new Point(r.Jx + 14, ly), new Point(hx, ly));
                Arrow(context, brush, new Point(b.X, ly), new Vector(1, 0), 9, (Cable / 2) + 4);
                continue;
            }
            var ys = m.Drawn.Where(e => e.To == id && _pos.ContainsKey(e.From) && _pos[e.From].Y < b.Y)
                .Select(e => _pos[e.From].Y + (NodeHeight / 2))
                .Order()
                .ToArray();
            if (ys.Length >= 2)
            {
                var bx = b.X + (NodeWidth * 0.72);
                context.DrawLine(new Pen(brush, 3.2), new Point(bx, ys[1] + 10), new Point(bx, b.Y - 8));
            }
        }
    }

    private static void Arrow(DrawingContext context, IBrush brush, Point tip, Vector direction, double length, double half = 4)
    {
        var back = new Point(tip.X - (direction.X * length), tip.Y - (direction.Y * length));
        var normal = new Vector(-direction.Y, direction.X) * half;
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(back + normal);
            g.LineTo(back - normal);
            g.EndFigure(true);
        }
        context.DrawGeometry(brush, null, head);
    }

    private IBrush Brush(string cls)
    {
        var key = cls switch
        {
            "uses" => "Accent",
            "usedby" => "CConvention",
            _ => "Axis",
        };
        var brush = this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush b ? b : Brushes.Gray;
        return cls is "dim" && brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.25) : brush;
    }

    /// <summary>The lines' layer, the first child: under the members, never hit by the pointer.</summary>
    private sealed class Lines : Control
    {
        private readonly DependencyMap _owner;

        public Lines(DependencyMap owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
            HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        public override void Render(DrawingContext context) => _owner.DrawLines(context);
    }
}
