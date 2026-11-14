using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>A line of a list being edited (a criterion, a Done when entry).</summary>
public sealed partial class LineItem(string text) : ObservableObject
{
    [ObservableProperty]
    public partial string Text { get; set; } = text;
}

/// <summary>A blueprint colour to pick ("" lets harness pick the first one no other blueprint uses).</summary>
public sealed record ColorChoice(string Name)
{
    public string Class => Name.Length > 0 ? "bp-" + Name : "";
    public bool IsAuto => Name.Length is 0;
    public string Label => IsAuto ? "auto" : Name;
    public string Tip => IsAuto ? "The first colour no other blueprint uses" : Name;
}

/// <summary>A task one may wait for, in the Depends on picker.</summary>
public sealed partial class DependencyOption(string id, string title, string status) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Status { get; } = status;
    public string StatusClass => "st-" + Status;
    public string StatusIcon => TaskStates.Icon(Status);
    public string StatusWords => TaskStates.Words(Status);

    [ObservableProperty]
    public partial bool Chosen { get; set; }
}

/// <summary>An acceptance criterion or validation check in the task dialog: its result's mark, words and evidence.</summary>
public sealed record CheckRow(string Result, string Text, string? Evidence, bool Mono = false)
{
    public string Mark =>
        Result switch
        {
            "pass" => "✓",
            "fail" => "✕",
            "n/a" => "–",
            _ => "○",
        };

    public bool Passed => Result is "pass";
    public bool Failed => Result is "fail";
    public bool HasEvidence => Evidence is { Length: > 0 };
}

/// <summary>An audit trail line: what happened, the states it moved between, who and when, and the detail.</summary>
public sealed record EventRow(string Event, string? From, string? To, string Who, string? Detail)
{
    public bool Moved => From is { Length: > 0 } && To is { Length: > 0 };
    public string Move => Moved ? $"{TaskStates.Words(From!)} → {TaskStates.Words(To!)}" : "";
    public bool HasDetail => Detail is { Length: > 0 };
}

/// <summary>An agent session in the task dialog: the client, its id, what it did, when and what it used.</summary>
public sealed record SessionRow(string Client, string ShortId, string Status, string Seen, string Usage);

/// <summary>
/// A task (app.js taskDetail, auditBlock, sessionsBlock): why it is blocked or deferred, its goal, criteria,
/// completion and notes, the agent sessions that worked on it, its audit trail on request, and the facts beside;
/// Edit (ready or backlog only), Move to another blueprint and Delete. Opened from Work › Tasks and the Board.
/// </summary>
public sealed partial class TaskDialog : DialogViewModel<bool>
{
    private readonly UiServices _services;
    private readonly string _projectId;
    private readonly string _taskId;
    private IReadOnlyList<BlueprintInfo> _blueprints = [];
    private IReadOnlyList<TaskSummary> _tasks = [];

    public TaskDialog(UiServices services, string projectId, string taskId, WorkPlan? plan = null)
    {
        _services = services;
        _projectId = projectId;
        _taskId = taskId;
        if (plan is { HasContent: true })
        {
            _blueprints = plan.AllBlueprints;
            _tasks = plan.AllTasks;
        }
        Loaded = LoadAsync();
    }

