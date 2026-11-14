using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Controls;
using Harness.Ui.Navigation;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>A workspace package this project uses: the versions it uses and the one its member declares.</summary>
public sealed record UseRow(StackUse Use)
{
    private bool Behind => Use.Declared is { } d && Use.Versions.Any(v => Versions.Compare(v, d) < 0);
    private bool Ahead => Use.Declared is { } d && Use.Versions.Any(v => Versions.Compare(v, d) > 0);
    public string Used => Use.Versions.Count > 0 ? string.Join(", ", Use.Versions) : "—";
    public string Declared => Use.Declared ?? "—";

    public string? Flag
    {
        get
        {
            if (Behind)
            {
                return "behind";
            }
            return Ahead ? "used is newer: is this declaration stale?" : null;
        }
    }

    public bool HasFlag => Flag is not null;
    public string Href => Route.ProjectHref(Use.MemberId, "architecture");
}

/// <summary>Versions compared part by part (app.js cmpVer): "1.4.0-alpha.29" comes before "1.4.0-alpha.42".</summary>
public static class Versions
{
    public static int Compare(string a, string b)
    {
        var pa = a.Split('.', '-', '+');
        var pb = b.Split('.', '-', '+');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : "";
            var y = i < pb.Length ? pb[i] : "";
            var c = int.TryParse(x, CultureInfo.InvariantCulture, out var nx) && int.TryParse(y, CultureInfo.InvariantCulture, out var ny)
                ? nx.CompareTo(ny)
                : string.CompareOrdinal(x, y);
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }
}

/// <summary>A package in the side panel, with its marks: a newer stable version, known vulnerabilities.</summary>
public sealed record PackageRow(string Name, string? Version, string? Newer, string? Vulnerable, bool Severe)
{
    public bool HasNewer => Newer is not null;
    public bool HasVulnerable => Vulnerable is not null;
    public string Line => Version is { Length: > 0 } v ? $"{Name} {v}" : Name;
}

/// <summary>A related unit in the side panel: its short name, and its path to select it by.</summary>
public sealed record RelatedRow(string Path, string Name);

/// <summary>A workspace member using the selected unit's package, and its projects that do.</summary>
public sealed record UserRow(string Member, string Projects);

/// <summary>
/// A project's Architecture (app.js "architecture"): Structure, Layers, Complexity or Change coupling by the address.
/// Structure and Layers share what they read; Complexity loads on its own; Change coupling (which may start
/// providers) only once it is shown.
/// </summary>
public sealed partial class ArchitectureViewModel : ObservableObject, IDisposable
{
    private readonly UiServices _services;
    private readonly string _id;
    private bool _couplingStarted;

    public ArchitectureViewModel(UiServices services, string projectId, Route route)
    {
        _services = services;
        _id = projectId;
        Structure = new StructureSection(services, projectId);
        Complexity = new ComplexitySection(services, projectId);
        Coupling = new CouplingSection(services, projectId);
        Show(route);
    }

    public StructureSection Structure { get; }
    public ComplexitySection Complexity { get; }
    public CouplingSection Coupling { get; }
    public ObservableCollection<TabItem> Sections { get; } = [];

    /// <summary>structure, layers, complexity or coupling.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsStructure), nameof(ShowsStructureOrLayers), nameof(ShowsLayers), nameof(ShowsComplexity), nameof(ShowsCoupling))]
    public partial string Section { get; private set; } = "structure";

    public bool ShowsStructure => Section is "structure";
    public bool ShowsStructureOrLayers => Section is "structure" or "layers";
    public bool ShowsLayers => Section is "layers";
    public bool ShowsComplexity => Section is "complexity";
    public bool ShowsCoupling => Section is "coupling";

    public void Start()
    {
        Structure.Follow(_services.Live);
        Complexity.Follow(_services.Live);
        _ = Structure.LoadAsync();
        _ = Complexity.LoadAsync();
        StartCoupling();
    }

    public void Show(Route route)
    {
        Section = route.Item is "layers" or "complexity" or "coupling" ? route.Item : "structure";
        Lists.Sync(
            Sections,
            new[] { ("structure", "Structure"), ("layers", "Layers"), ("complexity", "Complexity"), ("coupling", "Change coupling") }.Select(s =>
                new TabItem(s.Item1, s.Item2, Route.ProjectHref(_id, "architecture", s.Item1 is "structure" ? null : s.Item1), 0, s.Item1 == Section)
            )
        );
        StartCoupling();
    }

