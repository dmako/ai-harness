using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Harness.Ui.Api;
using Harness.Ui.Controls;
using Harness.Ui.Navigation;

namespace Harness.Ui.ViewModels;

/// <summary>How the findings rollup weighs a row (app.js sev): problems first, then warnings, then notes.</summary>
public static class Rollup
{
    public static int Severity(RollupRow r) =>
        (r.Levels.GetValueOrDefault("problem") * 10000) + (r.Levels.GetValueOrDefault("warn") * 100) + r.Levels.GetValueOrDefault("info");

    public static int Total(RollupRow r) => r.Levels.GetValueOrDefault("problem") + r.Levels.GetValueOrDefault("warn") + r.Levels.GetValueOrDefault("info");
}

/// <summary>A row of the findings rollup (app.js findingsRollup row): a member or what lies between them, and its open findings.</summary>
public sealed record RollupLine(RollupRow Row, string Href, int Most)
{
    public string Name => Row.Name;
    public int Total => Rollup.Total(Row);
    public bool HasNote => Row.Note is { Length: > 0 };
    public bool Clear => !HasNote && Total is 0;
    public bool HasBar => !HasNote && Total > 0;

    public IReadOnlyList<BarSegment> Bar =>
        [
            new("Critical", Row.Levels.GetValueOrDefault("problem")),
            new("Warning", Row.Levels.GetValueOrDefault("warn")),
            new("Accent", Row.Levels.GetValueOrDefault("info")),
            new("Grid", Math.Max(0, Most - Total)),
        ];

    public string Counts =>
        string.Join(
            ", ",
            new[] { ("problem", "problem"), ("warn", "warning"), ("info", "note") }
                .Where(l => Row.Levels.GetValueOrDefault(l.Item1) > 0)
                .Select(l => Shell.Plural(Row.Levels[l.Item1], l.Item2))
        );

    public string Marks =>
        string.Join(
            " · ",
            new[] { Row.Review > 0 ? $"{Row.Review} back for review" : null, Row.ByDesign > 0 ? $"{Row.ByDesign} by design" : null }.OfType<string>()
        );

    public bool HasMarks => Marks.Length > 0;
    public string When => Row.Snapshot?.TakenAt is { } at ? Shell.Ago(at) : "";
}

/// <summary>
/// The findings rollup (app.js findingsRollup): the open findings per member, the worst first, then between them (the
/// services' calls, the code repeated across them), as each one's last sync recorded them.
/// </summary>
public sealed partial class RollupSection(WorkspaceContext context) : SectionViewModel("Findings", null, ["sync", "findings", "levels"])
{
    public ObservableCollection<RollupLine> Members { get; } = [];
    public ObservableCollection<RollupLine> Between { get; } = [];
    public bool HasBetween => Between.Count > 0;

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var rows = (await context.Services.Api.RollupAsync(context.Name, token)).Rows;
        var most = Math.Max(1, rows.Select(Rollup.Total).DefaultIfEmpty(0).Max());
        string Href(RollupRow r) =>
            r.Kind switch
            {
                "member" => Route.ProjectHref(r.MemberId ?? "", "architecture"),
                "shared" => Route.WorkspaceHref(context.Name, "issues", "shared"),
                _ => Route.WorkspaceHref(context.Name, "architecture"),
            };
        Lists.Sync(
            Members,
            rows.Where(r => r.Kind is "member")
                .OrderByDescending(Rollup.Severity)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new RollupLine(r, Href(r), most))
        );
        Lists.Sync(Between, rows.Where(r => r.Kind is not "member").Select(r => new RollupLine(r, Href(r), most)));
        OnPropertyChanged(nameof(HasBetween));
    }
}

/// <summary>A design issue's kind (app.js ISSUE_KINDS, ISSUE_TODO): its name, why it matters, what to do.</summary>
public static class IssueKinds
{
    public static readonly IReadOnlyList<(string Kind, string Label, string Why)> All =
    [
        ("cycle", "Cycles", "Projects that use each other's packages can't be changed or released on their own."),
        ("local-copy", "Local copies", "Projects keeping their own copy of something a project they already use provides: the copies drift."),
        ("duplicate", "Duplicates", "The same code in several projects, to be fixed and changed in each."),
        ("version-drift", "Version drift", "The same package at different versions in different projects."),
        ("hub", "Hubs", "Services many others depend on directly: their changes ripple."),
    ];

