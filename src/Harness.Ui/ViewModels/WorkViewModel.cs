using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Controls;
using Harness.Ui.Navigation;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>The task states and how the page names and draws them (app.js TASK_STATUSES, STATUS_ICON).</summary>
public static class TaskStates
{
    public static readonly IReadOnlyList<string> All = ["ready", "claimed", "in_progress", "review", "blocked", "backlog", "done", "deferred"];

    /// <summary>What the Tasks list shows at first: what can be worked on or needs a person.</summary>
    public static readonly IReadOnlyList<string> Open = ["ready", "claimed", "in_progress", "review", "blocked"];

    public static readonly IReadOnlyList<string> Types = ["feature", "bug", "chore", "spike", "docs", "test", "process"];
    public static readonly IReadOnlyList<string> Priorities = ["low", "medium", "high", "critical"];

    /// <summary>The 16 blueprint colours, in the order harness gives them out.</summary>
    public static readonly IReadOnlyList<string> Colors =
    [
        "teal", "orange", "blue", "rose", "green", "violet", "amber", "cyan", "pink", "lime", "indigo", "red", "fuchsia", "yellow", "slate", "purple",
    ];

    public static string Words(string status) => status.Replace('_', ' ');

    public static string Icon(string status) => "status-" + status;

    public static string TypeIcon(string? type) => Types.Contains(type ?? "feature") ? type ?? "feature" : "type-other";

    public static bool Editable(TaskSummary task) => task.Status is "ready" or "backlog";

    /// <summary>How long a claim still holds and how urgent that is (app.js lease()): "" fine, warn, bad.</summary>
    public static (string Text, string Level) Lease(TaskClaim? claim, DateTimeOffset? now = null)
    {
        if (
            claim?.LeaseUntil is not { } until
            || !DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end)
        )
        {
            return ("", "");
        }
        var left = (end - (now ?? DateTimeOffset.UtcNow)).TotalMinutes;
        if (left <= 0)
        {
            return ("lease expired", "bad");
        }
        var text = left < 90
            ? $"{Math.Round(left, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)} min left"
            : $"{Math.Round(left / 60, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)} h left";
        return (text, left < 60 ? "warn" : "");
    }
}

/// <summary>A chip that filters (a status, a blueprint state, a proposal status): its words, count and whether it is on.</summary>
public sealed partial class FilterChip(string key, string label, int count, Action<FilterChip> changed) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial int Count { get; set; } = count;

    [ObservableProperty]
    public partial bool On { get; set; }

    public string StatusIcon => TaskStates.Icon(Key);
    public string StatusClass => "st-" + Key;
    public string AccessibleName => $"{Label} ({Count})";

    partial void OnOnChanged(bool value) => changed(this);
}

/// <summary>A part of the task bar's legend: its words, count and the shape of its status.</summary>
public sealed record BarPart(string Label, int Count, string Brush, string Icon)
{
    public string Text => $"{Count} {Label}";
}

/// <summary>Something stuck (app.js attention()): blocked, a lease ending, done with failed criteria.</summary>
public sealed record StuckItem(string Level, string Id, string Title, string Why, string Href)
{
    public bool IsProblem => Level is "bad";
    public string Icon => IsProblem ? "st-fail" : "st-warn";
}

/// <summary>A task row in Work › Tasks (app.js taskRow()).</summary>
public sealed record TaskRow(
    string Id,
    string Title,
    string Type,
    string Priority,
    string Status,
    string? Claim,
    string ClaimLevel,
    string Acceptance,
    bool Editable,
    string Href
)
{
    public string TypeIcon => TaskStates.TypeIcon(Type);
    public string PriorityIcon => "p-" + Priority;
    public string PriorityClass => "p-" + Priority;
    public string StatusIcon => TaskStates.Icon(Status);
    public string StatusClass => "st-" + Status;
    public string StatusWords => TaskStates.Words(Status);
    public bool HasClaim => Claim is { Length: > 0 };
    public bool ClaimBad => ClaimLevel is "bad";
    public bool ClaimWarn => ClaimLevel is "warn";
    public string EditTip => Editable ? $"Edit {Id}" : "Only a ready or backlog task can be edited: once claimed, its definition is fixed";
    public string AccessibleName => $"{Id} {Title}, {StatusWords}, {Priority} priority";
}