    public void Dispose()
    {
        Structure.Dispose();
        Complexity.Dispose();
        Coupling.Dispose();
    }

    /// <summary>Change coupling reads the history and may start providers: only once it is shown.</summary>
    private void StartCoupling()
    {
        if (!ShowsCoupling || _couplingStarted || Structure.ProjectId is null)
        {
            return;
        }
        _couplingStarted = true;
        Coupling.Follow(_services.Live);
        _ = Coupling.LoadAsync();
    }
}

/// <summary>
/// Structure and Layers (app.js viewArchitecture): what the project is built on, its units in layers and its
/// findings with their triage; the layer graph with selection and the selected unit beside it. The NuGet marks of
/// the packages come after the rest is drawn.
/// </summary>
public sealed partial class StructureSection : SectionViewModel
{
    private readonly UiServices _services;
    private StructureAnswer? _structure;
    private PackageMarks? _marks;
    private int _common;

    public StructureSection(UiServices services, string projectId)
        : base("", projectId, ["sync", "findings", "knowledge"])
    {
        _services = services;
        Board = new FindingsBoard(
            services,
            (action, body, token) => services.Api.FindingAsync(projectId, action, body, token),
            new KnowledgeActions(services, projectId, [])
        );
    }

    /// <summary>The findings and their triage.</summary>
    public FindingsBoard Board { get; }
    public ObservableCollection<StackPart> Parts { get; } = [];
    public ObservableCollection<UseRow> Uses { get; } = [];
    public ObservableCollection<RelatedRow> References { get; } = [];
    public ObservableCollection<RelatedRow> ReferencedBy { get; } = [];
    public ObservableCollection<RelatedRow> BuildsBefore { get; } = [];
    public ObservableCollection<UserRow> Users { get; } = [];
    public ObservableCollection<PackageRow> Packages { get; } = [];

    [ObservableProperty]
    public partial bool Known { get; private set; }

