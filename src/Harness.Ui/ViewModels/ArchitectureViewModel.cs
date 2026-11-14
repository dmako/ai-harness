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

/// <summary>How the page names findings' kinds, their risks and why they matter (app.js SMELL_KIND, SMELL_RISK, SMELL_WHY).</summary>
public static partial class Findings
{
    public static string Kind(string kind) =>
        kind switch
        {
            "layering" => "lower layers referencing higher ones",
            "deep-import" => "reaching into a feature's internals",
            "unused" => "not used",
            "duplicate" => "functions written more than once",
            "template" => "functions following one template",
            "interface-candidate" => "contracts written once per variant",
            "repeated-sequence" => "statements written out in several functions",
            "shared-code" => "code repeated across members",
            "boundary" => "dependencies your rules forbid",
            "rule-unclear" => "rules not understood",
            "stale-knowledge" => "knowledge naming code that is gone",
            "no-solution" => "in no solution",
            "test-reference" => "production code referencing tests",
            "missing-project" => "references to what is not there",
            "cycle" => "circles",
            "complexity" => "functions with many paths through them",
            _ => kind,
        };

    public static string? Risk(string kind) =>
        kind switch
        {
            "layering" => "a lower layer cannot change without a higher one",
            "deep-import" => "a change inside a feature breaks what imports it",
            "unused" => "code to read and keep that nothing needs",
            "duplicate" => "a fix in one copy misses the others",
            "template" => "the same steps maintained in several places",
            "interface-candidate" => "each new variant means editing every place that chooses",
            "repeated-sequence" => "a change to the steps must be made in each",
            "shared-code" => "a fix in one member's copy misses the others",
            "boundary" => "it breaks a rule recorded as knowledge",
            "complexity" => "hard to understand and test: bugs hide in the paths",
            _ => null,
        };

    public static readonly IReadOnlyList<(string Level, string Label)> Levels = [("problem", "problems"), ("warn", "warnings"), ("info", "notes")];

    public static int Rank(string level) =>
        level switch
        {
            "problem" => 0,
            "warn" => 1,
            _ => 2,
        };

    public static string Icon(string level) =>
        level switch
        {
            "problem" => "st-fail",
            "warn" => "st-warn",
            _ => "level-info",
        };

    /// <summary>A finding's subject: its title up to what is said of it ("Findings has complexity 50": "Findings").</summary>
    public static string Subject(Finding x)
    {
        var inner = InnerName().Match(x.Title);
        if (inner.Success)
        {
            return inner.Groups[1].Value;
        }
        var cut = SubjectEnd().Split(x.Title)[0];
        return SubjectStart().Replace(cut, "");
    }

    [GeneratedRegex(@"^\d.*?\(([^,)]+)", RegexOptions.None, 1000)]
    private static partial Regex InnerName();

    [GeneratedRegex(@": | (?:has|is|are|repeats|follows|and \d+ more|written|takes|reach(?:es)?) ", RegexOptions.None, 1000)]
    private static partial Regex SubjectEnd();

    [GeneratedRegex("^(Functions?|Types?) ", RegexOptions.None, 1000)]
    private static partial Regex SubjectStart();
}

/// <summary>A finding as listed (app.js smellsSection item): its chips, what could help, why, its rule, evidence and triage.</summary>
public sealed record FindingRow(Finding Finding, bool InGroup)
{
    public string Icon => Findings.Icon(Finding.Level);
    public bool IsProblem => Finding.Level is "problem";
    public bool IsWarn => Finding.Level is "warn";

    public string? Basis =>
        Finding.Basis switch
        {
            "heuristic" when Finding.Confidence is { } c => $"heuristic · {Overview.Pct(c)}",
            { Length: > 0 } b => b,
            _ => null,
        };

    public bool HasBasis => Basis is not null;
    public bool Fresh => Finding.Status is "new" or "changed";

    public string? MarkTag
    {
        get
        {
            if (Finding.Mark is not { } m)
            {
                return null;
            }
            var state = m.State switch
            {
                "by_design" => "by design",
                "review" => "back for review",
                _ => "task",
            };
            return m.TaskId is { Length: > 0 } t ? $"{state} {t}" : state;
        }
    }