    /// <summary>Done once the task and its sessions are read.</summary>
    public Task Loaded { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTask))]
    public partial TaskSummary? Task { get; private set; }

    public bool HasTask => Task is not null;

    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    public ObservableCollection<CheckRow> Criteria { get; } = [];
    public ObservableCollection<CheckRow> Checks { get; } = [];
    public ObservableCollection<Fact> Facts { get; } = [];
    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public ObservableCollection<EventRow> Trail { get; } = [];
    public ObservableCollection<Choice> MoveChoices { get; } = [];

    [ObservableProperty]
    public partial string? Callout { get; private set; }

    [ObservableProperty]
    public partial bool CalloutBad { get; private set; }

    [ObservableProperty]
    public partial string CriteriaHead { get; private set; } = "";

    [ObservableProperty]
    public partial string? SessionsNote { get; private set; }

    [ObservableProperty]
    public partial string? SessionsUsage { get; private set; }

    [ObservableProperty]
    public partial bool TrailShown { get; private set; }

    [ObservableProperty]
    public partial string? TrailError { get; private set; }

    [ObservableProperty]
    public partial Choice? MoveTo { get; set; }

    public bool Editable => Task is { } t && TaskStates.Editable(t);
    public string EditTip => Editable ? $"Edit {_taskId}" : "Only a ready or backlog task can be edited: once claimed, its definition is fixed";
    public bool HasCallout => Callout is { Length: > 0 };
    public bool HasGoal => Task?.Goal is { Length: > 0 };
    public bool HasCriteria => Criteria.Count > 0;
    public bool HasCompletion => Task?.Completion is not null;
    public bool HasChecks => Checks.Count > 0;
    public bool HasNotes => Task?.Notes is { Length: > 0 };
    public bool HasSessions => Sessions.Count > 0;
    public bool HasSessionsUsage => SessionsUsage is { Length: > 0 };

    /// <summary>Asks, then deletes a task (harness ui's only); refused while claimed or depended on.</summary>
    public static async Task<bool> DeleteAsync(UiServices services, string projectId, string what, string taskId)
    {
        var confirm = new ConfirmDialog
        {
            Title = $"Delete {taskId}?",
            Sub = what,
            Message = string.Join(
                "\n",
                "• The task goes with its acceptance criteria, results and audit trail; this cannot be undone.",
                "• Refused while it is claimed or while other tasks depend on it.",
                "• A backlog file that still has it brings it back on the next import: 'harness tasks export' writes the deletion into the file."
            ),
            AcceptLabel = "Delete task",
            Danger = true,
        };
        if (!await services.Dialogs.ConfirmAsync(confirm))
        {
            return false;
        }
        try
        {
            await services.Api.DeleteTaskAsync(projectId, taskId, CancellationToken.None);
            services.Toasts.Show($"{taskId} deleted");
            services.Live.RefreshAll();
            return true;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            services.Toasts.Fail(exception.Message);
            return false;
        }
    }

    protected override System.Threading.Tasks.Task AcceptAsync()
    {
        Close(true);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    [RelayCommand]
    private async Task EditAsync()
    {
        if (Task is { } task && Editable)
        {
            Close(false);
            await TaskFormDialog.EditAsync(_services, _projectId, _blueprints, _tasks, task);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        Close(false);
        await DeleteAsync(_services, _projectId, Task?.Title ?? _taskId, _taskId);
    }

    [RelayCommand]
    private async Task MoveAsync()
    {
        if (Task is not { } task || MoveTo is not { } to || to.Value == (task.Blueprint ?? ""))
        {
            return;
        }
        try
        {
            await _services.Api.MoveTaskAsync(_projectId, _taskId, to.Value, CancellationToken.None);
            _services.Toasts.Show($"{_taskId} moved to {(to.Value.Length > 0 ? to.Value : "no blueprint")}");
            Close(true);
            _services.Live.RefreshAll();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
    }

    /// <summary>The audit trail (harness tasks show --events), read on request.</summary>
    [RelayCommand]
    private async Task ShowTrailAsync()
    {
        try
        {
            var events = await _services.Api.TaskEventsAsync(_projectId, _taskId, CancellationToken.None);
            Lists.Sync(
                Trail,
                events.Events.Select(e => new EventRow(e.Event.Replace('_', ' '), e.From, e.To, $"{e.Actor} · {Shell.Ago(e.At)}", e.Detail))
            );
            TrailShown = true;
            TrailError = null;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            TrailError = exception.Message;
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            if (_tasks.Count is 0)
            {
                var blueprints = _services.Api.BlueprintsAsync(_projectId, CancellationToken.None);
                _tasks = (await _services.Api.TasksAsync(_projectId, CancellationToken.None)).Tasks;
                _blueprints = (await blueprints).Blueprints;
            }
            if (_tasks.FirstOrDefault(t => t.Id == _taskId) is not { } task)
            {
                LoadError = $"There is no task {_taskId} in this project.";
                return;
            }
            Show(task);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            LoadError = exception.Message;
            return;
        }
        try
        {
            ShowSessions(await _services.Api.TaskSessionsAsync(_projectId, _taskId, CancellationToken.None));
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            SessionsNote = exception.Message;
        }
    }

    private void Show(TaskSummary t)
    {
        Task = t;
        if (t.BlockedReason is { Length: > 0 } reason)
        {
            CalloutBad = t.Status is not "deferred";
            Callout = (CalloutBad ? "Blocked: " : "Deferred: ") + reason;
        }
        var passed = t.Acceptance.Count(c => c.Result is "pass" or "n/a");
        CriteriaHead = $"Acceptance criteria · {passed} of {t.Acceptance.Count} met";
        Lists.Sync(Criteria, t.Acceptance.Select(c => new CheckRow(c.Result, c.Criterion, c.Evidence)));
        Lists.Sync(Checks, (t.Completion?.Checks ?? []).Select(c => new CheckRow(c.Result, c.Command, c.Output, Mono: true)));
        var facts = new List<Fact>
        {
            new("Status", TaskStates.Words(t.Status)),
            new("Priority", t.Priority),
            new("Type", t.Type),
            new("Blueprint", t.Blueprint ?? ""),
            new("Depends on", string.Join("\n", t.DependsOn.Select(d => d.Status is { } s ? $"{d.Id} ({TaskStates.Words(s)})" : d.Id))),
            new("Scope", string.Join("\n", t.Scope)),
        };
        if (t.Claim is { } claim)
        {
            var (left, _) = TaskStates.Lease(claim);
            var until = claim.LeaseUntil is { Length: >= 16 } u ? $" (until {u[..16].Replace('T', ' ')})" : "";
            facts.Add(new("Claimed by", $"{claim.Claimant}\nsince {Shell.Ago(claim.ClaimedAt)}" + (left.Length > 0 ? $"\n{left}{until}" : "")));
        }
        if (t.Completion is { } done)
        {
            var commit = done.Commit is { Length: > 0 } c ? "\n" + c[..Math.Min(12, c.Length)] : "";
            facts.Add(new("Done", $"{done.CompletedBy}\n{Shell.Ago(done.CompletedAt)}{commit}"));
        }
        facts.Add(new("Validation", string.Join("\n", t.Validation)));
        Lists.Sync(Facts, facts.Where(f => f.Value.Trim().Length > 0));
        Lists.Sync(
            MoveChoices,
            new[] { new Choice("", "no blueprint") }.Concat(_blueprints.Select(b => new Choice(b.Id, b.Title is { Length: > 0 } title ? $"{b.Id} · {title}" : b.Id)))
        );
        MoveTo = MoveChoices.FirstOrDefault(c => c.Value == (t.Blueprint ?? ""));
        foreach (
            var name in new[]
            {
                nameof(Editable),
                nameof(EditTip),
                nameof(HasCallout),
                nameof(HasGoal),
                nameof(HasCriteria),
                nameof(HasCompletion),
                nameof(HasChecks),
                nameof(HasNotes),
            }
        )
        {
            OnPropertyChanged(name);
        }
    }

    private void ShowSessions(TaskSessions answer)
    {
        Lists.Sync(
            Sessions,
            answer.Sessions.Select(s => new SessionRow(
                Agents.Client(s.Client),
                s.SessionId[..Math.Min(8, s.SessionId.Length)],
                s.Status,
                $"{(s.Origin is "harness" ? "started by harness" : "seen by hooks")} · first {Shell.Ago(s.StartedAt)} · last {Shell.Ago(s.EndedAt)}",
                (s.Model is { Length: > 0 } model ? model + " · " : "")
                    + Agents.Usage(s)
                    + (s.ActiveMs > 0 ? " · active " + Agents.Duration(s.ActiveMs) : "")
            ))
        );
        SessionsNote = Sessions.Count > 0
            ? null
            : "None recorded. Sessions are linked when they claim, update or complete the task with the harness hooks installed ('harness hooks install').";
        if (Sessions.Count > 0)
        {
            var wall = DateTimeOffset.TryParse(answer.ClaimedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from)
                && DateTimeOffset.TryParse(answer.CompletedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to)
                ? (long)(to - from).TotalMilliseconds
                : 0;
            SessionsUsage =
                Agents.Usage(answer.Totals)
                + (answer.Totals.ActiveMs > 0 ? " · active " + Agents.Duration(answer.Totals.ActiveMs) : "")
                + (wall > 0 ? " · claimed to done " + Agents.Duration(wall) : "");
        }
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasSessionsUsage));
    }
}