/// <summary>Deferred tasks of a blueprint set aside for one reason, as one line.</summary>
public sealed record DeferredLine(string Reason, int Count)
{
    public string CountText => Shell.Plural(Count, "task");
}

/// <summary>A blueprint's group in Work › Tasks: its header with progress, its shown rows and its deferred lines.</summary>
public sealed partial class TaskGroup(string id) : ObservableObject
{
    public string Id { get; } = id;
    public bool HasBlueprint => Id is not "—";

    [ObservableProperty]
    public partial string? Title { get; set; }

    [ObservableProperty]
    public partial string? Color { get; set; }

    [ObservableProperty]
    public partial string CountText { get; set; } = "";

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string? Goal { get; set; }

    [ObservableProperty]
    public partial bool Open { get; set; } = true;

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    [ObservableProperty]
    public partial bool Movable { get; set; }

    public ObservableCollection<TaskRow> Rows { get; } = [];
    public ObservableCollection<DeferredLine> Deferred { get; } = [];

    public string FrameClass => Color is { Length: > 0 } c ? "bp-" + c : "";
    public string Name
    {
        get
        {
            if (!HasBlueprint)
            {
                return "No blueprint";
            }
            return Title is { Length: > 0 } t ? $"{Id} · {t}" : Id;
        }
    }
    public bool HasGoal => Open && Goal is { Length: > 0 };
    public string AccessibleName => $"{Name}: {CountText}";

    partial void OnOpenChanged(bool value) => OnPropertyChanged(nameof(HasGoal));

    partial void OnColorChanged(string? value) => OnPropertyChanged(nameof(FrameClass));

    partial void OnTitleChanged(string? value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AccessibleName));
    }

    partial void OnGoalChanged(string? value) => OnPropertyChanged(nameof(HasGoal));
}

/// <summary>A count of a blueprint's tasks in one state, on its card.</summary>
public sealed record StateCount(string Status, int Count)
{
    public string Icon => TaskStates.Icon(Status);
    public string Class => "st-" + Status;
    public string Text => $"{TaskStates.Words(Status)} {Count}";
}

/// <summary>A blueprint's card in Work › Blueprints (app.js drawMilestones()).</summary>
public sealed partial class BlueprintCard(BlueprintInfo info) : ObservableObject
{
    public BlueprintInfo Info { get; } = info;
    public string Id => Info.Id;
    public bool HasBlueprint => Id is not "—";

    public string Heading
    {
        get
        {
            if (Info.Title is { Length: > 0 } title)
            {
                return title;
            }
            return HasBlueprint ? "Not described in the backlog" : "Without a blueprint";
        }
    }

    public bool Described => Info.Title is { Length: > 0 };
    public string FrameClass => Info.Color is { Length: > 0 } c ? "bp-" + c : "";

    /// <summary>none, done, deferred, active or todo, with its words (app.js stateOf()).</summary>
    public (string Kind, string Label) State
    {
        get
        {
            int N(string s) => Info.Tasks.GetValueOrDefault(s);
            var total = Info.Total;
            if (total is 0)
            {
                return ("none", "no tasks");
            }
            if (N("done") == total)
            {
                return ("done", "done");
            }
            if (N("done") + N("deferred") == total)
            {
                return N("done") > 0 ? ("done", "done") : ("deferred", "deferred");
            }
            return N("done") + N("claimed") + N("in_progress") + N("review") > 0 ? ("active", "in progress") : ("todo", "not started");
        }
    }

    /// <summary>open, done or deferred: what the filter chips sort cards by.</summary>
    public string Filter => State.Kind is "done" or "deferred" ? State.Kind : "open";

    public string StateLabel => State.Label;

    public string StateIcon =>
        State.Kind switch
        {
            "done" => "status-done",
            "deferred" => "status-deferred",
            "active" => "status-in_progress",
            _ => "status-backlog",
        };

    public string StateClass =>
        State.Kind switch
        {
            "done" => "st-done",
            "active" => "st-in_progress",
            _ => "st-backlog",
        };