    public bool HasMark => Finding.Mark is not null;
    public bool Quiet => Finding.Mark?.State is "by_design";
    public string? Risk => InGroup ? null : Findings.Risk(Finding.Kind);
    public bool HasRisk => Risk is not null;
    public bool HasFix => !InGroup && Finding.Fix is { Length: > 0 };
    public string FixLine => "Could help: " + Finding.Fix;
    public bool HasWhy => Finding.Why is { Length: > 0 };

    public string? RuleLine
    {
        get
        {
            if (Finding.Rule is not { Line: { Length: > 0 } line } r)
            {
                return null;
            }
            var head = Finding.Kind is "stale-knowledge" ? "Names" : "Rule";
            var at = r.Scope is { Length: > 0 } s ? " at " + s : "";
            return $"{head}: {line} — {r.Type ?? "memory"}{at} ({r.Memory})";
        }
    }

    public bool HasRule => RuleLine is not null;
    public bool HasEvidence => Finding.Evidence.Count > 0;
    public string EvidenceHead => $"Evidence ({Finding.Evidence.Count})";

    public string? FoundBy
    {
        get
        {
            if (Finding.Source?.Provider is not { Length: > 0 } p)
            {
                return null;
            }
            var who = p is "harness" ? "harness, reading the files" : p;
            var tool = Finding.Source.Tool is { Length: > 0 } t ? $" ({t})" : "";
            return "Found by " + who + tool;
        }
    }

    public bool HasFoundBy => FoundBy is not null;

    /// <summary>The mark's story: back for review, by design with its reason, or its task.</summary>
    public string? MarkStory =>
        Finding.Mark switch
        {
            { State: "review" } m => $"Back for review: {m.ReviewReason}. It was marked by design: {m.Reason}",
            { State: "by_design" } m => $"By design: {m.Reason}" + (m.ProposedBy is { Length: > 0 } by ? $" (proposed by {by})" : ""),
            { State: "task" } m => $"Task {m.TaskId}" + (Finding.Status is "gone" ? ": the finding is gone, resolved." : "."),
            _ => null,
        };

    public bool HasMarkStory => MarkStory is not null;
    public bool Triaged => Finding.Key is { Length: > 0 } && Finding.Status is not "gone";
    public bool CanMark => Triaged && Finding.Mark is null or { State: "review" };
    public string MarkLabel => Finding.Mark is null ? "By design…" : "Still by design…";
    public bool CanTask => Triaged && Finding.Mark?.State is not "task";
    public string TaskLabel => Finding.Rule is not null ? "Fix the code (task)…" : "Make a task…";
    public bool CanChangeKnowledge => Triaged && Finding.Rule?.Memory is { Length: > 0 };
    public string ChangeLabel => $"Change the {Finding.Rule?.Type ?? "memory"}…";
    public bool CanRetireKnowledge => CanChangeKnowledge && Finding.Kind is "stale-knowledge";
    public bool CanUnmark => Triaged && Finding.Mark is not null;
    public string UnmarkLabel => Finding.Mark?.State is "task" ? "Unlink the task" : "Take the mark back";
}

/// <summary>
/// A kind's findings (app.js smellsSection): fewer than three listed one by one, three or more as one row opening to
/// the list, with a chip per level when they span levels, the first eight shown and the rest folded.
/// </summary>
public sealed partial class FindingGroup : ObservableObject
{
    private readonly IReadOnlyList<Finding> _all;
    private readonly HashSet<string> _levels;

    public FindingGroup(string kind, IReadOnlyList<Finding> findings)
    {
        Kind = kind;
        _all = findings;
        IsGroup = findings.Count >= 3;
        var present = Findings.Levels.Where(l => findings.Any(f => f.Level == l.Level)).ToArray();
        _levels = [.. present.Select(l => l.Level)];
        Chips = present.Length < 2 ? [] : [.. present.Select(l => new FilterChip(l.Level, $"{findings.Count(f => f.Level == l.Level)} {l.Label}", 0, Toggled) { On = true })];
        SameFix = findings.All(f => f.Fix == findings[0].Fix) ? findings[0].Fix : null;
        Draw();
    }