/// <summary>How the page names agents and what their sessions used (app.js usageLine, tokenCount, durationText).</summary>
public static class Agents
{
    public static string Client(string client) =>
        client switch
        {
            "claude-code" => "Claude Code",
            "codex" => "Codex",
            _ => client,
        };

    public static string Tokens(long n) =>
        n switch
        {
            >= 1_000_000 => (n / 1e6).ToString("F2", CultureInfo.InvariantCulture) + "M",
            >= 10_000 => (n / 1e3).ToString("F1", CultureInfo.InvariantCulture) + "k",
            _ => n.ToString(CultureInfo.InvariantCulture),
        };

    public static string Duration(long ms)
    {
        var s = (long)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero);
        if (s < 60)
        {
            return $"{s} s";
        }
        var m = (long)Math.Round(s / 60.0, MidpointRounding.AwayFromZero);
        return m < 60 ? $"{m} min" : $"{m / 60} h {(m % 60).ToString("00", CultureInfo.InvariantCulture)} min";
    }

    public static string Usage(SessionUsage u)
    {
        var tokens = u.InputTokens + u.CacheReadTokens + u.CacheWriteTokens + u.OutputTokens;
        if (tokens is 0)
        {
            return "no token usage read yet";
        }
        var parts = new List<string>
        {
            $"{Tokens(tokens)} tokens",
            $"in {Tokens(u.InputTokens)}",
            $"cache read {Tokens(u.CacheReadTokens)}",
            $"cache write {Tokens(u.CacheWriteTokens)}",
            $"out {Tokens(u.OutputTokens)}",
        };
        if (u.ReasoningTokens > 0)
        {
            parts.Add($"reasoning {Tokens(u.ReasoningTokens)}");
        }
        parts.Add(u.CostUsd is { } cost ? $"${cost.ToString("F2", CultureInfo.InvariantCulture)} API-equivalent" : "no price for this model");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// A task's definition (app.js taskFields): a new task, an unclaimed one restated (its id stays), or an agent's
/// proposal restated before it is approved. Title is required; an empty id takes the next free one.
/// </summary>
public sealed partial class TaskFormDialog : FormDialog<bool>
{
    private readonly UiServices _services;
    private readonly string _projectId;
    private readonly string? _editing;
    private readonly string? _proposal;

    private TaskFormDialog(UiServices services, string projectId, IReadOnlyList<BlueprintInfo> blueprints, IReadOnlyList<TaskSummary> tasks, TaskDraft init, string? editing, string? proposal)
    {
        _services = services;
        _projectId = projectId;
        _editing = editing;
        _proposal = proposal;
        TaskId = editing ?? "";
        TaskTitle = init.Title;
        Goal = init.Goal ?? "";
        TaskType = init.Type is { Length: > 0 } type ? type : "feature";
        Priority = init.Priority is { Length: > 0 } priority ? priority : "medium";
        var known = blueprints.Select(b => new Choice(b.Id, b.Title is { Length: > 0 } t ? $"{b.Id} · {t}" : b.Id)).ToList();
        if (init.Blueprint is { Length: > 0 } bp && blueprints.All(b => b.Id != bp))
        {
            known.Add(new(bp, $"{bp} (new)"));
        }
        BlueprintChoices = [new Choice("", "none"), .. known];
        Blueprint = BlueprintChoices.FirstOrDefault(c => c.Value == (init.Blueprint ?? "")) ?? BlueprintChoices[0];
        Criteria = [.. (init.Acceptance.Count > 0 ? init.Acceptance : [""]).Select(a => new LineItem(a))];
        Scope = [.. init.Scope];
        Depends = [.. init.DependsOn];
        _options =
        [
            .. tasks
                .Where(t => t.Id != editing && t.Status is not "done")
                .Select(t => new DependencyOption(t.Id, t.Title, t.Status) { Chosen = Depends.Contains(t.Id) }),
        ];
        FilterOptions();
        if (editing is null)
        {
            _ = SuggestIdAsync();
        }
    }

    private readonly List<DependencyOption> _options;

    public static IReadOnlyList<string> Types => TaskStates.Types;
    public static IReadOnlyList<string> Priorities => TaskStates.Priorities;
    public IReadOnlyList<Choice> BlueprintChoices { get; }
    public ObservableCollection<LineItem> Criteria { get; }
    public ObservableCollection<string> Scope { get; }
    public ObservableCollection<string> Depends { get; }
    public ObservableCollection<DependencyOption> Options { get; } = [];

    [ObservableProperty]
    public partial string TaskId { get; set; }

    [ObservableProperty]
    public partial string IdHint { get; private set; } = "next free id";

    public bool IdEditable => _editing is null;
    public string IdNote => _editing is null ? "Empty: the next free one." : "An edited task keeps its id.";

    [ObservableProperty]
    public partial string TaskTitle { get; set; }

    [ObservableProperty]
    public partial string Goal { get; set; }

    [ObservableProperty]
    public partial string TaskType { get; set; }

    [ObservableProperty]
    public partial string Priority { get; set; }

    [ObservableProperty]
    public partial Choice Blueprint { get; set; }

    [ObservableProperty]
    public partial string DependsQuery { get; set; } = "";

    [ObservableProperty]
    public partial string ScopeText { get; set; } = "";

    [ObservableProperty]
    public partial string? OptionsNote { get; private set; }

    public static Task NewAsync(UiServices services, string projectId, IReadOnlyList<BlueprintInfo> blueprints, IReadOnlyList<TaskSummary> tasks, string blueprint) =>
        services.Dialogs.ShowAsync(
            new TaskFormDialog(services, projectId, blueprints, tasks, new TaskDraft { Title = "", Blueprint = blueprint }, null, null)
            {
                Title = "New task",
                Sub = "It starts in the backlog and is ready once its dependencies are done; 'harness tasks export' writes it into the backlog file.",
                Wide = true,
                AcceptLabel = "Add the task",
            }
        );

    public static Task EditAsync(UiServices services, string projectId, IReadOnlyList<BlueprintInfo> blueprints, IReadOnlyList<TaskSummary> tasks, TaskSummary task) =>
        services.Dialogs.ShowAsync(
            new TaskFormDialog(
                services,
                projectId,
                blueprints,
                tasks,
                new TaskDraft
                {
                    Title = task.Title,
                    Goal = task.Goal,
                    Type = task.Type,
                    Priority = task.Priority,
                    Blueprint = task.Blueprint,
                    Acceptance = [.. task.Acceptance.Select(c => c.Criterion)],
                    DependsOn = [.. task.DependsOn.Select(d => d.Id)],
                    Scope = task.Scope,
                },
                task.Id,
                null
            )
            {
                Title = $"Edit {task.Id}",
                Sub = "It can change until an agent claims it; its validation and notes stay as they are.",
                Wide = true,
                AcceptLabel = "Save the task",
            }
        );

    public static Task ApproveAsync(UiServices services, string projectId, IReadOnlyList<BlueprintInfo> blueprints, IReadOnlyList<TaskSummary> tasks, TaskProposal proposal) =>
        services.Dialogs.ShowAsync(
            new TaskFormDialog(services, projectId, blueprints, tasks, proposal.Task, null, proposal.Id)
            {
                Title = "Create this task?",
                Sub = $"{proposal.ProposedBy} is recorded as the proposer and you as the one who made it.",
                Wide = true,
                AcceptLabel = "Create the task",
            }
        );

    partial void OnDependsQueryChanged(string value) => FilterOptions();

    [RelayCommand]
    private void AddCriterion() => Criteria.Add(new LineItem(""));

    [RelayCommand]
    private void RemoveCriterion(LineItem line)
    {
        Criteria.Remove(line);
        if (Criteria.Count is 0)
        {
            Criteria.Add(new LineItem(""));
        }
    }

    [RelayCommand]
    private void AddScope()
    {
        foreach (var part in ScopeText.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0 && !Scope.Contains(s)))
        {
            Scope.Add(part);
        }
        ScopeText = "";
    }

    [RelayCommand]
    private void RemoveScope(string path) => Scope.Remove(path);

    /// <summary>Chooses or drops a task to wait for; a typed id another project's ("project:TASK-1") is taken as written.</summary>
    [RelayCommand]
    private void ToggleDependency(string id)
    {
        if (!Depends.Remove(id))
        {
            Depends.Add(id);
        }
        foreach (var option in _options)
        {
            option.Chosen = Depends.Contains(option.Id);
        }
    }

    [RelayCommand]
    private void AddTypedDependency()
    {
        var typed = DependsQuery.Trim();
        if (typed.Length > 0 && !Depends.Contains(typed) && (typed.Contains(':', StringComparison.Ordinal) || _options.Any(o => o.Id == typed)))
        {
            ToggleDependency(typed);
            DependsQuery = "";
        }
    }

    protected override string? Validate()
    {
        if (TaskTitle.Trim().Length is 0)
        {
            return "Title: say what should be done.";
        }
        return TaskId.Trim().Length > 0 && !TaskIdPattern().IsMatch(TaskId.Trim()) ? "Id: letters, an optional dash and digits, e.g. TASK-0042." : null;
    }

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        var form = new TaskForm
        {
            Id = _editing is null && TaskId.Trim().Length > 0 ? TaskId.Trim() : null,
            Title = TaskTitle.Trim(),
            Goal = Goal.Trim(),
            Type = TaskType,
            Priority = Priority,
            Blueprint = Blueprint.Value,
            DependsOn = [.. Depends],
            Acceptance = [.. Criteria.Select(c => c.Text.Trim()).Where(c => c.Length > 0)],
            Scope = [.. Scope],
        };
        if (_proposal is { } proposal)
        {
            await _services.Api.ApproveTaskAsync(_projectId, proposal, form, token);
            _services.Toasts.Show("Task created");
        }
        else if (_editing is { } id)
        {
            await _services.Api.EditTaskAsync(_projectId, id, form, token);
            _services.Toasts.Show($"{id} saved");
        }
        else
        {
            await _services.Api.CreateTaskAsync(_projectId, form, token);
            _services.Toasts.Show("Task added");
        }
        _services.Live.RefreshAll();
        return true;
    }

    private void FilterOptions()
    {
        var q = DependsQuery.Trim();
        var shown = _options
            .Where(o => q.Length is 0 || o.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || o.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(40)
            .ToArray();
        Lists.Sync(Options, shown);
        var none = q.Length > 0 ? "No open task here matches." : "No open task in this project.";
        OptionsNote = shown.Length > 0 ? null : none;
    }

    private async Task SuggestIdAsync()
    {
        try
        {
            IdHint = (await _services.Api.NextTaskIdAsync(_projectId, CancellationToken.None)).Id;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            // the placeholder stays "next free id"
        }
    }

    [GeneratedRegex("^[A-Za-z]+-?[0-9]+$", RegexOptions.None, 1000)]
    private static partial Regex TaskIdPattern();
}