    public int Done => Info.Tasks.GetValueOrDefault("done");
    public double Progress => Info.Total > 0 ? Done / (double)Info.Total : 0;
    public string ProgressText => $"{Done} of {Info.Total} tasks done";
    public bool HasTasks => Info.Total > 0;
    public string ShowTasks => $"Show its {Shell.Plural(Info.Total, "task")} →";
    public bool HasGoal => Info.Goal is { Length: > 0 };

    public IReadOnlyList<StateCount> Counts =>
        [.. new[] { "ready", "in_progress", "review", "blocked", "backlog", "deferred" }.Where(s => Info.Tasks.GetValueOrDefault(s) > 0).Select(s => new StateCount(s, Info.Tasks[s]))];

    public bool HasExit => Info.Exit.Count > 0;
    public string ExitHead => $"Done when · {Info.Exit.Count}";

    /// <summary>The notes as a list when every line is a "- " entry, else null (shown as text).</summary>
    public IReadOnlyList<string>? NoteItems
    {
        get
        {
            var lines = (Info.Notes ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            return lines.Length > 1 && lines.All(l => l.Length > 2 && "-*•".Contains(l[0], StringComparison.Ordinal) && l[1] is ' ')
                ? [.. lines.Select(l => l[2..].Trim())]
                : null;
        }
    }

    public bool HasNotes => Info.Notes is { Length: > 0 };
    public bool NotesAsList => NoteItems is not null;
    public bool NotesAsText => HasNotes && NoteItems is null;

    [ObservableProperty]
    public partial bool ShowAllExit { get; set; }

    public IReadOnlyList<string> ExitShown => ShowAllExit ? Info.Exit : [.. Info.Exit.Take(3)];
    public bool ExitMore => Info.Exit.Count > 3;
    public string ExitToggle => ShowAllExit ? "Show less" : $"Show all {Info.Exit.Count}";

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    [RelayCommand]
    private void ToggleExit()
    {
        ShowAllExit = !ShowAllExit;
        OnPropertyChanged(nameof(ExitShown));
        OnPropertyChanged(nameof(ExitToggle));
    }
}

/// <summary>
/// A project's Work (app.js "work": viewTasks, viewArchProposals): Blueprints, Tasks and the Inbox, one at a time by
/// the address (#/p/&lt;id&gt;/work/&lt;section&gt;; a task's id opens Tasks with the task). The plan (tasks and
/// blueprints) and the Inbox load on their own.
/// </summary>
public sealed partial class WorkViewModel : ObservableObject, IDisposable
{
    private readonly UiServices _services;
    private readonly string _id;
    private ProjectInfo _project;

    public WorkViewModel(UiServices services, ProjectInfo project, Route route)
    {
        _services = services;
        _id = project.Id;
        _project = project;
        Plan = new WorkPlan(services, project.Id);
        Inbox = new InboxViewModel(services, project.Id);
        Show(route);
    }

    public WorkPlan Plan { get; }
    public InboxViewModel Inbox { get; }
    public ObservableCollection<TabItem> Sections { get; } = [];

    /// <summary>blueprints, tasks or inbox.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPlan), nameof(ShowsBlueprints), nameof(ShowsTasks), nameof(ShowsInbox))]
    public partial string Section { get; private set; } = "blueprints";

    public bool ShowsPlan => Section is not "inbox";
    public bool ShowsBlueprints => Section is "blueprints";
    public bool ShowsTasks => Section is "tasks";
    public bool ShowsInbox => Section is "inbox";

    public void Start()
    {
        Plan.Follow(_services.Live);
        Inbox.Follow(_services.Live);
        _ = Plan.LoadAsync();
        _ = Inbox.LoadAsync();
    }

    /// <summary>The address's section (and task); the plan and Inbox stay loaded.</summary>
    public void Show(Route route)
    {
        var item = route.Item ?? "";
        var remembered = _services.Host.Remembered.GetValueOrDefault("work.view", "blueprints");
        Section = item switch
        {
            "milestones" => "blueprints",
            "blueprints" or "tasks" or "inbox" => item,
            "" => remembered,
            _ => "tasks",
        };
        if (Section is not "inbox")
        {
            _services.Host.Remembered["work.view"] = Section;
        }
        Plan.Link(Section is "tasks" && item is not ("" or "tasks") ? item : null);
        Sections.Clear();
        foreach (
            var (key, label, badge) in new[]
            {
                ("blueprints", "Blueprints", 0),
                ("tasks", "Tasks", 0),
                ("inbox", "Inbox", _project.Live.WorkProposals),
            }
        )
        {
            Sections.Add(new TabItem(key, label, Route.ProjectHref(_id, "work", key), badge, key == Section));
        }
    }