    [ObservableProperty]
    public partial string? Language { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string Detail { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<GraphRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<GraphEdge> Edges { get; private set; } = [];

    [ObservableProperty]
    public partial bool Big { get; private set; }

    [ObservableProperty]
    public partial string LayersHint { get; private set; } = "";

    /// <summary>The selected unit's path in the layer graph.</summary>
    [ObservableProperty]
    public partial string? Selected { get; set; }

    [ObservableProperty]
    public partial ArchProject? Unit { get; private set; }

    [ObservableProperty]
    public partial string SideHint { get; private set; } = "";

    [ObservableProperty]
    public partial string? PackageNotes { get; private set; }

    public bool HasUnit => Unit is not null;
    public bool HasStack => Parts.Count > 0 || Language is not null;
    public bool HasUses => Uses.Count > 0;
    public bool HasBuildsBefore => BuildsBefore.Count > 0;
    public string UnitLine { get; private set; } = "";
    public string? Publishes => Unit?.Package is { Length: > 0 } p ? "publishes " + p : null;
    public bool HasPublishes => Publishes is not null;
    public bool InNoSolution => Unit is { Solutions.Count: 0 } && _structure?.Architecture?.Language is "dotnet";
    public string RefWord => Imports ? "Imports" : "References";
    public string RefByWord => Imports ? "Imported by" : "Referenced by";
    public string PackagesHead => $"{PackageNoun} ({Unit?.Packages.Count ?? 0})";
    public string UsersNote => _structure?.Workspaces > 0 ? "by no other member" : "not in a workspace";
    public bool HasUsers => Users.Count > 0;
    public bool ShowsUsers => Unit?.Package is { Length: > 0 };

    private bool Imports => _structure?.Architecture?.Language is "typescript" or "go";

    private string PackageNoun =>
        _structure?.Architecture?.Language switch
        {
            "go" => "Modules",
            "typescript" => "npm packages",
            _ => "Packages",
        };

    partial void OnSelectedChanged(string? value) => DrawSide();

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var structure = _services.Api.StructureAsync(ProjectId!, token);
        StackAnswer? stack;
        try
        {
            stack = await _services.Api.StackAsync(ProjectId!, token);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            stack = null;
        }
        _structure = await structure;
        Draw(stack);
        _ = MarksAsync();
    }

    [RelayCommand]
    private void Pick(RelatedRow row) => Selected = row.Path;

    private void Draw(StackAnswer? stack)
    {
        var s = _structure!;
        Language = stack?.Stack?.Language;
        var st = stack?.Stack;
        Lists.Sync(Parts, new[] { st?.Runtime }.OfType<StackPart>().Concat(st?.Frameworks ?? []));
        Lists.Sync(Uses, (stack?.Uses ?? []).Select(u => new UseRow(u)));
        Known = s.Known && s.Architecture is not null;
        OnPropertyChanged(nameof(HasStack));
        OnPropertyChanged(nameof(HasUses));
        if (!Known)
        {
            Board.Show([], null, false);
            return;
        }
        var a = s.Architecture!;
        var noun = a.Noun;
        var all = a.Projects;
        var parts = all.Select(x => x.Name.Split('.')).ToArray();
        _common = 0;
        while (parts.Length > 0 && parts.All(q => q.Length > _common + 1 && q[_common] == parts[0][_common]))
        {
            _common++;
        }
        var production = all.Where(x => x.Kind is not "test").ToArray();
        var layers = production.Select(x => x.Layer).Distinct().Count();
        var tests = all.Count - production.Length;
        Summary =
            $"{production.Length} {(production.Length is 1 ? noun : noun + "s")} in {layers} layer{(layers is 1 ? "" : "s")}"
            + (tests > 0 ? $" · {tests} test {noun}s" : "");
        Detail = a.Language switch
        {
            "dotnet" => Solutions(a) + $" · graphify loads: {s.Input}",
            "go" => "Go module: its packages; references are imports within the module (other modules are dependencies).",
            _ when noun is "folder" => "TypeScript: the folders below the source root, each feature a unit of its own; references are imports (relative, tsconfig paths, Vite aliases).",
            _ => "TypeScript monorepo: the workspace packages; references are their dependencies on each other and imports of them.",
        };
        Board.Okay = $"✓ No structural smells: no circles, no lower layer references a higher one, no production code depends on tests, every {noun} is used.";
        Board.Show(s.Findings ?? a.Smells, s.Snapshot, s.Findings is not null);
        DrawGraph(a);
        DrawSide();
    }

    private static string Solutions(RepositoryArchitecture a)
    {
        if (a.Solutions.Count is 0)
        {
            return "no solution";
        }
        var word = a.Solutions.Count is 1 ? "solution" : "solutions";
        return $"{word}: {string.Join(", ", a.Solutions.Select(x => x.Path))}";
    }

    private string Short(ArchProject x)
    {
        var name = string.Join('.', x.Name.Split('.').Skip(_common));
        return name.Length > 0 ? name : x.Name;
    }

    /// <summary>The layer map: tests on top, then the layers from the highest down, the less used first in a row.</summary>
    private void DrawGraph(RepositoryArchitecture a)
    {
        var all = a.Projects;
        var users = _structure!.PackageUsers;
        int MemberCount(ArchProject x) => x.Package is { } p && users.TryGetValue(p, out var us) ? us.Select(u => u.MemberId).Distinct().Count() : 0;
        GraphNode Node(ArchProject x)
        {
            var n = MemberCount(x);
            var meta = x.Role ?? (x.Kind is "entry" ? "entry" : null);
            return new GraphNode(x.Path, Short(x), meta, n > 0 ? $"↗ {n}" : null, x.Name);
        }
        var rows = new List<GraphRow>();
        var tests = all.Where(x => x.Kind is "test").ToArray();
        if (tests.Length > 0)
        {
            rows.Add(new GraphRow("tests", [.. tests.Select(Node)]));
        }
        for (var l = a.Layers - 1; l >= 0; l--)
        {
            var layer = all.Where(x => x.Kind is not "test" && x.Layer == l)
                .OrderBy(x => x.ReferencedBy.Count)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (layer.Length > 0)
            {
                rows.Add(new GraphRow($"layer {l}", [.. layer.Select(Node)]));
            }
        }
        Rows = rows;
        Edges = [.. all.SelectMany(x =>
            x.References.Select(r => new GraphEdge(x.Path, r, false, x.Kind is "test"))
                .Concat(x.BuildOrder.Select(r => new GraphEdge(x.Path, r, true, x.Kind is "test"))))];
        Big = all.Count(x => x.Kind is not "test") > 24;
        var noun = a.Noun;
        var verb = Imports ? "import" : "reference";
        LayersHint =
            $"{char.ToUpperInvariant(noun[0])}{noun[1..]}s by what they {verb}: layer 0 {verb}s no other {noun} here, each layer above {verb}s the ones below; {noun}s in a circle share a layer. Tests sit on top.";
    }

    /// <summary>The selected unit: what it references and what references it, its packages with their marks, who uses it.</summary>
    private void DrawSide()
    {
        var a = _structure?.Architecture;
        var all = a?.Projects ?? [];
        var byPath = all.ToDictionary(x => x.Path, StringComparer.Ordinal);
        Unit = Selected is { } path ? byPath.GetValueOrDefault(path) : null;
        var noun = a?.Noun ?? "project";
        var verb = Imports ? "imports" : "references";
        SideHint =
            $"{(Big ? $"Point at a {noun} to see its lines; select" : "Select")} a {noun} to see what it {verb}, what {verb} it, its packages and who uses it. Lines point from a {noun} to the {noun}s it {verb}"
            + (all.Any(x => x.BuildOrder.Count > 0) ? "; a dashed one to a project it only builds first." : ".");
        RelatedRow Related(string p) => new(p, byPath.TryGetValue(p, out var x) ? Short(x) : p);
        Lists.Sync(References, (Unit?.References ?? []).Select(Related));
        Lists.Sync(ReferencedBy, (Unit?.ReferencedBy ?? []).Select(Related));
        Lists.Sync(BuildsBefore, (Unit?.BuildOrder ?? []).Select(Related));
        var users = Unit?.Package is { } package && _structure!.PackageUsers.TryGetValue(package, out var us) ? us : [];
        Lists.Sync(
            Users,
            users.GroupBy(u => u.Member)
                .Select(g => new UserRow(g.Key, string.Join(", ", g.Select(u => Path.GetFileNameWithoutExtension(u.Project.Split('/')[^1])))))
        );
        Lists.Sync(Packages, (Unit?.Packages ?? []).Select(Package));
        if (Unit is { } u)
        {
            var role = u.Role is { Length: > 0 } r && r is not "test" ? $"{r} (from its name)" : null;
            UnitLine = string.Join(" · ", new[] { u.Kind, role, $"layer {u.Layer}" }.Where(x => x is { Length: > 0 }));
        }
        PackageNotes = Unit is { Packages.Count: > 0 } && _marks is { Notes.Count: > 0 } m ? string.Join("; ", m.Notes) : null;
        foreach (
            var name in new[]
            {
                nameof(HasUnit), nameof(UnitLine), nameof(Publishes), nameof(HasPublishes), nameof(InNoSolution), nameof(RefWord), nameof(RefByWord),
                nameof(PackagesHead), nameof(UsersNote), nameof(HasUsers), nameof(ShowsUsers), nameof(HasBuildsBefore),
            }
        )
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>A package with its marks, once they have arrived (app.js pkgInfo, pkgMarks).</summary>
    private PackageRow Package(PackageReference p)
    {
        PackageMark? mark = null;
        if (_marks is { } marks && p.Version is { } version && !p.Unresolved)
        {
            var plain = version.Trim().TrimStart('[').TrimEnd(']');
            var key = $"{p.Name}@{plain}";
            mark = marks.Packages.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        }
        var newer = mark?.Latest is { Length: > 0 } latest ? $"Newer stable version {latest} available (you use {p.Version})" : null;
        var vulns = mark?.Vulnerabilities ?? [];
        var vulnerable = vulns.Count > 0
            ? string.Join("\n", vulns.Select(v => $"Known {v.Severity} vulnerability in {mark!.Version}: {v.Advisory} (affects {v.Versions})" + (v.Fixed is { Length: > 0 } f ? $"; fixed in {f}" : "")))
            : null;
        return new PackageRow(p.Name, p.Version, newer, vulnerable, vulns.Any(v => v.Severity is "high" or "critical"));
    }

    /// <summary>The NuGet marks, asked once the view is drawn: the view never waits for the package sources.</summary>
    private async Task MarksAsync()
    {
        if (_structure?.Architecture is not { Language: "dotnet" } a)
        {
            return;
        }
        var wanted = a.Projects.SelectMany(x => x.Packages)
            .Where(p => p.Version is { Length: > 0 } && !p.Unresolved)
            .Select(p => $"{p.Name}@{p.Version}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (wanted.Length is 0)
        {
            return;
        }
        try
        {
            _marks = await _services.Api.PackageMarksAsync(ProjectId!, wanted, CancellationToken.None);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _marks = new PackageMarks();
        }
        DrawSide();
    }
}

/// <summary>A function among the most complex: its complexity's level, name and place.</summary>
public sealed record ComplexRow(ComplexFunction Function, string Level)
{
    public string Place => $"{Function.Path}:{Function.Line}";
    public bool IsProblem => Level is "problem";
    public bool IsWarn => Level is "warn";
    public bool IsNote => Level is "info";
}

/// <summary>A language's complexity (app.js complexityChart): the chart, the most complex functions by level, the table.</summary>
public sealed partial class LanguageCx : ObservableObject
{
    private readonly IReadOnlyList<ComplexRow> _top;
    private readonly HashSet<string> _levels = ["problem", "warn", "info", "low"];

    public LanguageCx(LanguageComplexity l, int binWidth)
    {
        Language = l;
        BinWidth = binWidth;
        Bars = [.. l.Bins.Select(b => new ComplexityBar(b.From, b.To, b.Count))];
        _top = [.. l.Top.Select(f => new ComplexRow(f, LevelOf(f.Complexity, l.Thresholds)))];
        var present = new[] { ("problem", "problems"), ("warn", "warnings"), ("info", "notes"), ("low", "below the thresholds") }
            .Where(x => _top.Any(t => t.Level == x.Item1))
            .ToArray();
        Chips = present.Length < 2 ? [] : [.. present.Select(x => new FilterChip(x.Item1, $"{_top.Count(t => t.Level == x.Item1)} {x.Item2}", 0, Toggled) { On = true })];
        Lists.Sync(Top, _top);
    }

    public LanguageComplexity Language { get; }
    public int BinWidth { get; }
    public IReadOnlyList<ComplexityBar> Bars { get; }
    public IReadOnlyList<FilterChip> Chips { get; }
    public bool HasChips => Chips.Count > 0;
    public ObservableCollection<ComplexRow> Top { get; } = [];
    public bool HasTop => _top.Count > 0;

    public string Title =>
        Language.Language switch
        {
            "csharp" => "C#",
            "go" => "Go",
            "typescript" => "TypeScript and JavaScript",
            _ => Language.Language,
        };

    public string Line => $"{Language.Functions} functions, the most complex at {Language.Max}";
    public string Name => $"{Title}: {Line}";

    public static string LevelOf(int complexity, ComplexityThresholds t)
    {
        if (complexity >= t.Problem)
        {
            return "problem";
        }
        if (complexity >= t.Warn)
        {
            return "warn";
        }
        return complexity >= t.Note ? "info" : "low";
    }

    private void Toggled(FilterChip chip)
    {
        if (chip.On)
        {
            _levels.Add(chip.Key);
        }
        else if (Chips.Any(c => c.On))
        {
            _levels.Remove(chip.Key);
        }
        else
        {
            chip.On = true;
        }
        Lists.Sync(Top, _top.Where(t => _levels.Contains(t.Level)));
    }
}

/// <summary>Complexity (app.js complexitySection): every function by cyclomatic complexity, per language reported.</summary>
public sealed partial class ComplexitySection(UiServices services, string projectId)
    : SectionViewModel("Complexity distribution", projectId, ["sync"])
{
    public ObservableCollection<LanguageCx> Languages { get; } = [];
    public bool IsEmpty => Languages.Count is 0;

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var d = await services.Api.ComplexityAsync(ProjectId!, token);
        Languages.Clear();
        foreach (var l in d.Languages)
        {
            Languages.Add(new LanguageCx(l, d.BinWidth));
        }
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>A file in Change coupling: how often it changed, its most complex function, what changes with it.</summary>
public sealed record HotspotRow(Hotspot Hotspot, double Ratio, int? Complexity, string Level)
{
    public string Name => Hotspot.Path.Split('/')[^1];
    public string Folder => Hotspot.Path.Length > Name.Length ? Hotspot.Path[..^Name.Length] : "";
    public string ComplexityText => Complexity?.ToString(CultureInfo.InvariantCulture) ?? "–";
    public bool IsProblem => Level is "problem";
    public bool IsWarn => Level is "warn";
    public IReadOnlyList<string> Partners => [.. Hotspot.Partners.Select(p => $"{p.Path.Split('/')[^1]} {Overview.Pct(p.Share)}")];
    public bool HasPartners => Hotspot.Partners.Count > 0;
}

/// <summary>
/// Change coupling (app.js couplingSection): the code files changed most in 180 days, the files that change with
/// them, and the usual partners an uncommitted change leaves out; with the files' complexity, the hotspot chart.
/// </summary>
public sealed partial class CouplingSection(UiServices services, string projectId) : SectionViewModel("Change coupling", projectId, ["sync"])
{
    public ObservableCollection<HotspotRow> Rows { get; } = [];
    public ObservableCollection<string> Missing { get; } = [];

    [ObservableProperty]
    public partial string? MissingHead { get; private set; }

    [ObservableProperty]
    public partial string? AllPresent { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<HotspotPoint> Points { get; private set; } = [];

    [ObservableProperty]
    public partial ComplexityThresholds Thresholds { get; private set; } = new();

    [ObservableProperty]
    public partial string? Errors { get; private set; }

    public bool HasChart => Points.Count >= 3;
    public bool IsEmpty => Rows.Count is 0;
    public bool HasMissing => Missing.Count > 0;

    public string ChartName =>
        $"Changing files by commits and complexity; the worst: {string.Join(", ", Points.Where(p => p.Named).Select(p => p.Name))}";

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var coupling = services.Api.CouplingAsync(ProjectId!, token);
        ComplexityAnswer? cx;
        try
        {
            cx = await services.Api.ComplexityAsync(ProjectId!, token);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            cx = null;
        }
        var c = await coupling;
        var files = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, value) in (cx?.Languages ?? []).SelectMany(l => l.Files))
        {
            files[path] = value;
        }
        Thresholds = cx is { Languages.Count: > 0 } ? cx.Languages[0].Thresholds : new ComplexityThresholds();
        var most = Math.Max(1, c.Hotspots.Count > 0 ? c.Hotspots.Max(h => h.Commits) : 1);
        Lists.Sync(
            Rows,
            c.Hotspots.Select(h =>
            {
                int? k = files.TryGetValue(h.Path, out var v) ? v : null;
                return new HotspotRow(h, h.Commits / (double)most, k, k is { } x ? LanguageCx.LevelOf(x, Thresholds) : "low");
            })
        );
        var points = c.Hotspots.Where(h => files.ContainsKey(h.Path)).Select(h => (h, k: files[h.Path])).ToArray();
        var cmax = Math.Max(1, points.Length > 0 ? points.Max(p => p.h.Commits) : 1);
        var kmax = Math.Max(Thresholds.Problem + 5, points.Length > 0 ? points.Max(p => p.k) : 1);
        var named = points.OrderByDescending(p => p.h.Commits / (double)cmax * (p.k / (double)kmax)).Take(4).Select(p => p.h.Path).ToHashSet(StringComparer.Ordinal);
        Points = [.. points.Select(p => new HotspotPoint(p.h.Path.Split('/')[^1], p.h.Commits, p.k, named.Contains(p.h.Path)))];
        Lists.Sync(
            Missing,
            c.Missing.Select(m => $"{m.Partner.Path} usually changes with {m.Changed} ({m.Partner.Together} commits together, {Overview.Pct(m.Partner.Share)} of its commits)")
        );
        MissingHead = c.Missing.Count > 0
            ? $"Your uncommitted change leaves out {Shell.Plural(c.Missing.Count, "file")} that usually change with it (a reminder, not a rule)"
            : null;
        AllPresent = c.Changed is { } changed && c.Missing.Count is 0
            ? $"✓ Your uncommitted change ({Shell.Plural(changed.Count, "file")}) leaves out none of their usual partners."
            : null;
        Errors = c.Errors.Count > 0 ? string.Join("; ", c.Errors) : null;
        foreach (var name in new[] { nameof(HasChart), nameof(IsEmpty), nameof(HasMissing), nameof(ChartName) })
        {
            OnPropertyChanged(name);
        }
    }
}