/// <summary>
/// A blueprint's description (app.js blueprintFields): a new one (its id suggested), one restated (its id and tasks
/// stay), or an agent's proposal restated before it is approved (merge or new when one titled the same exists).
/// </summary>
public sealed partial class BlueprintFormDialog : FormDialog<bool>
{
    private readonly UiServices _services;
    private readonly string _projectId;
    private readonly string? _editing;
    private readonly BlueprintProposal? _proposal;

    private BlueprintFormDialog(UiServices services, string projectId, BlueprintDraft init, bool isNew, string? editing, BlueprintProposal? proposal)
    {
        _services = services;
        _projectId = projectId;
        _editing = editing;
        _proposal = proposal;
        IsNew = isNew;
        BlueprintId = init.Id ?? "";
        BlueprintTitle = init.Title ?? "";
        Goal = init.Goal ?? "";
        Notes = init.Notes ?? "";
        Exit = [.. (init.Exit.Count > 0 ? init.Exit : [""]).Select(e => new LineItem(e))];
        // a new one (or one without a colour) may leave it to harness
        Colors = [.. (isNew || init.Color is not { Length: > 0 } ? new[] { "" } : []).Concat(TaskStates.Colors).Select(c => new ColorChoice(c))];
        Color = Colors.FirstOrDefault(c => c.Name == (init.Color ?? "")) ?? Colors[0];
        SameTitle = proposal is { Changes: null, SameTitle: { Length: > 0 } same } ? same : null;
        if (isNew && proposal is null)
        {
            _ = SuggestIdAsync();
        }
    }