    public void Update(ProjectInfo project, Route route)
    {
        _project = project;
        Show(route);
    }

    public void Dispose()
    {
        Plan.Dispose();
        Inbox.Dispose();
    }
}

/// <summary>
/// The plan of Work: the tasks and blueprints, read together (tasks, blueprints), shown as Blueprints cards or as
/// Tasks grouped by blueprint, with the summary, what is stuck, and every write on them.
/// </summary>
public sealed partial class WorkPlan : SectionViewModel
{
    private readonly UiServices _services;
    private readonly HashSet<string> _statuses = [.. TaskStates.Open];
    private readonly HashSet<string> _collapsed = [];
    private IReadOnlyList<TaskSummary> _tasks = [];
    private IReadOnlyList<BlueprintInfo> _blueprints = [];
    private List<string> _order = [];
    private string? _cardFilter;
    private bool _drawing;

    public WorkPlan(UiServices services, string projectId)
        : base("", projectId, ["tasks"])
    {
        _services = services;
        StatusChips = [.. TaskStates.All.Select(s => new FilterChip(s, TaskStates.Words(s), 0, StatusToggled) { On = _statuses.Contains(s) })];
        CardChips = [.. new[] { ("open", "Open"), ("done", "Done"), ("deferred", "Deferred"), ("all", "All") }.Select(c => new FilterChip(c.Item1, c.Item2, 0, CardFilterChosen))];
    }

    /// <summary>The task the address names (#/…/work/TASK-1): shown whatever the filters, its dialog opened.</summary>
    public string? Linked { get; set; }

    public IReadOnlyList<FilterChip> StatusChips { get; }
    public IReadOnlyList<FilterChip> CardChips { get; }

    /// <summary>Opens the task the address names once the plan is loaded (now, when it is).</summary>
    public void Link(string? id)
    {
        Linked = id;
        if (id is not null && HasContent && _tasks.Any(t => t.Id == id))
        {
            Linked = null;
            _ = OpenTaskAsync(id);
        }
    }
    public ObservableCollection<BarPart> Parts { get; } = [];
    public ObservableCollection<StuckItem> Stuck { get; } = [];
    public ObservableCollection<BlueprintCard> Cards { get; } = [];
    public ObservableCollection<TaskGroup> Groups { get; } = [];
    public ObservableCollection<Choice> BlueprintChoices { get; } = [];

    /// <summary>The picker's choice for <see cref="BlueprintFilter"/>.</summary>
    [ObservableProperty]
    public partial Choice? SelectedBlueprint { get; set; }

    partial void OnSelectedBlueprintChanged(Choice? value)
    {
        if (value is not null && value.Value != BlueprintFilter)
        {
            BlueprintFilter = value.Value;
        }
    }

    [ObservableProperty]
    public partial IReadOnlyList<BarSegment> Segments { get; private set; } = [];

    [ObservableProperty]
    public partial string DoneText { get; private set; } = "";

    [ObservableProperty]
    public partial string DonePct { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasTasks { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStuck), nameof(StuckNote), nameof(StuckIcon))]
    public partial string StuckWorst { get; private set; } = "";

    public bool HasStuck => Stuck.Count > 0;
    public string StuckCount => Stuck.Count.ToString(CultureInfo.InvariantCulture);
    public string StuckNote
    {
        get
        {
            if (!HasStuck)
            {
                return "nothing blocked or stale";
            }
            return Stuck.Count is 1 ? "task stuck" : "tasks stuck";
        }
    }

    public string StuckIcon
    {
        get
        {
            if (!HasStuck)
            {
                return "st-ok";
            }
            return StuckWorst is "bad" ? "st-fail" : "st-warn";
        }
    }

    /// <summary>The blueprint the Tasks list is narrowed to ("" all, "—" none).</summary>
    [ObservableProperty]
    public partial string BlueprintFilter { get; set; } = "";