    public static string Label(string kind) => All.FirstOrDefault(k => k.Kind == kind).Label ?? kind;

    public static string? Todo(string kind) =>
        kind switch
        {
            "cycle" => "Find the dependency that points the wrong way and move what both need into a shared library, or turn the call into an event or an API call.",
            "local-copy" => "Replace the copy with the provided type (or derive from it, if the project needs to adjust it), then delete the copy.",
            "duplicate" => "If it is one concern, move it into a library the projects share; if the copies are meant to differ, give them distinct names.",
            "version-drift" => "Align the versions (a central package version file helps); start with different major versions.",
            "hub" => "Decide whether it is a shared library (then version and publish it as one) or a service (then talk to it through an API, not its packages).",
            _ => null,
        };
}

/// <summary>A design issue in the list: its sign, subject and summary, and whether it is the one shown.</summary>
public sealed partial class IssueRow(DesignIssue issue) : ObservableObject
{
    public DesignIssue Issue { get; } = issue;
    public string Subject => Issue.Subject ?? Issue.Title;
    public string Summary => Issue.Summary ?? "";
    public string Icon => Findings.Icon(Issue.Level);
    public bool IsProblem => Issue.Level is "problem";
    public bool IsWarn => Issue.Level is "warn";

    [ObservableProperty]
    public partial bool Selected { get; set; }
}

/// <summary>A kind's issues in the list, its explanation once in its header.</summary>
public sealed record IssueGroup(string Label, string Why, IReadOnlyList<IssueRow> Rows)
{
    public string Count => Rows.Count.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A place an issue shows: the member, the file and line, a note.</summary>
public sealed record IssuePlace(IssueEvidence Evidence)
{
    public string Project => Evidence.Project;
    public string? Note => Evidence.Note is { Length: > 0 } n ? "· " + n : null;
    public string? Where => Evidence.Path is { Length: > 0 } p ? p + Line : null;
    private string Line => Evidence.Line > 0 ? ":" + Evidence.Line.ToString(CultureInfo.InvariantCulture) : "";
    public bool HasWhere => Where is not null;
}

/// <summary>
/// Design issues between the members (app.js issuesView): grouped by kind, a chip per kind to show or hide them, the
/// selected one's detail beside the list. Version drift is on its own view; the copies are shared code's findings.
/// </summary>
public sealed partial class DesignIssuesSection : SectionViewModel
{
    private readonly WorkspaceContext _context;
    private readonly string? _only;
    private IReadOnlyList<IssueRow> _all = [];

    public DesignIssuesSection(WorkspaceContext context, string? only)
        : base(only is null ? "Design issues" : "Packages at different versions", null, ["sync", "findings", "levels"])
    {
        _context = context;
        _only = only;
        Hint = only is null
            ? "Smells that only show across repositories, read from the members' manifests and code. Evidence, not a verdict."
            : "Each with the members using which version, and what to do.";
    }

    public ObservableCollection<FilterChip> Chips { get; } = [];
    public ObservableCollection<IssueGroup> Groups { get; } = [];
    public ObservableCollection<IssuePlace> Places { get; } = [];
    public ObservableCollection<string> Projects { get; } = [];

    [ObservableProperty]
    public partial IssueRow? Selection { get; private set; }

    [ObservableProperty]
    public partial string Counts { get; private set; } = "";

    public bool IsEmpty => _all.Count is 0;
    public bool HasChips => Chips.Count > 1;
    public string Okay => _only is null ? "✓ No design issues found between these projects." : "✓ Every package used by several members is at one version.";
    public bool HasSelection => Selection is not null;
    public string? KindLabel => Selection is { } s ? IssueKinds.Label(s.Issue.Kind) : null;
    public string? Todo => Selection is { } s ? IssueKinds.Todo(s.Issue.Kind) : null;
    public bool HasTodo => Todo is not null;
    public bool HasPlaces => Places.Count > 0;
    public bool HasProjects => Projects.Count > 0;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Choose(IssueRow row) => Pick(row);

