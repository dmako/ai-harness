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

/// <summary>A rate beside the rings (app.js rings()): its words, its picture over the window, and where it leads.</summary>
public sealed record RingRow(
    string Label,
    string Brush,
    double? Value,
    string Text,
    string? Delta,
    bool DeltaUp,
    IReadOnlyList<double>? Bars,
    IReadOnlyList<double?>? Line,
    string Href
)
{
    public string Percent => Value is { } v ? Overview.Pct(v) : "—";
    public bool HasDelta => Delta is { Length: > 0 };
    public bool HasSpark => Bars is not null || Line is not null;
    public string AccessibleName => $"{Label}: {Text}";

    public bool Equals(RingRow? other) =>
        other is not null
        && Label == other.Label
        && Nullable.Equals(Value, other.Value)
        && Text == other.Text
        && Delta == other.Delta
        && Same(Bars, other.Bars)
        && Same(Line, other.Line);

    public override int GetHashCode() => HashCode.Combine(Label, Value, Text);

    private static bool Same<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);
}

/// <summary>A task in the Now card: its state where it is not ready, its id and title.</summary>
public sealed record NowRow(string Id, string Title, string Status, string Href)
{
    public bool ShowStatus => Status is not "ready";
    public string StatusWords => Status.Replace('_', ' ');
    public string StatusIcon => "status-" + Status;
    public string AccessibleName => ShowStatus ? $"{Id} {StatusWords}: {Title}" : $"{Id}: {Title}";
}

/// <summary>Something waiting for a person (app.js Needs attention): how urgent, what, and where to act.</summary>
public sealed record AttentionItem(string Level, string Message, string Href)
{
    public string Icon =>
        Level switch
        {
            "problem" or "fail" => "st-fail",
            "warn" => "st-warn",
            _ => "level-info",
        };

    public bool IsProblem => Level is "problem" or "fail";
    public bool IsWarn => Level is "warn";
    public string AccessibleName => $"{Level}: {Message}";
}

/// <summary>
/// The page's helpers for the overview (app.js pct, weekDone, weekRate, workProposals and the activity's session
/// line).
/// </summary>
public static partial class Overview
{
    public static string Pct(double f) => Math.Round(f * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>Tasks completed in the last 7 days.</summary>
    public static (string Text, bool Up)? WeekDone(IReadOnlyList<TrendDay> days)
    {
        var n = days.TakeLast(7).Sum(d => d.Done);
        return n > 0 ? ($"+{n.ToString(CultureInfo.InvariantCulture)} this week", true) : null;
    }

    /// <summary>The context hit rate of the last 7 days against the 7 before (each answer weighs the same).</summary>
    public static (string Text, bool Up)? WeekRate(IReadOnlyList<TrendDay> days)
    {
        static double? Mean(IEnumerable<TrendDay> xs)
        {
            var list = xs.ToArray();
            var n = list.Sum(d => d.Answers);
            return n > 0 ? list.Sum(d => d.HitRate * d.Answers) / n : null;
        }
        var a = Mean(days.TakeLast(7));
        var b = Mean(days.Count > 7 ? days.Take(days.Count - 7).TakeLast(7) : []);
        if (a is not { } now || b is not { } before)
        {
            return null;
        }
        var points = (int)Math.Round((now - before) * 100, MidpointRounding.AwayFromZero);
        if (points is 0)
        {
            return ("steady this week", false);
        }
        var arrow = points > 0 ? "▲" : "▼";
        return ($"{arrow} {Math.Abs(points).ToString(CultureInfo.InvariantCulture)} pts vs the week before", points > 0);
    }

    /// <summary>A session's calls and failed calls, from its title ("… used harness: 12 call(s) over 5 min, 2 failed").</summary>
    public static (int Calls, int Failed, string Span) Session(string title)
    {
        var m = SessionTitle().Match(title);
        if (!m.Success)
        {
            return (0, 0, "");
        }
        var failed = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), failed, m.Groups[2].Value);
    }

    [GeneratedRegex(@": (\d+) call\(s\) over (.+?)(?:, (\d+) failed)?$", RegexOptions.None, 1000)]
    private static partial Regex SessionTitle();
}