    public bool IsNew { get; }
    public bool AsksId => IsNew && _proposal is null;
    public ObservableCollection<LineItem> Exit { get; }
    public IReadOnlyList<ColorChoice> Colors { get; }

    /// <summary>The blueprint titled the same that exists already (a proposal asks merge or new).</summary>
    public string? SameTitle { get; }

    public bool AsksSameName => SameTitle is not null;
    public string SameNameQuestion => $"A blueprint titled “{BlueprintTitle}” exists already ({SameTitle}). Which should this be?";
    public string MergeLabel => $"Merge into {SameTitle}: this restates it; its id and tasks stay";

    [ObservableProperty]
    public partial string BlueprintId { get; set; }

    [ObservableProperty]
    public partial string BlueprintTitle { get; set; }

    [ObservableProperty]
    public partial string Goal { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial ColorChoice Color { get; set; }

    [ObservableProperty]
    public partial bool Merge { get; set; }

    [ObservableProperty]
    public partial bool Separate { get; set; }

    public static Task NewAsync(UiServices services, string projectId) =>
        services.Dialogs.ShowAsync(
            new BlueprintFormDialog(services, projectId, new BlueprintDraft(), true, null, null)
            {
                Title = "New blueprint",
                Sub = "A group of tasks with what it is for and when it is done.",
                Wide = true,
                AcceptLabel = "Create blueprint",
            }
        );

    public static Task EditAsync(UiServices services, string projectId, BlueprintInfo blueprint) =>
        services.Dialogs.ShowAsync(
            new BlueprintFormDialog(
                services,
                projectId,
                new BlueprintDraft
                {
                    Id = blueprint.Id,
                    Title = blueprint.Title,
                    Goal = blueprint.Goal,
                    Exit = blueprint.Exit,
                    Notes = blueprint.Notes,
                    Color = blueprint.Color,
                },
                false,
                blueprint.Id,
                null
            )
            {
                Title = $"Edit blueprint {blueprint.Id}",
                Sub = "Its id and its tasks stay.",
                Wide = true,
                AcceptLabel = "Save blueprint",
            }
        );

    public static Task ApproveAsync(UiServices services, string projectId, BlueprintProposal proposal)
    {
        var isNew = proposal.Changes is null;
        return services.Dialogs.ShowAsync(
            new BlueprintFormDialog(services, projectId, proposal.Blueprint, isNew, null, proposal)
            {
                Title = isNew ? "Create this blueprint?" : $"Restate {proposal.Changes}?",
                Sub = isNew ? $"{proposal.ProposedBy} is recorded as the proposer." : "Its id and tasks stay.",
                Wide = true,
                AcceptLabel = isNew ? "Create the blueprint" : "Save the blueprint",
            }
        );
    }

    [RelayCommand]
    private void AddExit() => Exit.Add(new LineItem(""));

    [RelayCommand]
    private void RemoveExit(LineItem line)
    {
        Exit.Remove(line);
        if (Exit.Count is 0)
        {
            Exit.Add(new LineItem(""));
        }
    }

    /// <summary>merge or new for a proposal titled as an existing blueprint, else nothing.</summary>
    private string? SameNameChoice
    {
        get
        {
            if (!AsksSameName)
            {
                return null;
            }
            return Merge ? "merge" : "new";
        }
    }

    protected override string? Validate()
    {
        if (AsksId && !BlueprintIdPattern().IsMatch(BlueprintId.Trim()))
        {
            return "Id: letters, an optional dash and digits, e.g. BP-012.";
        }
        if (BlueprintTitle.Trim().Length is 0)
        {
            return "Title: say what it is for.";
        }
        return AsksSameName && !Merge && !Separate ? "Say whether to merge it into the blueprint titled the same or create a separate one." : null;
    }

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        var form = new BlueprintForm
        {
            Id = AsksId ? BlueprintId.Trim() : null,
            Title = BlueprintTitle.Trim(),
            Goal = Goal.Trim(),
            Exit = [.. Exit.Select(e => e.Text.Trim()).Where(e => e.Length > 0)],
            Notes = Notes.Trim(),
            Color = Color.IsAuto ? null : Color.Name,
        };
        if (_proposal is { } proposal)
        {
            await _services.Api.ApproveBlueprintAsync(
                _projectId,
                proposal.Id,
                new BlueprintApproval
                {
                    Edit = form,
                    SameName = SameNameChoice,
                },
                token
            );
            _services.Toasts.Show(IsNew ? "Blueprint created" : $"Blueprint {proposal.Changes} saved");
        }
        else if (_editing is { } id)
        {
            await _services.Api.EditBlueprintAsync(_projectId, id, form, token);
            _services.Toasts.Show($"Blueprint {id} saved");
        }
        else
        {
            await _services.Api.CreateBlueprintAsync(_projectId, form, token);
            _services.Toasts.Show($"Blueprint {form.Id} created");
        }
        _services.Live.RefreshAll();
        return true;
    }