    public void Pick(IssueRow row)
    {
        foreach (var r in _all)
        {
            r.Selected = r == row;
        }
        Selection = row;
        Lists.Sync(Places, row.Issue.Evidence.Select(e => new IssuePlace(e)));
        Lists.Sync(Projects, row.Issue.Projects.Select(_context.Folder));
        foreach (var name in new[] { nameof(HasSelection), nameof(KindLabel), nameof(Todo), nameof(HasTodo), nameof(HasPlaces), nameof(HasProjects) })
        {
            OnPropertyChanged(name);
        }
    }

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var issues = (await _context.Services.Api.IssuesAsync(_context.Name, token)).Issues
            .Where(x => x.Kind is not ("local-copy" or "duplicate") && (_only is null ? x.Kind is not "version-drift" : x.Kind == _only))
            .ToArray();
        _all = [.. issues.Select(x => new IssueRow(x))];
        var levels = new[] { ("problem", "problem"), ("warn", "warning"), ("info", "note") };
        Counts = string.Join(
            " · ",
            levels.Select(l => (Word: l.Item2, N: issues.Count(x => x.Level == l.Item1))).Where(x => x.N > 0).Select(x => Shell.Plural(x.N, x.Word))
        );
        Chips.Clear();
        foreach (var (kind, label, _) in IssueKinds.All.Where(k => issues.Any(x => x.Kind == k.Kind)))
        {
            Chips.Add(new FilterChip(kind, label, issues.Count(x => x.Kind == kind), _ => Draw()) { On = true });
        }
        Draw();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasChips));
    }

    private void Draw()
    {
        var hidden = Chips.Where(c => !c.On).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var shown = _all.Where(r => !hidden.Contains(r.Issue.Kind)).ToArray();
        Lists.Sync(
            Groups,
            IssueKinds.All.Select(k => new IssueGroup(k.Label, k.Why, [.. shown.Where(r => r.Issue.Kind == k.Kind)])).Where(g => g.Rows.Count > 0)
        );
        if (Selection is null || !shown.Contains(Selection))
        {
            if (shown.Length > 0)
            {
                Pick(shown[0]);
            }
            else
            {
                Selection = null;
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }
}

/// <summary>A name in several members (app.js sharedCodeView row): how alike, where, and where it could live once.</summary>
public sealed record DuplicateRow(DuplicateGroup Group, Func<string, string> Folder)
{
    public string Name => Group.Name;
    public string? Kind => Group.Kind;
    public string? Names => Group.Names is { Count: > 0 } n ? string.Join(", ", n) : null;
    public bool HasNames => Names is not null;
    public string Verdict => Group.Verdict;
    public string Alike => Group.Verdict is "identical" ? "" : Overview.Pct(Group.Similarity);
    public string Copies => Group.Copies.Count.ToString(CultureInfo.InvariantCulture);

    public IReadOnlyList<string> Where =>
        [.. Group.Copies.Select(c => $"{Folder(c.Project)}  {c.Path}{(c.Line > 0 ? ":" + c.Line.ToString(CultureInfo.InvariantCulture) : "")}{(c.Extends ? " · extends it" : "")}")];

    public string Home => Group.Home is { } h ? h.Member ?? "a new shared library" : "–";
    public string? HomeReason => Group.Home?.Reason;
    public bool HasHomeReason => HomeReason is not null;
    public IReadOnlyList<string> Knowledge => [.. (Group.Home?.Knowledge ?? []).Select(k => "↳ " + k)];
}

/// <summary>
/// Shared code (app.js sharedCodeView): the code repeated across members as findings with a suggested home each,
/// triaged like the others; then every name in several members, filtered by how alike and by name.
/// </summary>
public sealed partial class SharedCodeSection : SectionViewModel
{
    public static readonly IReadOnlyList<(string Verdict, string Label)> Verdicts =
    [
        ("identical", "Identical"),
        ("near copy", "Near copies"),
        ("similar", "Similar"),
        ("same name only", "Same name only"),
        ("extends", "Extends a shared one"),
    ];

    private readonly WorkspaceContext _context;
    private IReadOnlyList<DuplicateGroup> _groups = [];

    public SharedCodeSection(WorkspaceContext context)
        : base("Code repeated across members", null, ["sync", "findings"])
    {
        _context = context;
        Hint = "Code repeated in several members (identical, near copies, similar), each with where it could live once: a library that already holds a copy, else one they all use, else a new shared library.";
        Board = context.Board();
        Board.Okay = "✓ No code is copied between members (same names only, if anything: see below).";
    }

    public FindingsBoard Board { get; }
    public ObservableCollection<FilterChip> Chips { get; } = [];
    public ObservableCollection<DuplicateRow> Rows { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    public string AllHead => $"All code by name: {Shell.Plural(_groups.Count, "name")} in several projects";
    public bool HasRows => Rows.Count > 0;

    public string Empty => _groups.Count > 0 ? "Nothing matches the filter." : "No code appears in more than one project.";

    partial void OnQueryChanged(string value) => Draw();

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var d = await _context.Services.Api.DuplicatesAsync(_context.Name, token);
        _groups = d.Groups;
        Board.Show(d.Findings ?? [], d.Snapshot, d.Snapshot is not null || d.Findings is not null);
        if (Chips.Count is 0)
        {
            foreach (var (verdict, label) in Verdicts)
            {
                Chips.Add(new FilterChip(verdict, label, 0, _ => Draw()) { On = verdict is "identical" or "near copy" or "similar" });
            }
        }
        foreach (var chip in Chips)
        {
            chip.Count = _groups.Count(g => g.Verdict == chip.Key);
        }
        Lists.Sync(Notes, (d.Notes ?? []).Where(x => !x.StartsWith("similarity compares", StringComparison.Ordinal)));
        Draw();
        OnPropertyChanged(nameof(AllHead));
    }

    private void Draw()
    {
        var show = Chips.Where(c => c.On).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var q = Query.Trim();
        Lists.Sync(
            Rows,
            _groups.Where(g =>
                    show.Contains(g.Verdict)
                    && (q.Length is 0 || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || g.Copies.Any(c => c.Project.Contains(q, StringComparison.OrdinalIgnoreCase)))
                )
                .Take(150)
                .Select(g => new DuplicateRow(g, _context.Folder))
        );
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(Empty));
    }
}