/// <summary>
/// A project's Overview (app.js viewOverview): since the last visit, the rings, what is in flight, the proposals
/// waiting, what needs attention, and the recent activity beside. The top and Needs attention load together (tasks,
/// report and trends, as app.js does); the activity loads on its own.
/// </summary>
public sealed partial class OverviewViewModel : ObservableObject, IDisposable
{
    private readonly UiServices _services;

    public OverviewViewModel(UiServices services, ProjectInfo project)
    {
        _services = services;
        Summary = new OverviewSummary(services, project);
        Activity = new ActivityViewModel(services, project.Id);
    }

    public OverviewSummary Summary { get; }
    public ActivityViewModel Activity { get; }

    /// <summary>What happened since this browser's last visit, more than an hour ago (app.js sinceVisit).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSince))]
    public partial string? Since { get; private set; }

    public bool HasSince => Since is { Length: > 0 };

    /// <summary>Follows live updates and loads every section, each on its own.</summary>
    public void Start()
    {
        Summary.Follow(_services.Live);
        Activity.Follow(_services.Live);
        _ = Summary.LoadAsync();
        _ = Activity.LoadAsync();
        _ = SinceAsync();
    }

    /// <summary>The project's newer list entry: its proposals and failing checks are in the attention list.</summary>
    public void Update(ProjectInfo project) => Summary.Update(project);

    public void Dispose()
    {
        Summary.Dispose();
        Activity.Dispose();
    }

