using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
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

/// <summary>Where findings' triage goes: a project's findings/{action}, or a workspace's.</summary>
public delegate Task FindingPost(string action, FindingAction body, CancellationToken token);

/// <summary>A member a finding's task can belong to (a workspace's findings).</summary>
public sealed record MemberChoice(string Id, string Name);

/// <summary>
/// Findings with their triage (app.js smellsSection): grouped by kind in the server's order, the ones marked by design
/// hidden unless shown, the gone ones folded, the line on what changed at the last sync, and each finding's actions
/// (mark by design, make a task, unmark, change or retire the knowledge it is about). Used by a project's
/// Architecture and a workspace's Runtime and Shared code.
/// </summary>
public sealed partial class FindingsBoard(
    UiServices services,
    FindingPost post,
    KnowledgeActions knowledge,
    Func<IReadOnlyList<MemberChoice>>? members = null
) : ObservableObject
{
    private IReadOnlyList<Finding> _list = [];

    public ObservableCollection<FindingGroup> Groups { get; } = [];
    public ObservableCollection<Finding> Gone { get; } = [];

    [ObservableProperty]
    public partial bool ShowMarked { get; set; }

    [ObservableProperty]
    public partial int MarkedCount { get; private set; }

    [ObservableProperty]
    public partial string? SnapshotLine { get; private set; }

    [ObservableProperty]
    public partial string Okay { get; set; } = "";

    public bool HasGone => Gone.Count > 0;
    public string GoneHead => $"Gone ({Gone.Count})";
    public bool HasMarked => MarkedCount > 0;
    public string MarkedLabel => $"show {MarkedCount} marked by design";
    public bool HasFindings => Groups.Count > 0;
    public bool HasSnapshot => SnapshotLine is { Length: > 0 };

    partial void OnShowMarkedChanged(bool value) => Draw();

    /// <summary>
    /// Shows <paramref name="list"/>; <paramref name="recorded"/> when it came with history (after a sync), so the
    /// snapshot line says what changed or that nothing was synced yet.
    /// </summary>
    public void Show(IReadOnlyList<Finding> list, FindingSnapshot? snapshot, bool recorded)
    {
        _list = list;
        SnapshotLine = recorded ? SnapshotText(snapshot) : null;
        Draw();
    }

    private static string SnapshotText(FindingSnapshot? snapshot) =>
        snapshot is { } snap
            ? $"Since the last change ({Shell.Ago(snap.TakenAt)}): {snap.Added} new, {snap.Changed} changed, {snap.Gone} gone"
            : "Not synced yet: harness sync records them, so they show as new, changed or gone.";

    private void Draw()
    {
        var active = _list.Where(x => x.Status is not "gone").ToArray();
        MarkedCount = active.Count(x => x.Mark?.State is "by_design");
        var shown = active.Where(x => ShowMarked || x.Mark?.State is not "by_design").ToArray();
        // kinds in the order their first finding comes (the server's: most worth acting on first)
        Groups.Clear();
        foreach (var kind in shown.Select(x => x.Kind).Distinct(StringComparer.Ordinal))
        {
            Groups.Add(new FindingGroup(kind, [.. shown.Where(x => x.Kind == kind)]));
        }
        Lists.Sync(Gone, _list.Where(x => x.Status is "gone"));
        foreach (var name in new[] { nameof(HasGone), nameof(GoneHead), nameof(HasMarked), nameof(MarkedLabel), nameof(HasFindings), nameof(HasSnapshot) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>A finding marked by design, with the reason recorded as a decision.</summary>
    [RelayCommand]
    private Task<bool> MarkAsync(FindingRow row) =>
        services.Dialogs.ShowAsync(
            new FindingMarkDialog(services, post, row.Finding)
            {
                Title = row.Finding.Mark is null ? "Mark by design" : "Still by design?",
                Sub = row.Finding.Title,
                Wide = true,
                AcceptLabel = "Mark by design",
            }
        );

    /// <summary>An ARCH task with the finding's why and evidence: in this project, or in the member chosen.</summary>
    [RelayCommand]
    private async Task TaskAsync(FindingRow row)
    {
        if (members?.Invoke() is { Count: > 0 } choices)
        {
            await services.Dialogs.ShowAsync(
                new FindingTaskDialog(services, post, row.Finding, choices)
                {
                    Title = "Make it a task?",
                    Sub = row.Finding.Title,
                    AcceptLabel = "Make a task",
                }
            );
            return;
        }
        var confirm = new ConfirmDialog
        {
            Title = "Make it a task?",
            Sub = row.Finding.Title,
            Message = "An ARCH task in this project with the finding's why, what could help and its evidence; the finding links to it and shows resolved once it is gone. It lives in harness, not in a backlog file.",
            AcceptLabel = "Make a task",
        };
        if (await services.Dialogs.ConfirmAsync(confirm))
        {
            await ActAsync("task", new FindingAction { Key = row.Finding.Key!, Project = "" }, "Task made");
        }
    }

    [RelayCommand]
    private Task UnmarkAsync(FindingRow row) => ActAsync("unmark", new FindingAction { Key = row.Finding.Key! }, "Mark taken back");

    /// <summary>The knowledge a finding is about, corrected instead of the code.</summary>
    [RelayCommand]
    private Task ChangeKnowledgeAsync(FindingRow row) => KnowledgeAsync(row, correct: true);

    [RelayCommand]
    private Task RetireKnowledgeAsync(FindingRow row) => KnowledgeAsync(row, correct: false);

    private async Task KnowledgeAsync(FindingRow row, bool correct)
    {
        var rule = row.Finding.Rule!;
        var level = rule.Scope is { } s && MemoryTypes.IsLevel(s) ? s : null;
        try
        {
            var memory = (await services.Api.MemoryAsync(knowledge.ProjectId, rule.Memory!, level, CancellationToken.None)).Memory;
            await (correct ? knowledge.CorrectAsync(memory) : knowledge.RetireAsync(memory));
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            services.Toasts.Fail(exception.Message);
        }
    }

    private async Task ActAsync(string action, FindingAction body, string done)
    {
        try
        {
            await post(action, body, CancellationToken.None);
            services.Toasts.Show(done);
            services.Live.RefreshAll();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            services.Toasts.Fail(exception.Message);
        }
    }
}

/// <summary>Marking a finding by design: why it is intended (recorded as a decision for agents working there).</summary>
public sealed partial class FindingMarkDialog(UiServices services, FindingPost post, Finding finding) : FormDialog<bool>
{
    [ObservableProperty]
    public partial string Reason { get; set; } = finding.Mark?.Reason ?? "";

    public static string Note =>
        "It stays listed, marked, and hides unless marked ones are shown. The reason is recorded as a decision for agents working there; it comes back for review if it grows.";

    protected override string? Validate() => Reason.Trim().Length is 0 ? "Why it is intended is required." : null;

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        await post("mark", new FindingAction { Key = finding.Key!, Reason = Reason.Trim() }, token);
        services.Toasts.Show("Marked by design");
        services.Live.RefreshAll();
        return true;
    }
}

/// <summary>A workspace finding made a task in one member (app.js smellsSection with members): the member it belongs to.</summary>
public sealed partial class FindingTaskDialog : FormDialog<bool>
{
    private readonly UiServices _services;
    private readonly FindingPost _post;
    private readonly Finding _finding;

    public FindingTaskDialog(UiServices services, FindingPost post, Finding finding, IReadOnlyList<MemberChoice> members)
    {
        _services = services;
        _post = post;
        _finding = finding;
        Members = members;
        Member = members.FirstOrDefault(m => m.Id == finding.Project) ?? members[0];
    }

    public IReadOnlyList<MemberChoice> Members { get; }

    [ObservableProperty]
    public partial MemberChoice Member { get; set; }

    public static string Note =>
        "An ARCH task in that member with the finding's why, what could help and its evidence; the finding links to it and shows resolved once it is gone.";

    protected override string? Validate() => null;

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        await _post("task", new FindingAction { Key = _finding.Key!, Project = Member.Id }, token);
        _services.Toasts.Show($"Task made in {Member.Name}");
        _services.Live.RefreshAll();
        return true;
    }
}