/// <summary>A member's versions in a row of the versions table, with the package marks once they arrive.</summary>
public sealed partial class VersionCell(string text, bool behind) : ObservableObject
{
    public string Text { get; } = text;
    public bool Behind { get; } = behind;
    public bool Used => Text is not "—";

    [ObservableProperty]
    public partial string? Marks { get; set; }

    [ObservableProperty]
    public partial bool Severe { get; set; }

    public bool HasMarks => Marks is { Length: > 0 };

    partial void OnMarksChanged(string? value) => OnPropertyChanged(nameof(HasMarks));
}

/// <summary>A row of the versions table: a group's heading, or a package (several of one owner at one version merged).</summary>
public sealed record VersionRow(string? Group, string Name, string? More, string? Owner, IReadOnlyList<VersionCell> Cells, string Declared, bool Stale, bool Differ)
{
    public bool IsGroup => Group is not null;
    public bool IsRow => Group is null;
    public bool HasMore => More is not null;
    public bool HasOwner => Owner is not null;
    public IReadOnlyList<string> Names { get; init; } = [];
}

/// <summary>
/// Versions across the workspace (app.js versionsTable): what each member is built on, compared; a member on an older
/// version than another or than its owner declares is marked, a declaration older than what is used looks stale, and
/// the members' own packages get their NuGet marks (newer stable, known vulnerabilities) once they arrive.
/// </summary>
public sealed partial class VersionsSection : SectionViewModel
{
    private readonly WorkspaceContext _context;