    private async Task SuggestIdAsync()
    {
        try
        {
            var next = (await _services.Api.NextBlueprintIdAsync(_projectId, CancellationToken.None)).Id;
            if (BlueprintId.Length is 0)
            {
                BlueprintId = next;
            }
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            // the author types one
        }
    }

    [GeneratedRegex("^[A-Za-z]+-?[0-9]+$", RegexOptions.None, 1000)]
    private static partial Regex BlueprintIdPattern();
}

/// <summary>
/// Removing a blueprint that has tasks (app.js removeBlueprintDialog): they move to another blueprint (or none), or
/// are deleted with it. One without tasks is a plain confirmation.
/// </summary>
public sealed partial class RemoveBlueprintDialog : FormDialog<bool>
{
    private readonly UiServices _services;
    private readonly string _projectId;
    private readonly string _id;

    private RemoveBlueprintDialog(UiServices services, string projectId, BlueprintInfo blueprint, IReadOnlyList<BlueprintInfo> others)
    {
        _services = services;
        _projectId = projectId;
        _id = blueprint.Id;
        Name = blueprint.Title;
        Targets = [new Choice("", "no blueprint"), .. others.Select(o => new Choice(o.Id, o.Title is { Length: > 0 } t ? $"{o.Id} · {t}" : o.Id))];
        MoveTo = Targets[0];
    }