    private async Task SinceAsync()
    {
        var key = "seen." + Summary.ProjectId;
        var visits = _services.Host.Visits;
        if (!visits.TryGetValue(key, out var previous))
        {
            previous = _services.Host.Preferences.Get(key) ?? "";
            visits[key] = previous;
            _services.Host.Preferences.Set(key, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        // the first visit, or back within the hour
        if (
            !DateTimeOffset.TryParse(previous, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from)
            || DateTimeOffset.UtcNow - from < TimeSpan.FromHours(1)
        )
        {
            return;
        }
        ActivityFeed feed;
        try
        {
            feed = await _services.Api.ActivityAsync(Summary.ProjectId, 7, 200, CancellationToken.None);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            return;
        }
        var items = feed.Items.Where(x => DateTimeOffset.TryParse(x.At, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at > from).ToArray();
        var calls = items.Where(x => x.Kind is "session").Sum(x => Overview.Session(x.Title).Calls);
        var tasks = items.Where(x => x.Kind is "task").ToArray();
        var done = tasks.Count(x => x.Title.Contains(" done: ", StringComparison.Ordinal));
        var other = tasks.Length - done;
        var knowledge = items.Count(x => x.Kind is "memory");
        var parts = new List<string>();
        if (calls > 0)
        {
            parts.Add(Shell.Plural(calls, "agent call"));
        }
        if (done > 0)
        {
            parts.Add(Shell.Plural(done, "task") + " done");
        }
        if (other > 0)
        {
            parts.Add(Shell.Plural(other, "other task change"));
        }
        if (knowledge > 0)
        {
            parts.Add(Shell.Plural(knowledge, "knowledge change"));
        }
        if (parts.Count > 0)
        {
            Since = $"Since your last visit {Shell.Ago(previous)}: {string.Join(" · ", parts)}";
        }
    }
}

/// <summary>
/// The Overview's top and Needs attention: the rings (tasks done, context hits, read next), what is in flight, the
/// proposals waiting, and what waits for a person, most urgent first. The report and trends are optional: without
/// them the rings say so; without the tasks the section fails with Retry.
/// </summary>
public sealed partial class OverviewSummary : SectionViewModel
{
    private static readonly Dictionary<string, int> Rank = new(StringComparer.Ordinal)
    {
        ["problem"] = 0,
        ["fail"] = 0,
        ["warn"] = 1,
        ["info"] = 2,
    };

    private readonly UiServices _services;
    private ProjectInfo _project;
    private IReadOnlyList<TaskSummary> _tasks = [];
    private ProjectReport? _report;
    private IReadOnlyList<TrendDay> _days = [];

    public OverviewSummary(UiServices services, ProjectInfo project)
        : base("Needs attention", project.Id, ["tasks", "knowledge", "activity", "sync"])
    {
        _services = services;
        _project = project;
        Hint = "What waits for a person in this project, most urgent first.";
    }

    public ObservableCollection<RingRow> Rings { get; } = [];
    public ObservableCollection<NowRow> Now { get; } = [];
    public ObservableCollection<AttentionItem> Attention { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<RingValue> RingValues { get; private set; } = [];

    [ObservableProperty]
    public partial string RingsName { get; private set; } = "";

    [ObservableProperty]
    public partial string NowLabel { get; private set; } = "Next up";

    [ObservableProperty]
    public partial string NowNote { get; private set; } = "";

    /// <summary>Why the Now card shows done tasks or nothing (no tasks, all done, nothing to pick up).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNowEmpty))]
    public partial string? NowEmpty { get; private set; }

    public bool HasNowEmpty => NowEmpty is { Length: > 0 };

    [ObservableProperty]
    public partial bool AllDone { get; private set; }

    [ObservableProperty]
    public partial bool HasMoreTasks { get; private set; }

    [ObservableProperty]
    public partial string Proposals { get; private set; } = "0";

    [ObservableProperty]
    public partial string ProposalsNote { get; private set; } = "";

    [ObservableProperty]
    public partial string ProposalsHref { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttention))]
    public partial int AttentionCount { get; private set; }

    public bool HasAttention => AttentionCount > 0;

    /// <summary>The report has findings for the user: the link to how harness serves the agents.</summary>
    [ObservableProperty]
    public partial bool HasFindings { get; private set; }

    public string TasksHref => Route.ProjectHref(ProjectId!, "work", "tasks");
    public string AgentsHref => Route.ProjectHref(ProjectId!, "agents");

    public void Update(ProjectInfo project)
    {
        _project = project;
        if (HasContent)
        {
            Show();
        }
    }

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var id = ProjectId!;
        var days = _services.Host.Days;
        var tasks = _services.Api.TasksAsync(id, token);
        var report = Optional(_services.Api.ReportAsync(id, days, token));
        var trends = Optional(_services.Api.TrendsAsync(id, days, token));
        _tasks = (await tasks).Tasks;
        _report = await report;
        _days = (await trends)?.Days ?? [];
        Show();
    }

    private static async Task<T?> Optional<T>(Task<T> read)
        where T : class
    {
        try
        {
            return await read;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            return null;
        }
    }

    private void Show()
    {
        var id = ProjectId!;
        var all = _tasks;
        var done = all.Count(t => t.Status is "done");
        var context = _report?.Context;
        var read = context?.AfterRead;
        var tasksDelta = Overview.WeekDone(_days);
        var rateDelta = Overview.WeekRate(_days);
        RingRow[] rings =
        [
            new(
                "Tasks done",
                "Good",
                all.Count > 0 ? done / (double)all.Count : null,
                all.Count > 0 ? $"{done} of {all.Count}" : "no tasks",
                tasksDelta?.Text,
                tasksDelta?.Up ?? false,
                _days.Any(d => d.Done > 0) ? [.. _days.Select(d => (double)d.Done)] : null,
                null,
                Route.ProjectHref(id, "work")
            ),
            new(
                "Context hits",
                "Accent",
                context is { Changed: > 0 } ? context.HitRate : null,
                context is { Changed: > 0 } ? $"{Overview.Pct(context.HitRate)} per answer" : "not scored yet",
                rateDelta?.Text,
                rateDelta?.Up ?? false,
                null,
                _days.Count(d => d.Answers > 0) >= 2 ? [.. _days.Select(d => d.Answers > 0 ? d.HitRate : (double?)null)] : null,
                Route.ProjectHref(id, "agents")
            ),
            new(
                "Read next",
                "CConvention",
                read is { Touched: > 0 } ? read.HitRate : null,
                read is { Touched: > 0 } ? $"{read.Hits} of {read.Touched} files" : "needs the client hooks",
                null,
                false,
                null,
                null,
                Route.ProjectHref(id, "agents")
            ),
        ];
        Lists.Sync(Rings, rings);
        RingValues = [.. rings.Select(r => new RingValue(r.Brush, r.Value))];
        RingsName = string.Join("; ", rings.Select(r => r.AccessibleName));
        ShowNow(all);
        ShowProposals();
        ShowAttention(all);
    }

    /// <summary>What is in flight, then the next ready tasks, a few each (app.js nowCard).</summary>
    private void ShowNow(IReadOnlyList<TaskSummary> all)
    {
        var id = ProjectId!;
        var flight = all.Where(t => t.Status is "claimed" or "in_progress" or "review").ToArray();
        var ready = all.Where(t => t.Status is "ready").ToArray();
        var shown = flight.Take(3).Concat(ready.Take(Math.Max(0, 4 - Math.Min(flight.Length, 3)))).ToArray();
        NowLabel = flight.Length > 0 ? "In flight" : "Next up";
        NowNote = $"{flight.Length} in flight · {ready.Length} ready";
        AllDone = all.Count > 0 && all.All(t => t.Status is "done");
        HasMoreTasks = flight.Length + ready.Length > shown.Length;
        if (shown.Length > 0)
        {
            NowEmpty = null;
        }
        else if (all.Count is 0)
        {
            NowEmpty = "No tasks yet.";
        }
        else
        {
            NowEmpty = AllDone
                ? "✓ Every task is done; the last ones:"
                : "Nothing in flight or ready: the open tasks wait on others or are deferred. Done last:";
            // nothing to pick up: what was finished last, newest first
            shown = [.. all.Where(t => t.Status is "done").OrderByDescending(t => t.Completion?.CompletedAt ?? "", StringComparer.Ordinal).Take(3)];
        }
        Lists.Sync(Now, shown.Select(t => new NowRow(t.Id, t.Title, t.Status, Route.ProjectHref(id, "work", t.Id))));
    }

    private void ShowProposals()
    {
        var live = _project.Live;
        var knowledge = live.PendingProposals;
        var work = live.WorkProposals;
        Proposals = (knowledge + work).ToString(CultureInfo.InvariantCulture);
        ProposalsNote = knowledge + work > 0 ? $"{knowledge} knowledge · {work} tasks" : "nothing waiting";
        ProposalsHref = knowledge > 0 || work is 0
            ? Route.ProjectHref(ProjectId!, "knowledge", "inbox")
            : Route.ProjectHref(ProjectId!, "work", "inbox");
    }

    /// <summary>What waits for a person, most urgent first (app.js viewOverview's items).</summary>
    private void ShowAttention(IReadOnlyList<TaskSummary> all)
    {
        var id = ProjectId!;
        var live = _project.Live;
        var items = new List<AttentionItem>();
        if (live.PendingProposals > 0)
        {
            var verb = live.PendingProposals is 1 ? "waits" : "wait";
            items.Add(new("warn", $"{Shell.Plural(live.PendingProposals, "knowledge proposal")} {verb} for review", Route.ProjectHref(id, "knowledge", "inbox")));
        }
        if (live.RemovedFromProject > 0)
        {
            var them = live.RemovedFromProject is 1 ? "it" : "them";
            var memories = live.RemovedFromProject is 1 ? "1 memory" : $"{live.RemovedFromProject} memories";
            items.Add(new("warn", $"{memories} left project knowledge in a Git change: keep {them} local or retire", Route.ProjectHref(id, "knowledge", "inbox")));
        }
        if (live.WorkProposals > 0)
        {
            var verb = live.WorkProposals is 1 ? "waits" : "wait";
            items.Add(new("warn", $"{Shell.Plural(live.WorkProposals, "task proposal")} {verb} for review", Route.ProjectHref(id, "work", "inbox")));
        }
        if (live.ChecksFailed > 0)
        {
            items.Add(new("problem", $"{Shell.Plural(live.ChecksFailed, "doctor check")} failing", Route.ProjectHref(id, "health")));
        }
        foreach (var task in all.Where(t => t.Status is "blocked"))
        {
            var reason = task.BlockedReason is { Length: > 0 } r ? ": " + r : "";
            items.Add(new("warn", $"{task.Id} is blocked{reason}", Route.ProjectHref(id, "work", task.Id)));
        }
        var findings = (_report?.Findings ?? []).Where(f => f.Audience is not "developer").ToArray();
        HasFindings = findings.Length > 0;
        items.AddRange(
            findings
                .Where(f => f.Level is "problem" or "warn")
                .Take(5)
                .Select(f => new AttentionItem(f.Level, f.Message, Route.ProjectHref(id, "agents")))
        );
        Lists.Sync(Attention, items.OrderBy(i => Rank.GetValueOrDefault(i.Level, 2)));
        AttentionCount = Attention.Count;
    }
}

/// <summary>A day of the activity strip: its weekday, calls and the task and knowledge changes (drawn as dots).</summary>
public sealed record ActivityDay(string Label, int Calls, int Changes, bool Today)
{
    public string CallsText => Calls > 0 ? Calls.ToString(CultureInfo.InvariantCulture) : "";
}

/// <summary>An activity line (app.js activityRow): what and when, then who and the detail.</summary>
public sealed record ActivityRow(string Kind, string Glyph, string Head, string? HeadMuted, string? Failed, string When, string? Level, string Meta, string Href)
{
    public bool HasHeadMuted => HeadMuted is { Length: > 0 };
    public bool HasFailed => Failed is { Length: > 0 };
    public bool HasMeta => Meta.Length > 0;
    public bool HasLevel => Level is { Length: > 0 };
    public bool IsWarn => Level is "warn";
    public bool IsProblem => Level is "problem" or "fail";
    public string LevelIcon => IsWarn ? "st-warn" : "st-fail";
}

/// <summary>
/// A project's recent activity (app.js activitySection): a strip of the last 7 days (agents' calls a day, the failed
/// apart, and the task and knowledge changes), then the newest 8 lines, all on request.
/// </summary>
public sealed partial class ActivityViewModel : SectionViewModel
{
    private const int Shown = 8;
    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.Ordinal)
    {
        ["task"] = "✓",
        ["memory"] = "✎",
        ["sync"] = "↻",
    };