    public VersionsSection(WorkspaceContext context)
        : base("Versions across the workspace", null, ["sync"])
    {
        _context = context;
        Hint = "What each member is built on, compared. Highlighted: a member on an older version than another, or than the one its owner declares now; a newer stable version on its package source, or a known vulnerability, under the version.";
    }

    public ObservableCollection<string> Columns { get; } = [];
    public ObservableCollection<VersionRow> Rows { get; } = [];
    public bool IsEmpty => Rows.Count is 0;

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var d = await _context.Services.Api.WorkspaceStackAsync(_context.Name, token);
        var ids = _context.Workspace.Members.Select(m => m.ProjectId)
            .Where(id => d.Stacks.ContainsKey(id) || d.Rows.Any(r => r.Versions.ContainsKey(id)))
            .OrderBy(_context.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Lists.Sync(Columns, ids.Select(_context.Label));
        // one owner's packages used at the same versions are one row
        var merged = new List<(StackVersionRow Row, List<string> Names, string? Key)>();
        foreach (var r in d.Rows)
        {
            var key = r.Kind is "core"
                ? r.Owner + "\0" + string.Join(";", r.Versions.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + "=" + string.Join(",", v.Value))) + "\0" + r.Published
                : null;
            var prev = key is null ? default : merged.FirstOrDefault(x => x.Key == key);
            if (prev.Names is not null)
            {
                prev.Names.Add(r.Name);
                continue;
            }
            merged.Add((r, [r.Name], key));
        }
        var rows = new List<VersionRow>();
        var last = "";
        var marks = new List<(string Id, VersionCell Cell, IReadOnlyList<string> Names, IReadOnlyList<string> Versions)>();
        foreach (var (r, names, _) in merged)
        {
            if (r.Kind != last)
            {
                last = r.Kind;
                var group = r.Kind switch
                {
                    "runtime" => "Runtime",
                    "framework" => "Frameworks",
                    _ => "The members' own packages",
                };
                rows.Add(new VersionRow(group, "", null, null, [], "", false, false));
            }
            var all = r.Versions.Values.SelectMany(v => v).ToArray();
            var top = all.Order(Comparer<string>.Create(Versions.Compare)).LastOrDefault();
            var cells = new List<VersionCell>();
            foreach (var id in ids)
            {
                if (!r.Versions.TryGetValue(id, out var vs))
                {
                    cells.Add(new VersionCell("—", false));
                    continue;
                }
                var against = r.Published ?? top;
                var cell = new VersionCell(string.Join(", ", vs), against is not null && vs.Any(v => Versions.Compare(v, against) < 0));
                cells.Add(cell);
                if (r.Kind is "core")
                {
                    marks.Add((id, cell, names, vs));
                }
            }
            var stale = r.Published is { } pub && all.Any(v => Versions.Compare(v, pub) > 0);
            rows.Add(
                new VersionRow(
                    null,
                    names[0],
                    names.Count > 1 ? $"and {Shell.Plural(names.Count - 1, "more package")}" : null,
                    r.Owner is { } o ? "from " + _context.Label(o) : null,
                    cells,
                    r.Published ?? (r.Kind is "core" ? "—" : ""),
                    stale,
                    r.Differ
                )
                {
                    Names = names,
                }
            );
        }
        Lists.Sync(Rows, rows);
        OnPropertyChanged(nameof(IsEmpty));
        _ = MarksAsync(marks);
    }

    /// <summary>Each member's marks from its own package sources, asked once the table is drawn.</summary>
    private async Task MarksAsync(IReadOnlyList<(string Id, VersionCell Cell, IReadOnlyList<string> Names, IReadOnlyList<string> Versions)> cells)
    {
        foreach (var member in cells.GroupBy(c => c.Id))
        {
            var wanted = member.SelectMany(c => c.Names.SelectMany(n => c.Versions.Select(v => $"{n}@{v}"))).Distinct(StringComparer.Ordinal).ToArray();
            PackageMarks marks;
            try
            {
                marks = await _context.Services.Api.PackageMarksAsync(member.Key, wanted, CancellationToken.None);
            }
            catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
            {
                continue;
            }
            foreach (var (_, cell, names, versions) in member)
            {
                var found = names.SelectMany(n => versions.Select(v => (Name: n, Version: v, Mark: Mark(marks, n, v)))).Where(x => x.Mark is not null).ToArray();
                var lines = new List<string>();
                foreach (var (name, version, mark) in found)
                {
                    var who = names.Count > 1 ? name + ": " : "";
                    if (mark!.Latest is { Length: > 0 } latest)
                    {
                        lines.Add($"{who}ⓘ newer stable {latest} (you use {version})");
                    }
                    lines.AddRange(mark.Vulnerabilities.Select(v => $"{who}⚠ {v.Severity} vulnerability{(v.Fixed is { Length: > 0 } f ? $", fixed in {f}" : "")}"));
                }
                cell.Severe = found.Any(x => x.Mark!.Vulnerabilities.Any(v => v.Severity is "high" or "critical"));
                cell.Marks = lines.Count > 0 ? string.Join("\n", lines) : null;
            }
        }
    }

    private static PackageMark? Mark(PackageMarks marks, string name, string version)
    {
        var plain = version.Trim().TrimStart('[').TrimEnd(']');
        var key = $"{name}@{plain}";
        return marks.Packages.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }
}