    public string Kind { get; }
    public bool IsGroup { get; }
    public IReadOnlyList<FilterChip> Chips { get; }
    public bool HasChips => Chips.Count > 0;
    public string? SameFix { get; }
    public bool HasSameFix => SameFix is { Length: > 0 };
    public string SameFixLine => "Could help: " + SameFix;
    public string Level => Findings.Levels.First(l => _all.Any(f => f.Level == l.Level) || l.Level is "info").Level;
    public string Icon => Findings.Icon(Level);
    public bool IsProblem => Level is "problem";
    public bool IsWarn => Level is "warn";
    public string Title => $"{_all.Count} × {Findings.Kind(Kind)}";
    public int Fresh => _all.Count(f => f.Status is "new" or "changed");
    public bool HasFresh => Fresh > 0;
    public string FreshTag => $"{Fresh} new or changed";
    public string Why => _all[0].Why ?? "";

    public string Summary =>
        (Findings.Risk(Kind) is { } risk ? risk : "")
        + (Kind is "complexity" ? " · worst: " : " · e.g. ")
        + string.Join(", ", _all.Take(3).Select(Findings.Subject));

    public ObservableCollection<FindingRow> Shown { get; } = [];
    public ObservableCollection<FindingRow> More { get; } = [];
    public bool HasMore => More.Count > 0;
    public string MoreHead => $"{More.Count} more, worth less";

    private void Toggled(FilterChip chip)
    {
        if (chip.On)
        {
            _levels.Add(chip.Key);
        }
        else if (_levels.Count > 1)
        {
            _levels.Remove(chip.Key);
        }
        else
        {
            chip.On = true;
        }
        Draw();
    }