    public const string Backlog = "A backlog file that still defines it brings it back on the next import: 'harness tasks export' writes the change into the file.";

    public string? Name { get; }
    public bool HasName => Name is { Length: > 0 };
    public IReadOnlyList<Choice> Targets { get; }

    [ObservableProperty]
    public partial bool Move { get; set; } = true;

    [ObservableProperty]
    public partial bool Delete { get; set; }

    [ObservableProperty]
    public partial Choice MoveTo { get; set; }

    public static string Note =>
        "Deleting is refused while an agent holds one of them; tasks elsewhere that depend on a deleted one lose that dependency.";

    public static async Task AskAsync(UiServices services, string projectId, BlueprintInfo blueprint, IReadOnlyList<BlueprintInfo> others)
    {
        if (blueprint.Total > 0)
        {
            await services.Dialogs.ShowAsync(
                new RemoveBlueprintDialog(services, projectId, blueprint, others)
                {
                    Title = $"Remove blueprint {blueprint.Id}?",
                    Sub = $"It has {Shell.Plural(blueprint.Total, "task")}. What happens to them?",
                    AcceptLabel = "Remove the blueprint",
                    Danger = true,
                }
            );
            return;
        }
        var confirm = new ConfirmDialog
        {
            Title = $"Remove blueprint {blueprint.Id}?",
            Sub = blueprint.Title,
            Message = "• It has no tasks.\n• " + Backlog,
            AcceptLabel = "Remove the blueprint",
            Danger = true,
        };
        if (!await services.Dialogs.ConfirmAsync(confirm))
        {
            return;
        }
        try
        {
            await services.Api.RemoveBlueprintAsync(projectId, blueprint.Id, new BlueprintRemoval(), CancellationToken.None);
            services.Toasts.Show($"Blueprint {blueprint.Id} removed");
            services.Live.RefreshAll();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            services.Toasts.Fail(exception.Message);
        }
    }