    [ObservableProperty]
    public partial string? TasksEmpty { get; private set; }

    [ObservableProperty]
    public partial bool OffersDone { get; private set; }

    [ObservableProperty]
    public partial string? CardsEmpty { get; private set; }

    /// <summary>The blueprints with nothing shown, offered as targets to move a task to.</summary>
    public ObservableCollection<TaskGroup> Targets { get; } = [];

    public IReadOnlyList<TaskSummary> AllTasks => _tasks;
    public IReadOnlyList<BlueprintInfo> AllBlueprints => _blueprints;

    partial void OnBlueprintFilterChanged(string value)
    {
        SelectedBlueprint = BlueprintChoices.FirstOrDefault(c => c.Value == value);
        if (!_drawing)
        {
            DrawTasks();
        }
    }

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var tasks = _services.Api.TasksAsync(ProjectId!, token);
        BlueprintList? blueprints;
        try
        {
            blueprints = await _services.Api.BlueprintsAsync(ProjectId!, token);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            blueprints = null;
        }
        _tasks = (await tasks).Tasks;
        _blueprints = blueprints?.Blueprints ?? [];
        // the blueprints in the author's order, then those only tasks name, then "—" for tasks without one
        _order = [.. _blueprints.Select(b => b.Id).Concat(_tasks.Select(t => t.Blueprint ?? "—")).Distinct(StringComparer.Ordinal)];
        Draw();
        if (Linked is { } linked && _tasks.Any(t => t.Id == linked))
        {
            _collapsed.Remove(_tasks.First(t => t.Id == linked).Blueprint ?? "—");
            DrawTasks();
            var id = linked;
            Linked = null;
            _ = OpenTaskAsync(id);
        }
    }

    private void Draw()
    {
        var all = _tasks;
        var done = all.Count(t => t.Status is "done");
        HasTasks = all.Count > 0;
        DoneText = $"{done} of {all.Count} tasks done";
        DonePct = all.Count > 0 ? Overview.Pct(done / (double)all.Count) : "";
        (string Key, string Label, string[] Statuses, string Brush, string Icon)[] parts =
        [
            ("done", "done", ["done"], "Good", "status-done"),
            ("flight", "in flight", ["claimed", "in_progress", "review"], "Accent", "status-in_progress"),
            ("ready", "ready", ["ready"], "AccentWash", "status-ready"),
            ("blocked", "blocked", ["blocked"], "Critical", "status-blocked"),
            ("backlog", "backlog", ["backlog"], "Axis", "status-backlog"),
            ("deferred", "deferred", ["deferred"], "Grid", "status-deferred"),
        ];
        var counted = parts.Select(p => (p, n: all.Count(t => p.Statuses.Contains(t.Status)))).Where(x => x.n > 0).ToArray();
        Segments = [.. counted.Select(x => new BarSegment(x.p.Brush, x.n))];
        Lists.Sync(Parts, counted.Select(x => new BarPart(x.p.Label, x.n, x.p.Brush, x.p.Icon)));
        DrawStuck();
        _drawing = true;
        Lists.Sync(
            BlueprintChoices,
            new[] { new Choice("", "all blueprints") }.Concat(
                _order.Select(m =>
                    new Choice(m, _blueprints.FirstOrDefault(b => b.Id == m)?.Title is { Length: > 0 } title ? $"{m} · {title}" : m)
                )
            )
        );
        if (BlueprintChoices.All(c => c.Value != BlueprintFilter))
        {
            BlueprintFilter = "";
        }
        SelectedBlueprint = BlueprintChoices.FirstOrDefault(c => c.Value == BlueprintFilter);
        _drawing = false;
        foreach (var chip in StatusChips)
        {
            chip.Count = all.Count(t => t.Status == chip.Key);
        }
        DrawCards();
        DrawTasks();
    }

    /// <summary>Blocked tasks, leases ending or ended, done tasks with failed criteria or checks (app.js attention()).</summary>
    private void DrawStuck()
    {
        var items = new List<StuckItem>();
        foreach (var t in _tasks)
        {
            var href = Route.ProjectHref(ProjectId!, "work", t.Id);
            if (t.Status is "blocked")
            {
                items.Add(new("warn", t.Id, t.Title, "blocked: " + (t.BlockedReason is { Length: > 0 } r ? r : "no reason given"), href));
            }
            var (left, level) = TaskStates.Lease(t.Claim);
            if (t.Claim is { } claim && level.Length > 0)
            {
                items.Add(new(level, t.Id, t.Title, $"{TaskStates.Words(t.Status)} by {claim.Claimant}: {left}", href));
            }
            if (t.Status is "done")
            {
                var failed = t.Acceptance.Count(c => c.Result is "fail") + (t.Completion?.Checks.Count(c => c.Result is "fail") ?? 0);
                if (failed > 0)
                {
                    items.Add(new("warn", t.Id, t.Title, $"done with {failed} failed {(failed is 1 ? "criterion or check" : "criteria or checks")}", href));
                }
            }
        }
        Lists.Sync(Stuck, items.OrderBy(i => i.Level is "bad" ? 0 : 1));
        StuckWorst = Stuck.Count > 0 ? Stuck[0].Level : "";
        OnPropertyChanged(nameof(HasStuck));
        OnPropertyChanged(nameof(StuckCount));
        OnPropertyChanged(nameof(StuckNote));
        OnPropertyChanged(nameof(StuckIcon));
    }

    private BlueprintInfo InfoOf(string id) =>
        _blueprints.FirstOrDefault(b => b.Id == id)
        ?? new BlueprintInfo
        {
            Id = id,
            Tasks = _tasks
                .Where(t => (t.Blueprint ?? "—") == id)
                .GroupBy(t => t.Status)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            Total = _tasks.Count(t => (t.Blueprint ?? "—") == id),
        };

    private void DrawCards()
    {
        var cards = _order.Select(id => new BlueprintCard(InfoOf(id))).ToArray();
        var filter = _cardFilter ?? (cards.Any(c => c.Filter is "open") ? "open" : "all");
        foreach (var chip in CardChips)
        {
            chip.Count = chip.Key is "all" ? cards.Length : cards.Count(c => c.Filter == chip.Key);
            _drawing = true;
            chip.On = chip.Key == filter;
            _drawing = false;
        }
        var shown = cards.Where(c => filter is "all" || c.Filter == filter).ToArray();
        var movable = shown.Where(c => c.HasBlueprint).Select(c => c.Id).ToList();
        foreach (var card in shown)
        {
            var at = movable.IndexOf(card.Id);
            card.CanMoveUp = at > 0;
            card.CanMoveDown = at >= 0 && at < movable.Count - 1;
        }
        Cards.Clear();
        foreach (var card in shown)
        {
            Cards.Add(card);
        }
        CardsEmpty = shown.Length > 0 ? null : "No blueprints match.";
    }

    private void DrawTasks()
    {
        var all = _tasks;
        bool InFilter(TaskSummary t) => BlueprintFilter.Length is 0 || (t.Blueprint ?? "—") == BlueprintFilter;
        var shown = all.Where(t => _statuses.Contains(t.Status) && InFilter(t)).ToArray();
        var deferred = _statuses.Contains("deferred") ? [] : all.Where(t => t.Status is "deferred" && InFilter(t)).ToArray();
        if (shown.Length is 0 && deferred.Length is 0)
        {
            var done = all.Count(t => t.Status is "done");
            TasksEmpty = all.Count > 0 && done == all.Count ? $"No open tasks: all {all.Count} are done." : "No tasks match.";
            OffersDone = !_statuses.Contains("done") && done > 0;
            Groups.Clear();
            Lists.Sync(Targets, []);
            return;
        }
        TasksEmpty = null;
        OffersDone = false;
        bool In(TaskSummary t, string m) => (t.Blueprint ?? "—") == m;
        // blueprints with something to work on first, those holding only deferred tasks after them
        var order = _order.Where(m => shown.Any(t => In(t, m))).Concat(_order.Where(m => !shown.Any(t => In(t, m)) && deferred.Any(t => In(t, m)))).ToArray();
        var movable = BlueprintFilter.Length > 0 ? [] : order.Where(m => m is not "—").ToList();
        var groups = new List<TaskGroup>();
        foreach (var m in order)
        {
            var group = Groups.FirstOrDefault(g => g.Id == m) ?? new TaskGroup(m);
            var info = _blueprints.FirstOrDefault(b => b.Id == m);
            var mine = all.Where(t => In(t, m)).ToArray();
            var rows = shown.Where(t => In(t, m)).ToArray();
            var reasons = deferred.Where(t => In(t, m)).GroupBy(t => t.BlockedReason ?? "").ToArray();
            var done = mine.Count(t => t.Status is "done");
            group.Title = info?.Title;
            group.Color = info?.Color;
            group.Goal = info?.Goal;
            group.Open = !_collapsed.Contains(m);
            group.CountText = $"{rows.Length} shown" + (reasons.Length > 0 ? $", {reasons.Sum(r => r.Count())} deferred" : "");
            group.Progress = mine.Length > 0 ? done / (double)mine.Length : 0;
            group.ProgressText = $"{done} of {mine.Length} tasks done";
            var at = movable.IndexOf(m);
            group.Movable = at >= 0;
            group.CanMoveUp = at > 0;
            group.CanMoveDown = at >= 0 && at < movable.Count - 1;
            Lists.Sync(group.Rows, rows.Select(Row));
            Lists.Sync(group.Deferred, reasons.Select(r => new DeferredLine(r.Key.Length > 0 ? r.Key : "no reason given", r.Count())));
            groups.Add(group);
        }
        Lists.Sync(Groups, groups);
        // the blueprints with nothing shown (and "no blueprint"), as targets for a move
        Lists.Sync(
            Targets,
            _order.Where(m => !order.Contains(m)).Concat(_order.Contains("—") ? [] : ["—"]).Select(m =>
                new TaskGroup(m) { Title = _blueprints.FirstOrDefault(b => b.Id == m)?.Title, Color = _blueprints.FirstOrDefault(b => b.Id == m)?.Color }
            )
        );
    }

    private TaskRow Row(TaskSummary t)
    {
        var passed = t.Acceptance.Count(c => c.Result is "pass" or "n/a");
        var (left, level) = TaskStates.Lease(t.Claim);
        var lease = left.Length > 0 ? " · " + left : "";
        var claim = t.Claim is { } c ? c.Claimant + lease : null;
        return new TaskRow(
            t.Id,
            t.Title,
            t.Type,
            t.Priority,
            t.Status,
            claim,
            level,
            t.Acceptance.Count > 0 ? $"{passed}/{t.Acceptance.Count}" : "",
            TaskStates.Editable(t),
            Route.ProjectHref(ProjectId!, "work", t.Id)
        );
    }

    private void StatusToggled(FilterChip chip)
    {
        if (chip.On)
        {
            _statuses.Add(chip.Key);
        }
        else
        {
            _statuses.Remove(chip.Key);
        }
        DrawTasks();
    }

    private void CardFilterChosen(FilterChip chip)
    {
        if (_drawing || !chip.On)
        {
            return;
        }
        _cardFilter = chip.Key;
        DrawCards();
    }

    [RelayCommand]
    private void ShowDone() => StatusChips.First(c => c.Key is "done").On = true;

    [RelayCommand]
    private void ShowDeferred() => StatusChips.First(c => c.Key is "deferred").On = true;

    [RelayCommand]
    private void Toggle(TaskGroup group)
    {
        if (!_collapsed.Remove(group.Id))
        {
            _collapsed.Add(group.Id);
        }
        group.Open = !_collapsed.Contains(group.Id);
    }

    /// <summary>A card's "Show its tasks": Tasks narrowed to the blueprint, every state shown.</summary>
    [RelayCommand]
    private void ShowTasksOf(BlueprintCard card)
    {
        BlueprintFilter = card.Id;
        foreach (var chip in StatusChips)
        {
            chip.On = true;
        }
        _collapsed.Remove(card.Id);
        _services.Host.Navigation.Go(Route.ProjectHref(ProjectId!, "work", "tasks"));
        DrawTasks();
    }

    [RelayCommand]
    private async Task OpenTaskAsync(string id)
    {
        var task = _tasks.FirstOrDefault(t => t.Id == id);
        await _services.Dialogs.ShowAsync(
            new TaskDialog(_services, ProjectId!, id, this)
            {
                Title = task is null ? id : $"{id} · {task.Title}",
                Sub = task?.Blueprint is { } blueprint ? $"In blueprint {blueprint}" : "Without a blueprint",
                Wide = true,
                HasCancel = false,
                AcceptLabel = "Close",
            }
        );
    }

    [RelayCommand]
    private Task NewTaskAsync(string? blueprint) => TaskFormDialog.NewAsync(_services, ProjectId!, _blueprints, _tasks, blueprint is "—" ? "" : blueprint ?? "");

    [RelayCommand]
    private Task EditTaskAsync(string id) =>
        _tasks.FirstOrDefault(t => t.Id == id) is { } task && TaskStates.Editable(task)
            ? TaskFormDialog.EditAsync(_services, ProjectId!, _blueprints, _tasks, task)
            : Task.CompletedTask;

    [RelayCommand]
    private async Task DeleteTaskAsync(string id) => await TaskDialog.DeleteAsync(_services, ProjectId!, _tasks.FirstOrDefault(t => t.Id == id)?.Title ?? id, id);

    [RelayCommand]
    private Task NewBlueprintAsync() => BlueprintFormDialog.NewAsync(_services, ProjectId!);

    [RelayCommand]
    private Task EditBlueprintAsync(BlueprintCard card) => BlueprintFormDialog.EditAsync(_services, ProjectId!, card.Info);

    [RelayCommand]
    private Task RemoveBlueprintAsync(BlueprintCard card) =>
        RemoveBlueprintDialog.AskAsync(_services, ProjectId!, card.Info, [.. _blueprints.Where(b => b.Id != card.Id)]);

    [RelayCommand]
    private Task MoveCardUpAsync(BlueprintCard card) => MoveAsync(card.Id, Cards.Select(c => c.Id), -1);

    [RelayCommand]
    private Task MoveCardDownAsync(BlueprintCard card) => MoveAsync(card.Id, Cards.Select(c => c.Id), 1);

    [RelayCommand]
    private Task MoveGroupUpAsync(TaskGroup group) => MoveAsync(group.Id, Groups.Select(g => g.Id), -1);

    [RelayCommand]
    private Task MoveGroupDownAsync(TaskGroup group) => MoveAsync(group.Id, Groups.Select(g => g.Id), 1);

    /// <summary>Moves <paramref name="id"/> one place among the <paramref name="shown"/> blueprints and saves the order.</summary>
    private Task MoveAsync(string id, IEnumerable<string> shown, int by)
    {
        var list = shown.Where(m => m is not "—").ToList();
        var at = list.IndexOf(id);
        var to = at + by;
        return at < 0 || to < 0 || to >= list.Count ? Task.CompletedTask : PlaceAsync(id, list[to], by > 0);
    }

    /// <summary>Puts <paramref name="id"/> before (or after) <paramref name="target"/> in the whole order and saves it.</summary>
    public async Task PlaceAsync(string id, string target, bool after)
    {
        if (id == target)
        {
            return;
        }
        var rest = _order.Where(m => m is not "—" && m != id).ToList();
        rest.Insert(rest.IndexOf(target) + (after ? 1 : 0), id);
        try
        {
            await _services.Api.OrderBlueprintsAsync(ProjectId!, rest, CancellationToken.None);
            _services.Toasts.Show("Blueprint order saved");
            _services.Live.RefreshAll();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _services.Toasts.Fail(exception.Message);
        }
    }

    /// <summary>Moves a task to another blueprint ("—" none), as dropping it there does.</summary>
    public async Task MoveTaskAsync(string taskId, string blueprint)
    {
        var to = blueprint is "—" ? "" : blueprint;
        if (_tasks.FirstOrDefault(t => t.Id == taskId) is not { } task || (task.Blueprint ?? "") == to)
        {
            return;
        }
        try
        {
            await _services.Api.MoveTaskAsync(ProjectId!, taskId, to, CancellationToken.None);
            _services.Toasts.Show($"{taskId} moved to {(to.Length > 0 ? to : "no blueprint")}");
            _services.Live.RefreshAll();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _services.Toasts.Fail(exception.Message);
        }
    }
}