/// <summary>
/// A workspace's Issues (app.js workspaceView "issues"): Findings (the rollup, the design issues, the code repeated
/// across members) or Version drift (the drifting packages and the versions table), by the address.
/// </summary>
public sealed partial class WorkspaceIssuesViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceContext _context;
    private bool _versionsStarted;
    private bool _findingsStarted;

    public WorkspaceIssuesViewModel(WorkspaceContext context, Route route)
    {
        _context = context;
        Rollup = new RollupSection(context) { Hint = "Open architecture findings per member and between them, as each one's last sync recorded them." };
        Issues = new DesignIssuesSection(context, null);
        Shared = new SharedCodeSection(context);
        Drift = new DesignIssuesSection(context, "version-drift");
        Versions = new VersionsSection(context);
        Show(route);
    }

    public RollupSection Rollup { get; }
    public DesignIssuesSection Issues { get; }
    public SharedCodeSection Shared { get; }
    public DesignIssuesSection Drift { get; }
    public VersionsSection Versions { get; }
    public ObservableCollection<TabItem> Sections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFindings), nameof(ShowsVersions))]
    public partial string Section { get; private set; } = "findings";

    public bool ShowsFindings => Section is "findings";
    public bool ShowsVersions => Section is "versions";

    /// <summary>The address asked for shared code (an old link): Findings, scrolled to it.</summary>
    [ObservableProperty]
    public partial bool ToShared { get; private set; }

    public void Show(Route route)
    {
        Section = route.Item is "versions" ? "versions" : "findings";
        ToShared = route.Item is "shared";
        Lists.Sync(
            Sections,
            new[] { ("findings", "Findings"), ("versions", "Version drift") }.Select(s =>
                new TabItem(s.Item1, s.Item2, Route.WorkspaceHref(_context.Name, "issues", s.Item1 is "findings" ? null : s.Item1), 0, s.Item1 == Section)
            )
        );
        Start();
    }

    private void Start()
    {
        var live = _context.Services.Live;
        if (ShowsFindings && !_findingsStarted)
        {
            _findingsStarted = true;
            foreach (var section in new SectionViewModel[] { Rollup, Issues, Shared })
            {
                section.Follow(live);
                _ = section.LoadAsync();
            }
        }
        if (ShowsVersions && !_versionsStarted)
        {
            _versionsStarted = true;
            foreach (var section in new SectionViewModel[] { Drift, Versions })
            {
                section.Follow(live);
                _ = section.LoadAsync();
            }
        }
    }

    public void Dispose()
    {
        Rollup.Dispose();
        Issues.Dispose();
        Shared.Dispose();
        Drift.Dispose();
        Versions.Dispose();
    }
}