    partial void OnMoveToChanged(Choice value) => Move = true;

    partial void OnMoveChanged(bool value) => Delete = !value;

    partial void OnDeleteChanged(bool value) => Move = !value;

    protected override string? Validate() => null;

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        await _services.Api.RemoveBlueprintAsync(
            _projectId,
            _id,
            Delete ? new BlueprintRemoval { Tasks = "delete" } : new BlueprintRemoval { Tasks = "move", MoveTo = MoveTo.Value },
            token
        );
        _services.Toasts.Show($"Blueprint {_id} removed");
        _services.Live.RefreshAll();
        return true;
    }
}

/// <summary>Approving an architecture proposal (app.js archProposalCard): the task's title and body, or the mark's reason.</summary>
public sealed partial class ArchApproveDialog : FormDialog<bool>
{
    private readonly UiServices _services;
    private readonly string _projectId;
    private readonly ArchProposal _proposal;

    public ArchApproveDialog(UiServices services, string projectId, ArchProposal proposal)
    {
        _services = services;
        _projectId = projectId;
        _proposal = proposal;
        TaskTitle = proposal.Title;
        Body = proposal.Body ?? "";
    }

    public bool IsTask => _proposal.Kind is "task";
    public string BodyLabel => IsTask ? "What should change and why" : "Why it is intended";

    public string Explanation
    {
        get
        {
            if (!IsTask)
            {
                return "The finding is marked and hidden unless marked ones are shown; the reason becomes a decision for agents working there.";
            }
            var from = _proposal.FindingKey is { Length: > 0 } ? ", carrying the finding's evidence and linked to it" : ", marked as the agent's observation";
            return $"An ARCH task in this project{from}; {_proposal.ProposedBy} is recorded as the proposer.";
        }
    }

    [ObservableProperty]
    public partial string TaskTitle { get; set; }

    [ObservableProperty]
    public partial string Body { get; set; }

    protected override string? Validate() => IsTask && TaskTitle.Trim().Length is 0 ? "Title: say what should be done." : null;

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        await _services.Api.ApproveArchAsync(
            _projectId,
            _proposal.Id,
            new ArchApproval { Title = IsTask ? TaskTitle.Trim() : "", Body = Body.Trim() },
            token
        );
        _services.Toasts.Show(IsTask ? "Task created" : "Marked by design");
        _services.Live.RefreshAll();
        return true;
    }
}

/// <summary>Rejecting a proposal of any kind, with why not (optional).</summary>
public sealed partial class RejectDialog(UiServices services, string projectId, string kind, string proposalId) : FormDialog<bool>
{
    [ObservableProperty]
    public partial string Note { get; set; } = "";

    protected override string? Validate() => null;

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        await services.Api.RejectAsync(projectId, kind, proposalId, Note.Trim(), token);
        services.Toasts.Show("Rejected");
        services.Live.RefreshAll();
        return true;
    }
}