    private static readonly Dictionary<string, string> Clients = new(StringComparer.Ordinal)
    {
        ["claude-code"] = "Claude Code",
        ["codex"] = "Codex",
    };

    private readonly UiServices _services;
    private IReadOnlyList<ActivityItem> _items = [];

    public ActivityViewModel(UiServices services, string projectId)
        : base("Recent activity", projectId, ["activity", "tasks", "knowledge", "sync"])
    {
        _services = services;
    }

    public ObservableCollection<ActivityRow> Rows { get; } = [];
    public ObservableCollection<ActivityDay> Week { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<DayBar> Bars { get; private set; } = [];

    [ObservableProperty]
    public partial string WeekSummary { get; private set; } = "";

    [ObservableProperty]
    public partial string WeekName { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Quiet))]
    public partial bool HasItems { get; private set; }

    public bool Quiet => !HasItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    public partial bool ShowingAll { get; private set; }

    public bool HasMore => !ShowingAll && _items.Count > Shown;
    public string MoreLabel => $"Show all {_items.Count}";

    [RelayCommand]
    private void ShowAll()
    {
        ShowingAll = true;
        Lists.Sync(Rows, _items.Select(Row));
    }

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        _items = (await _services.Api.ActivityAsync(ProjectId, 7, 200, token)).Items;
        HasItems = _items.Count > 0;
        Lists.Sync(Rows, (ShowingAll ? _items : _items.Take(Shown)).Select(Row));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(MoreLabel));
        ShowWeek();
    }

    /// <summary>The 7 days to today in local time: calls, failed calls, sessions and changes each.</summary>
    private void ShowWeek()
    {
        var today = DateTime.Today;
        var days = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).ToArray();
        var calls = new int[7];
        var failed = new int[7];
        var sessions = new int[7];
        var changes = new int[7];
        foreach (var item in _items)
        {
            if (!DateTimeOffset.TryParse(item.At, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                continue;
            }
            var index = Array.IndexOf(days, at.ToLocalTime().Date);
            if (index < 0)
            {
                continue;
            }
            if (item.Kind is "session")
            {
                var (n, bad, _) = Overview.Session(item.Title);
                calls[index] += n;
                failed[index] += bad;
                sessions[index]++;
            }
            else if (item.Kind is not "sync")
            {
                changes[index]++;
            }
        }
        Bars = [.. Enumerable.Range(0, 7).Select(i => new DayBar(calls[i], failed[i]))];
        Lists.Sync(
            Week,
            Enumerable
                .Range(0, 7)
                .Select(i => new ActivityDay(i is 6 ? "today" : days[i].ToString("ddd", CultureInfo.InvariantCulture), calls[i], changes[i], i is 6))
        );
        var total = calls.Sum();
        var change = changes.Sum();
        WeekSummary =
            $"{Shell.Plural(total, "call")} in {Shell.Plural(sessions.Sum(), "session")}"
            + (change > 0 ? $" · {Shell.Plural(change, "change")} to tasks and knowledge" : "");
        WeekName = string.Join(", ", Enumerable.Range(0, 7).Select(i => $"{days[i].ToString("ddd", CultureInfo.InvariantCulture)}: {calls[i]} calls"));
    }

    private static ActivityRow Row(ActivityItem x)
    {
        var href = x.Kind switch
        {
            "task" => Route.ProjectHref(x.ProjectId, "work", x.Ref),
            "memory" => Route.ProjectHref(x.ProjectId, "knowledge"),
            "sync" => Route.ProjectHref(x.ProjectId, "health"),
            _ => Route.ProjectHref(x.ProjectId, "agents", "tools"),
        };
        var when = Shell.Ago(x.At);
        if (x.Kind is "session")
        {
            var (calls, failed, span) = Overview.Session(x.Title);
            var client = x.Actor is { Length: > 0 } actor ? Clients.GetValueOrDefault(actor, actor) : "an agent";
            var meta = x.Detail is { Length: > 0 } detail ? $"{client} · {detail}" : client;
            return new ActivityRow(
                "session",
                client[..1],
                Shell.Plural(calls, "call"),
                span.Length > 0 ? "over " + span : null,
                failed > 0 ? $"{failed} failed" : null,
                when,
                x.Level,
                meta,
                href
            );
        }
        var parts = new List<string>();
        if (x.Detail is { Length: > 0 } d)
        {
            parts.Add(d);
        }
        if (x.Actor is { Length: > 0 } who)
        {
            parts.Add(who);
        }
        return new ActivityRow(x.Kind, Glyphs.GetValueOrDefault(x.Kind, "•"), x.Title, null, null, when, x.Level, string.Join(" · ", parts), href);
    }
}