    private void Draw()
    {
        var rows = _all.Where(f => _levels.Contains(f.Level)).Select(f => new FindingRow(f, IsGroup && SameFix is not null)).ToArray();
        var shown = IsGroup ? rows.Take(8) : rows;
        Lists.Sync(Shown, shown);
        Lists.Sync(More, IsGroup ? rows.Skip(8) : []);
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(MoreHead));
    }
}

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
        Actions = new KnowledgeActions(services, projectId, []);
    }

    public KnowledgeActions Actions { get; }
    public ObservableCollection<StackPart> Parts { get; } = [];
    public ObservableCollection<UseRow> Uses { get; } = [];
    public ObservableCollection<FindingGroup> Groups { get; } = [];
    public ObservableCollection<Finding> Gone { get; } = [];
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
    public partial string? SnapshotLine { get; private set; }

    [ObservableProperty]
    public partial bool ShowMarked { get; set; }

    [ObservableProperty]
    public partial int MarkedCount { get; private set; }

    [ObservableProperty]
    public partial string? Okay { get; private set; }

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
    public bool HasGone => Gone.Count > 0;
    public string GoneHead => $"Gone ({Gone.Count})";
    public bool HasMarked => MarkedCount > 0;
    public string MarkedLabel => $"show {MarkedCount} marked by design";
    public bool HasFindings => Groups.Count > 0;
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

    partial void OnShowMarkedChanged(bool value) => DrawFindings();

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

    /// <summary>A finding marked by design, with the reason recorded as a decision.</summary>
    [RelayCommand]
    private Task<bool> MarkAsync(FindingRow row) =>
        _services.Dialogs.ShowAsync(
            new FindingMarkDialog(_services, ProjectId!, row.Finding)
            {
                Title = row.Finding.Mark is null ? "Mark by design" : "Still by design?",
                Sub = row.Finding.Title,
                Wide = true,
                AcceptLabel = "Mark by design",
            }
        );

    [RelayCommand]
    private async Task TaskAsync(FindingRow row)
    {
        var confirm = new ConfirmDialog
        {
            Title = "Make it a task?",
            Sub = row.Finding.Title,
            Message = "An ARCH task in this project with the finding's why, what could help and its evidence; the finding links to it and shows resolved once it is gone. It lives in harness, not in a backlog file.",
            AcceptLabel = "Make a task",
        };
        if (await _services.Dialogs.ConfirmAsync(confirm))
        {
            await ActAsync("task", new FindingAction { Key = row.Finding.Key!, Project = "" }, "Task made");
        }
    }

    [RelayCommand]
    private Task UnmarkAsync(FindingRow row) => ActAsync("unmark", new FindingAction { Key = row.Finding.Key! }, "Mark taken back");

    /// <summary>The knowledge a finding is about, corrected instead of the code.</summary>
    [RelayCommand]
    private async Task ChangeKnowledgeAsync(FindingRow row) => await KnowledgeAsync(row, correct: true);

    [RelayCommand]
    private async Task RetireKnowledgeAsync(FindingRow row) => await KnowledgeAsync(row, correct: false);

    [RelayCommand]
    private void Pick(RelatedRow row) => Selected = row.Path;

    private async Task KnowledgeAsync(FindingRow row, bool correct)
    {
        var rule = row.Finding.Rule!;
        var level = rule.Scope is { } s && MemoryTypes.IsLevel(s) ? s : null;
        try
        {
            var memory = (await _services.Api.MemoryAsync(ProjectId, rule.Memory!, level, CancellationToken.None)).Memory;
            await (correct ? Actions.CorrectAsync(memory) : Actions.RetireAsync(memory));
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _services.Toasts.Fail(exception.Message);
        }
    }

    private async Task ActAsync(string action, FindingAction body, string done)
    {
        try
        {
            await _services.Api.FindingAsync(ProjectId!, action, body, CancellationToken.None);
            _services.Toasts.Show(done);
            _services.Live.RefreshAll();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _services.Toasts.Fail(exception.Message);
        }
    }

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
            Groups.Clear();
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
        Okay = $"✓ No structural smells: no circles, no lower layer references a higher one, no production code depends on tests, every {noun} is used.";
        DrawFindings();
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

    private static string? Snapshot(StructureAnswer? s)
    {
        if (s?.Findings is null)
        {
            return null;
        }
        return s.Snapshot is { } snap
            ? $"Since the last change ({Shell.Ago(snap.TakenAt)}): {snap.Added} new, {snap.Changed} changed, {snap.Gone} gone"
            : "Not synced yet: harness sync records them, so they show as new, changed or gone.";
    }

    private void DrawFindings()
    {
        var s = _structure;
        var list = s?.Findings ?? s?.Architecture?.Smells ?? [];
        var active = list.Where(x => x.Status is not "gone").ToArray();
        MarkedCount = active.Count(x => x.Mark?.State is "by_design");
        var shown = active.Where(x => ShowMarked || x.Mark?.State is not "by_design").ToArray();
        // kinds in the order their first finding comes (the server's: most worth acting on first)
        Groups.Clear();
        foreach (var kind in shown.Select(x => x.Kind).Distinct(StringComparer.Ordinal))
        {
            Groups.Add(new FindingGroup(kind, [.. shown.Where(x => x.Kind == kind)]));
        }
        Lists.Sync(Gone, list.Where(x => x.Status is "gone"));
        SnapshotLine = Snapshot(s);
        foreach (var name in new[] { nameof(HasGone), nameof(GoneHead), nameof(HasMarked), nameof(MarkedLabel), nameof(HasFindings) })
        {
            OnPropertyChanged(name);
        }
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

/// <summary>Marking a finding by design: why it is intended (recorded as a decision for agents working there).</summary>
public sealed partial class FindingMarkDialog(UiServices services, string projectId, Finding finding) : FormDialog<bool>
{
    [ObservableProperty]
    public partial string Reason { get; set; } = finding.Mark?.Reason ?? "";

    public static string Note =>
        "It stays listed, marked, and hides unless marked ones are shown. The reason is recorded as a decision for agents working there; it comes back for review if it grows.";

    protected override string? Validate() => Reason.Trim().Length is 0 ? "Why it is intended is required." : null;

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        await services.Api.FindingAsync(projectId, "mark", new FindingAction { Key = finding.Key!, Reason = Reason.Trim() }, token);
        services.Toasts.Show("Marked by design");
        services.Live.RefreshAll();
        return true;
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
