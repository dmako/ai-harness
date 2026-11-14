using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Navigation;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>The memory types as the page names, explains and colours them (app.js MEMORY_GROUPS, MEMORY_TYPE_HELP).</summary>
public static partial class MemoryTypes
{
    public static IReadOnlyList<string> All => HarnessApi.MemoryTypes;

    public static string Group(string type) =>
        type switch
        {
            "goal" => "Goals",
            "constraint" => "Constraints",
            "decision" => "Decisions",
            "convention" => "Conventions",
            "warning" => "Warnings",
            "fact" => "Facts",
            "open_question" => "Open questions",
            _ => type,
        };

    public static string One(string type) => Group(type) is var g && g.EndsWith('s', StringComparison.Ordinal) ? g[..^1] : g;

    /// <summary>What the type is for, an example, and where it reaches agents.</summary>
    public static (string Meaning, string Example, string Reach) Help(string type) =>
        type switch
        {
            "goal" => ("What the work is for: an outcome to optimize toward.", "Optimize code intelligence for C#/.NET first.", "goals_current, and the top goals in every get_context"),
            "constraint" => ("A hard limit set from outside: what must or must not happen, not ours to change.", "Keycloak must not be changed for now.", "get_context, decisions_for and constraints_current"),
            "decision" => ("A choice made between alternatives, with its reason: why the design is this way.", "Live updates use Server-Sent Events, not WebSocket: the page only needs to know what changed.", "get_context and decisions_for"),
            "convention" => ("How code and work are done here, in every new change.", "CancellationToken is always the last parameter of an async method.", "get_context and decisions_for; offered to other projects as a convention they may adopt"),
            "warning" => ("A trap: what breaks or misleads, and how to avoid it.", "graphify-csharp ignores SIGTERM: stop its sessions by id.", "get_context and decisions_for"),
            "fact" => ("Something true that the code does not show: who uses what, where data comes from.", "Tenant GUIDs originate in a Confluence table.", "get_context, where it applies or matches the task, and memory_search"),
            _ => ("Not decided yet: what is open and what it waits for.", "Do roles exist on the organization server, and who approves?", "get_context, where it applies or matches the task (an agent asks rather than decides it), and memory_search"),
        };

    /// <summary>The type's colour token.</summary>
    public static string Brush(string type) =>
        type switch
        {
            "goal" => "CGoal",
            "constraint" => "CConstraint",
            "decision" => "CDecision",
            "convention" => "CConvention",
            "warning" => "CWarning",
            "fact" => "CFact",
            _ => "COpenQuestion",
        };

    /// <summary>A level's words: "all your projects" or "workspace &lt;name&gt;".</summary>
    public static string LevelWord(string level) => level is "user" ? "all your projects" : "workspace " + level["workspace:".Length..];

    public static bool IsLevel(string scope) => scope is "user" || scope.StartsWith("workspace:", StringComparison.Ordinal);

    /// <summary>A statement's point and the rest (app.js leadOf): up to the first ": ", "; " or sentence end.</summary>
    public static (string Lead, string Remainder) Lead(string text)
    {
        var t = text.Trim();
        var m = LeadPattern().Match(t);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (t, "");
    }

    [GeneratedRegex(@"^(.{12,140}?[.:;])\s+(\S[\s\S]*)$", RegexOptions.None, 1000)]
    private static partial Regex LeadPattern();
}

/// <summary>A memory type to pick in a form: its words and what it is for.</summary>
public sealed record TypeChoice(string Type)
{
    public string Name => MemoryTypes.One(Type);
    public string Meaning => MemoryTypes.Help(Type).Meaning;
    public string Label => $"{MemoryTypes.One(Type)}: {MemoryTypes.Help(Type).Meaning}";
    public string Example => "e.g. " + MemoryTypes.Help(Type).Example;
    public string Reach => $"Reaches agents: {MemoryTypes.Help(Type).Reach}.";
    public override string ToString() => Label;
}

/// <summary>A type's segment of the type bar: its count and whether its memories are shown.</summary>
public sealed partial class TypeFilter(string type, Action changed) : ObservableObject
{
    public string Type { get; } = type;
    public string Label => MemoryTypes.Group(Type);
    public string Brush => MemoryTypes.Brush(Type);
    public string Tip => $"{Label}: {Count}. {MemoryTypes.Help(Type).Meaning}";

    [ObservableProperty]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial bool On { get; set; } = true;

    partial void OnOnChanged(bool value) => changed();
}

/// <summary>A history entry of a memory: a version, or an event of its trail.</summary>
public sealed record HistoryLine(string Text, string? Code, string? Detail, bool This)
{
    public bool HasCode => Code is { Length: > 0 };
    public bool HasDetail => Detail is { Length: > 0 };
}

/// <summary>
/// A memory as a card (app.js memoryCard): its point first, provenance, what it overrides or is overridden by, what
/// superseded it, and its actions (Correct, Retire, Promote, Share or Withdraw, History) as the memory allows.
/// </summary>
public sealed partial class MemoryCard(Memory memory, KnowledgeActions actions, bool onLevel) : ObservableObject
{
    public Memory Memory { get; } = memory;
    public KnowledgeActions Actions { get; } = actions;
    public string Lead => MemoryTypes.Lead(Memory.Content).Lead;
    public string Rest => MemoryTypes.Lead(Memory.Content).Remainder;
    public bool HasRest => Rest.Length > 0;
    public string Brush => MemoryTypes.Brush(Memory.Type);
    public string Level => MemoryTypes.IsLevel(Memory.Scope) ? Memory.Scope : "";
    public bool IsLevel => Level.Length > 0;

    public bool Inactive =>
        Memory.SupersededBy is { Length: > 0 }
        || (
            DateTimeOffset.TryParse(Memory.ValidUntil, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until)
            && until < DateTimeOffset.UtcNow
        );

    public bool IsShared => Memory.Shared is "project";
    public bool IsRemoved => Memory.Shared is "removed";
    public bool ShowsScope => !onLevel;

    public string ScopeText =>
        Memory.Scope switch
        {
            "user" => "you · all your projects",
            var s when s.StartsWith("workspace:", StringComparison.Ordinal) => "workspace · " + s["workspace:".Length..],
            var s => s,
        };

    /// <summary>Who, from where, when, how sure and until when, as one line.</summary>
    public string Provenance
    {
        get
        {
            var parts = new List<string> { Memory.SourceType is "user" ? "stated by you" : "recorded by an agent" };
            if (Memory.SourceRef is { Length: > 0 } source && source is not "unknown")
            {
                parts.Add(source);
            }
            parts.Add(Shell.Ago(Memory.CreatedAt));
            if (Memory.Confidence is { } confidence)
            {
                parts.Add("confidence " + confidence.ToString(CultureInfo.InvariantCulture));
            }
            if (Memory.ValidUntil is { Length: >= 10 } valid)
            {
                parts.Add("valid until " + valid[..10]);
            }
            return string.Join(" · ", parts);
        }
    }

    public string? OverridesText => Memory.Overrides.Count > 0 ? "Takes precedence here over " + string.Join(", ", Memory.Overrides) : null;
    public string? OverriddenText => Memory.OverriddenIn.Count > 0 ? "Overridden by their own memory in: " + string.Join(", ", Memory.OverriddenIn) : null;

    public string? SupersededText
    {
        get
        {
            if (Memory.SupersededBy is not { Length: > 0 } by)
            {
                return null;
            }
            return Memory.SupersedeReason is { Length: > 0 } reason ? $"Superseded by {by}: {reason}" : $"Superseded by {by}";
        }
    }

    public bool CanChange => !Inactive;
    public bool CanPromote => !Inactive && !IsLevel && Actions.InProject && Memory.Scope is "project";
    public bool CanShare => !Inactive && !IsLevel && Actions.InProject && !IsRemoved;
    public string ShareLabel => IsShared ? "Withdraw from project" : "Share with project";

    public string ShareTip =>
        IsShared ? "Take it out of the team's knowledge; it stays as your local memory" : "Share it with the team through .harness/knowledge.toml";

    public ObservableCollection<HistoryLine> Versions { get; } = [];
    public ObservableCollection<HistoryLine> Trail { get; } = [];

    [ObservableProperty]
    public partial bool HistoryShown { get; private set; }

    [ObservableProperty]
    public partial string? HistoryError { get; private set; }

    public bool HasVersions => Versions.Count > 1;

    [RelayCommand]
    private async Task HistoryAsync()
    {
        if (HistoryShown)
        {
            HistoryShown = false;
            return;
        }
        try
        {
            var detail = await Actions.DetailAsync(Memory);
            Lists.Sync(Versions, detail.History.Select(v => new HistoryLine(v.Content, v.Id, null, v.Id == detail.Memory.Id)));
            Lists.Sync(
                Trail,
                detail.Events.Select(e => new HistoryLine($"{e.Event} · {e.Actor} · {Shell.Ago(e.At)}", e.RelatedId, e.Detail, false))
            );
            OnPropertyChanged(nameof(HasVersions));
            HistoryError = null;
            HistoryShown = true;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            HistoryError = exception.Message;
        }
    }

    [RelayCommand]
    private Task CorrectAsync() => Actions.CorrectAsync(Memory);

    [RelayCommand]
    private Task RetireAsync() => Actions.RetireAsync(Memory);

    [RelayCommand]
    private Task PromoteAsync() => Actions.PromoteAsync(Memory);

    [RelayCommand]
    private Task ShareAsync() => Actions.ShareAsync(Memory);
}

/// <summary>A type's group of cards (app.js .mgroup).</summary>
public sealed record MemoryGroup(string Type, IReadOnlyList<MemoryCard> Cards)
{
    public string Title => MemoryTypes.Group(Type);
    public string Brush => MemoryTypes.Brush(Type);
    public string Tip => MemoryTypes.Help(Type).Meaning;
    public int Count => Cards.Count;

    public bool Equals(MemoryGroup? other) => other is not null && Type == other.Type && Cards.SequenceEqual(other.Cards);

    public override int GetHashCode() => HashCode.Combine(Type, Cards.Count);
}

/// <summary>
/// The writes on memories, wherever a card is (a project's Knowledge, its Inbox, a level's page): each opens its
/// form, sends, and reloads every section. A level's memory is read and changed at its level.
/// </summary>
public sealed class KnowledgeActions(UiServices services, string? projectId, IReadOnlyList<string> workspaces)
{
    public UiServices Services { get; } = services;
    public string? ProjectId { get; } = projectId;
    public bool InProject => ProjectId is not null;

    public Task<MemoryDetail> DetailAsync(Memory memory) =>
        Services.Api.MemoryAsync(ProjectId, memory.Id, MemoryTypes.IsLevel(memory.Scope) ? memory.Scope : null, CancellationToken.None);

    public Task CorrectAsync(Memory memory) =>
        Services.Dialogs.ShowAsync(
            new MemoryChangeDialog(this, memory, correct: true)
            {
                Title = "Correct this memory",
                Sub = "The new statement replaces it (same type and scope); the old one stays in its history.",
                Wide = true,
                AcceptLabel = "Save correction",
            }
        );

    public Task RetireAsync(Memory memory) =>
        Services.Dialogs.ShowAsync(
            new MemoryChangeDialog(this, memory, correct: false)
            {
                Title = "Retire this memory",
                Sub = "It stops applying now. Nothing is deleted; it stays readable with its history.",
                AcceptLabel = "Retire",
                Danger = true,
            }
        );

    public Task PromoteAsync(Memory memory) =>
        Services.Dialogs.ShowAsync(
            new PromoteDialog(this, memory, [.. workspaces.Select(w => new Choice("workspace:" + w, "Workspace " + w)), new Choice("user", "All your projects")])
            {
                Title = "Promote",
                Sub = "Record this for more than this project. Where should it apply?",
                Wide = true,
                AcceptLabel = "Review",
            }
        );

    public async Task ShareAsync(Memory memory)
    {
        var shared = memory.Shared is "project";
        var confirm = new ConfirmDialog
        {
            Title = shared ? "Withdraw from project knowledge?" : "Share with the project?",
            Message = shared
                ? "harness removes it from .harness/knowledge.toml now and keeps it as your local memory. Commit the file to withdraw it for the team."
                : "harness writes it to .harness/knowledge.toml now. Commit the file to share it with the team; their harness picks it up after their next pull.",
            AcceptLabel = shared ? "Withdraw" : "Share",
        };
        if (!await Services.Dialogs.ConfirmAsync(confirm))
        {
            return;
        }
        try
        {
            var result = await Services.Api.ShareAsync(ProjectId!, memory.Id, !shared, CancellationToken.None);
            if (result.Warning is { Length: > 0 } warning)
            {
                Services.Toasts.Fail("Shared here, but Git ignores the file", $"{warning}. Fix: {result.Fix}");
            }
            else
            {
                Services.Toasts.Show(shared ? "Withdrawn from project knowledge" : "Shared with the project");
            }
            Services.Live.RefreshAll();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Services.Toasts.Fail(exception.Message);
        }
    }

    /// <summary>Adds the author's statement for the project (with its scope) or a level.</summary>
    public Task AddAsync(string? level) =>
        Services.Dialogs.ShowAsync(
            new MemoryAddDialog(this, level)
            {
                Title = level is null ? "Add knowledge" : "Add for " + MemoryTypes.LevelWord(level),
                Sub = level is null
                    ? "Add knowledge for this project, as yours: agents get it where its type and scope apply."
                    : "It applies to every one of them unless a project states otherwise.",
                Wide = true,
                AcceptLabel = "Add",
            }
        );
}

/// <summary>Correcting (a new statement and why) or retiring (why) a memory.</summary>
public sealed partial class MemoryChangeDialog : FormDialog<bool>
{
    private readonly KnowledgeActions _actions;
    private readonly Memory _memory;

    public MemoryChangeDialog(KnowledgeActions actions, Memory memory, bool correct)
    {
        _actions = actions;
        _memory = memory;
        Correcting = correct;
        Content = memory.Content;
    }

    public bool Correcting { get; }
    public string ReasonLabel => Correcting ? "Why" : "Why it no longer applies";

    [ObservableProperty]
    public partial string Content { get; set; }

    [ObservableProperty]
    public partial string Reason { get; set; } = "";

    protected override string? Validate()
    {
        if (Correcting && Content.Trim().Length is 0)
        {
            return "Corrected statement is required.";
        }
        return Reason.Trim().Length is 0 ? ReasonLabel + " is required." : null;
    }

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        var level = MemoryTypes.IsLevel(_memory.Scope) ? _memory.Scope : null;
        await _actions.Services.Api.ChangeMemoryAsync(
            _actions.ProjectId,
            _memory.Id,
            new MemoryChange { Content = Correcting ? Content.Trim() : null, Reason = Reason.Trim(), Level = level },
            token
        );
        _actions.Services.Toasts.Show(Correcting ? "Corrected" : "Retired");
        _actions.Services.Live.RefreshAll();
        return true;
    }
}

/// <summary>
/// Promoting a project memory (app.js promoteForm): where to, then a review of where it already holds (a dry run)
/// before it is recorded at the level and this project's copy retired.
/// </summary>
public sealed partial class PromoteDialog : DialogViewModel<bool>
{
    private readonly KnowledgeActions _actions;
    private readonly Memory _memory;

    public PromoteDialog(KnowledgeActions actions, Memory memory, IReadOnlyList<Choice> targets)
    {
        _actions = actions;
        _memory = memory;
        Targets = targets;
        To = targets[0];
    }

    public IReadOnlyList<Choice> Targets { get; }

    [ObservableProperty]
    public partial Choice To { get; set; }

    /// <summary>First the review (a dry run: where it already holds, nothing written), then the promotion.</summary>
    protected override async Task AcceptAsync()
    {
        if (Busy)
        {
            return;
        }
        Busy = true;
        Error = null;
        try
        {
            if (!Reviewing)
            {
                var dry = await _actions.Services.Api.PromoteAsync(
                    _actions.ProjectId!,
                    _memory.Id,
                    new Promotion { To = To.Value, DryRun = true },
                    CancellationToken.None
                );
                var holds = dry.HoldsIn.Count > 0
                    ? $"Already holds in: {string.Join(", ", dry.HoldsIn.Select(h => h.Project))} (their own copies keep precedence there until retired)"
                    : "No other project records it yet.";
                var kind = To.Value is "user" ? "user" : "workspace";
                ReviewText = string.Join(
                    "\n\n",
                    $"Promote to {MemoryTypes.LevelWord(To.Value)}?",
                    _memory.Content,
                    holds,
                    $"This project's copy is retired; the {kind} one applies here from now on. Recorded as stated by you."
                );
                Reviewing = true;
                return;
            }
            var result = await _actions.Services.Api.PromoteAsync(_actions.ProjectId!, _memory.Id, new Promotion { To = To.Value }, CancellationToken.None);
            if (result.Status is "needs_decision")
            {
                Error = "That level already holds something similar: " + string.Join("; ", result.SimilarAtLevel);
                return;
            }
            _actions.Services.Toasts.Show("Promoted to " + MemoryTypes.LevelWord(To.Value));
            _actions.Services.Live.RefreshAll();
            Close(true);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}

/// <summary>Adding the author's statement (app.js "Add knowledge", levelKnowledge's Add): type, statement, scope, source.</summary>
public sealed partial class MemoryAddDialog : FormDialog<bool>
{
    private readonly KnowledgeActions _actions;

    public MemoryAddDialog(KnowledgeActions actions, string? level)
    {
        _actions = actions;
        Level = level;
        Types = [.. MemoryTypes.All.Select(t => new TypeChoice(t))];
        Type = Types.First(t => t.Type == (level is null ? "decision" : "convention"));
    }

    public string? Level { get; }
    public bool AsksScope => Level is null;
    public IReadOnlyList<TypeChoice> Types { get; }

    [ObservableProperty]
    public partial TypeChoice Type { get; set; }

    [ObservableProperty]
    public partial string Content { get; set; } = "";

    [ObservableProperty]
    public partial string Scope { get; set; } = "project";

    [ObservableProperty]
    public partial string SourceRef { get; set; } = "";

    private string ScopeOrProject => Scope.Trim().Length > 0 ? Scope.Trim() : "project";

    protected override string? Validate() => Content.Trim().Length is 0 ? "Statement is required." : null;

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        var added = await _actions.Services.Api.AddMemoryAsync(
            _actions.ProjectId,
            new MemoryAdd
            {
                Type = Type.Type,
                Content = Content.Trim(),
                Scope = Level is null ? ScopeOrProject : null,
                Level = Level,
                SourceRef = SourceRef.Trim().Length > 0 ? SourceRef.Trim() : null,
            },
            token
        );
        if (added.Status is "needs_decision")
        {
            var similar = string.Join("; ", (added.Similar ?? []).Select(m => m.Content));
            throw new HarnessApiException($"Something similar is already recorded: {similar} (correct that one instead)");
        }
        _actions.Services.Toasts.Show("Added");
        _actions.Services.Live.RefreshAll();
        return true;
    }
}

/// <summary>
/// A project's Knowledge (app.js "knowledge": viewKnowledge, viewProposals): its memories, or its Inbox of proposals
/// and memories a Git change took out, by the address (#/p/&lt;id&gt;/knowledge[/inbox|/&lt;scope&gt;]).
/// </summary>
public sealed partial class KnowledgeViewModel : ObservableObject, IDisposable
{
    private readonly UiServices _services;
    private readonly string _id;
    private ProjectInfo _project;

    public KnowledgeViewModel(UiServices services, ProjectInfo project, Route route)
    {
        _services = services;
        _id = project.Id;
        _project = project;
        var actions = new KnowledgeActions(services, project.Id, project.Workspaces);
        Memories = new ProjectMemories(services, project.Id, actions);
        Inbox = new MemoryInbox(services, project.Id, actions);
        Show(route);
    }

    public ProjectMemories Memories { get; }
    public MemoryInbox Inbox { get; }
    public ObservableCollection<TabItem> Sections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMemories))]
    public partial bool ShowsInbox { get; private set; }

    public bool ShowsMemories => !ShowsInbox;

    public void Start()
    {
        Memories.Follow(_services.Live);
        Inbox.Follow(_services.Live);
        _ = Memories.LoadAsync();
        _ = Inbox.LoadAsync();
    }

    /// <summary>Memories or Inbox by the address; another item is a scope for "What applies here?".</summary>
    public void Show(Route route)
    {
        var item = route.Item ?? "";
        ShowsInbox = item is "inbox";
        if (item is not ("" or "inbox" or "memories"))
        {
            Memories.Applies.Scope = item;
            _ = Memories.Applies.ShowCommand.ExecuteAsync(null);
        }
        Lists.Sync(
            Sections,
            [
                new TabItem("memories", "Memories", Route.ProjectHref(_id, "knowledge"), 0, !ShowsInbox),
                new TabItem("inbox", "Inbox", Route.ProjectHref(_id, "knowledge", "inbox"), _project.Live.KnowledgeWaiting, ShowsInbox),
            ]
        );
    }

    public void Update(ProjectInfo project, Route route)
    {
        _project = project;
        Show(route);
    }

    public void Dispose()
    {
        Memories.Dispose();
        Inbox.Dispose();
    }
}

/// <summary>"What applies here?" (app.js appliesPanel): what an agent is told about a path, subsystem or branch.</summary>
public sealed partial class AppliesPanel(UiServices services, string projectId, KnowledgeActions actions) : ObservableObject
{
    [ObservableProperty]
    public partial string Scope { get; set; } = "";

    [ObservableProperty]
    public partial string? Heading { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public ObservableCollection<MemoryGroup> Groups { get; } = [];

    public bool HasResult => Heading is not null;

    [RelayCommand]
    private async Task ShowAsync()
    {
        var scope = Scope.Trim();
        if (scope.Length is 0)
        {
            Clear();
            return;
        }
        if (!ScopePrefix().IsMatch(scope))
        {
            scope = "path:" + scope;
        }
        try
        {
            var a = await services.Api.AppliesAsync(projectId, scope, CancellationToken.None);
            Heading = $"What an agent is told about {a.Scope}" + (a.Branch is { Length: > 0 } b ? $" on branch {b}" : "") + ":";
            Lists.Sync(
                Groups,
                [
                    new MemoryGroup("goal", [.. a.Goals.Select(m => new MemoryCard(m, actions, false))]),
                    new MemoryGroup("constraint", [.. a.Constraints.Select(m => new MemoryCard(m, actions, false))]),
                    new MemoryGroup("decision", [.. a.Decisions.Select(m => new MemoryCard(m, actions, false))]),
                ]
            );
            Error = null;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
        OnPropertyChanged(nameof(HasResult));
    }

    [RelayCommand]
    private void Clear()
    {
        Scope = "";
        Heading = null;
        Groups.Clear();
        OnPropertyChanged(nameof(HasResult));
    }

    [GeneratedRegex("^(path|subsystem|branch|project)(:|$)", RegexOptions.None, 1000)]
    private static partial Regex ScopePrefix();
}

/// <summary>
/// A project's memories (app.js viewKnowledge): every type read, the chosen shown, grouped by type; the type bar
/// toggles types, a search (debounced), a scope picker and superseded ones on request; Add knowledge.
/// </summary>
public sealed partial class ProjectMemories : SectionViewModel
{
    private readonly UiServices _services;
    private IReadOnlyList<Memory> _memories = [];
    private CancellationTokenSource? _typing;
    private bool _drawing;

    public ProjectMemories(UiServices services, string projectId, KnowledgeActions actions)
        : base("", projectId, ["knowledge", "levels"])
    {
        _services = services;
        Actions = actions;
        Applies = new AppliesPanel(services, projectId, actions);
        Types = [.. MemoryTypes.All.Select(t => new TypeFilter(t, Draw))];
    }

    public KnowledgeActions Actions { get; }
    public AppliesPanel Applies { get; }
    public IReadOnlyList<TypeFilter> Types { get; }
    public ObservableCollection<MemoryGroup> Groups { get; } = [];
    public ObservableCollection<Choice> Scopes { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool IncludeInactive { get; set; }

    [ObservableProperty]
    public partial Choice? Scope { get; set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string? Empty { get; private set; }

    /// <summary>The type bar's segments with memories, as wide as their share.</summary>
    public IEnumerable<TypeFilter> Bar => Types.Where(t => t.Count > 0);

    partial void OnQueryChanged(string value)
    {
        _typing?.Cancel();
        _typing?.Dispose();
        _typing = new CancellationTokenSource();
        var token = _typing.Token;
        _ = Task.Delay(250, token).ContinueWith(
            _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RefreshAsync(force: true)),
            token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default
        );
    }

    partial void OnIncludeInactiveChanged(bool value) => _ = RefreshAsync(force: true);

    partial void OnScopeChanged(Choice? value)
    {
        if (!_drawing)
        {
            Draw();
        }
    }

    [RelayCommand]
    private Task AddAsync() => Actions.AddAsync(null);

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        _memories = (await _services.Api.MemoriesAsync(ProjectId!, Query.Trim(), IncludeInactive, token)).Memories;
        Draw();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _typing?.Cancel();
            _typing?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Draw()
    {
        var scopes = _memories
            .Select(m => m.Scope)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s is "project" ? 0 : 1)
            .ThenBy(s => s, StringComparer.Ordinal)
            .ToArray();
        _drawing = true;
        var chosen = Scope?.Value ?? "";
        if (chosen.Length > 0 && !scopes.Contains(chosen))
        {
            chosen = "";
        }
        Lists.Sync(Scopes, new[] { new Choice("", $"all scopes ({scopes.Length})") }.Concat(scopes.Select(s => new Choice(s, s))));
        Scope = Scopes.First(c => c.Value == chosen);
        _drawing = false;
        var inScope = _memories.Where(m => chosen.Length is 0 || m.Scope == chosen).ToArray();
        foreach (var type in Types)
        {
            type.Count = inScope.Count(m => m.Type == type.Type);
        }
        OnPropertyChanged(nameof(Bar));
        var on = Types.Where(t => t.On).Select(t => t.Type).ToHashSet(StringComparer.Ordinal);
        var shown = inScope.Where(m => on.Contains(m.Type)).ToArray();
        Summary = (shown.Length is 1 ? "1 memory" : $"{shown.Length} memories") + (chosen.Length > 0 ? " in " + chosen : "");
        if (on.Count is 0)
        {
            Empty = "Pick at least one type.";
        }
        else
        {
            Empty = shown.Length is 0 ? "No memories match." : null;
        }
        Lists.Sync(
            Groups,
            MemoryTypes.All.Where(t => shown.Any(m => m.Type == t))
                .Select(t => new MemoryGroup(t, [.. shown.Where(m => m.Type == t).Select(m => Card(m))]))
        );
    }

    /// <summary>A card, the one already shown kept when its memory did not change (its history stays open).</summary>
    private MemoryCard Card(Memory memory) =>
        Groups.SelectMany(g => g.Cards).FirstOrDefault(c => c.Memory.Id == memory.Id && Same(c.Memory, memory)) ?? new MemoryCard(memory, Actions, false);

    private static bool Same(Memory a, Memory b) =>
        a.Content == b.Content && a.SupersededBy == b.SupersededBy && a.Shared == b.Shared && a.Scope == b.Scope && a.ValidUntil == b.ValidUntil;
}

/// <summary>A memory a Git change took out of project knowledge (app.js removedCard): keep it local or retire it.</summary>
public sealed record RemovedCard(Memory Memory)
{
    public string Lead => MemoryTypes.Lead(Memory.Content).Lead;
    public string Rest => MemoryTypes.Lead(Memory.Content).Remainder;
    public string Brush => MemoryTypes.Brush(Memory.Type);
    public string Meta => $"removed from project · {Memory.Scope} · {MemoryTypes.Group(Memory.Type)}";
}

/// <summary>A memory proposal (app.js proposalCard): what, why, who, what it resembles, and its review.</summary>
public sealed record ProposalItem(MemoryProposal Proposal)
{
    public string Brush => MemoryTypes.Brush(Proposal.Type);
    public string Kind => MemoryTypes.One(Proposal.Type) + (Proposal.Status is "pending" ? "" : $" · {Decision}");
    public bool Pending => Proposal.Status is "pending";
    public bool Rejected => Proposal.Status is "rejected";
    public bool HasRationale => Proposal.Rationale is { Length: > 0 };

    private string Decision =>
        (Proposal.Status is "approved" ? "✓ " : "✕ ")
        + Proposal.Status
        + (Proposal.DecidedBy is { Length: > 0 } by ? " by " + by : "")
        + (Proposal.DecisionNote is { Length: > 0 } note ? ": " + note : "");

    public string Provenance
    {
        get
        {
            var parts = new List<string> { Proposal.Scope };
            if (Proposal.SourceRef is { Length: > 0 } source)
            {
                parts.Add("source " + source);
            }
            parts.Add("proposed by " + (Proposal.ProposedBy is { Length: > 0 } by ? by : "unknown"));
            parts.Add(Shell.Ago(Proposal.CreatedAt));
            if (Proposal.Supersedes is { Length: > 0 } supersedes)
            {
                parts.Add("would supersede " + supersedes);
            }
            if (Proposal.MemoryId is { Length: > 0 } memory)
            {
                parts.Add("memory " + memory);
            }
            return string.Join(" · ", parts);
        }
    }

    public IReadOnlyList<string> Similar =>
        [.. Proposal.Similar.Select(s => $"{Math.Round(s.Similarity * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}% · {s.Memory.Content}")];

    public bool HasSimilar => Proposal.Similar.Count > 0;
}

/// <summary>
/// A project's Knowledge Inbox (app.js viewProposals): memories a Git change took out of project knowledge (keep or
/// retire), then the memory proposals by status with Approve, Edit &amp; approve and Reject; approving one that
/// resembles current memories asks which to replace, or to keep both.
/// </summary>
public sealed partial class MemoryInbox : SectionViewModel
{
    private readonly UiServices _services;

    public MemoryInbox(UiServices services, string projectId, KnowledgeActions actions)
        : base("", projectId, ["proposals", "knowledge"])
    {
        _services = services;
        Actions = actions;
        Hint = "Memories agents proposed but nobody confirmed yet. They are not active until approved; approving or rejecting here is recorded as you.";
        StatusChips = [.. new[] { "pending", "approved", "rejected", "all" }.Select(s => new FilterChip(s, s, 0, Chosen) { On = s is "pending" })];
    }

    public KnowledgeActions Actions { get; }
    public IReadOnlyList<FilterChip> StatusChips { get; }
    public ObservableCollection<RemovedCard> Removed { get; } = [];
    public ObservableCollection<ProposalItem> Proposals { get; } = [];

    [ObservableProperty]
    public partial string Status { get; private set; } = "pending";

    [ObservableProperty]
    public partial bool InboxZero { get; private set; }

    [ObservableProperty]
    public partial string? Empty { get; private set; }

    public bool HasRemoved => Removed.Count > 0;

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var removed = _services.Api.MemoriesAsync(ProjectId!, null, false, token, removed: true);
        var proposals = (await _services.Api.MemoryProposalsAsync(ProjectId!, Status, token)).Proposals;
        Lists.Sync(Removed, (await removed).Memories.Select(m => new RemovedCard(m)));
        Lists.Sync(Proposals, proposals.Select(p => new ProposalItem(p)));
        InboxZero = proposals.Count is 0 && Status is "pending";
        var which = Status is "all" ? "" : Status + " ";
        Empty = proposals.Count is 0 && Status is not "pending" ? $"No {which}proposals." : null;
        OnPropertyChanged(nameof(HasRemoved));
    }

    private void Chosen(FilterChip chip)
    {
        if (!chip.On || chip.Key == Status)
        {
            return;
        }
        Status = chip.Key;
        foreach (var other in StatusChips.Where(c => c != chip))
        {
            other.On = false;
        }
        _ = RefreshAsync(force: true);
    }

    [RelayCommand]
    private async Task ApproveAsync(ProposalItem item) => await ApproveWithAsync(item.Proposal, new ProposalApproval());

    [RelayCommand]
    private async Task EditAndApproveAsync(ProposalItem item) =>
        await _services.Dialogs.ShowAsync(
            new ProposalEditDialog(this, item.Proposal)
            {
                Title = "Edit the statement, then approve it",
                Wide = true,
                AcceptLabel = "Approve edited",
            }
        );

    [RelayCommand]
    private async Task RejectAsync(ProposalItem item) =>
        await _services.Dialogs.ShowAsync(
            new MemoryRejectDialog(_services, ProjectId!, item.Proposal.Id)
            {
                Title = "Reject it?",
                Sub = item.Proposal.Content,
                AcceptLabel = "Reject",
                Danger = true,
            }
        );

    [RelayCommand]
    private async Task KeepAsync(RemovedCard card)
    {
        try
        {
            await _services.Api.KeepMemoryAsync(ProjectId!, card.Memory.Id, CancellationToken.None);
            _services.Toasts.Show("Kept as local");
            _services.Live.RefreshAll();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _services.Toasts.Fail(exception.Message);
        }
    }

    [RelayCommand]
    private async Task RetireRemovedAsync(RemovedCard card)
    {
        try
        {
            await _services.Api.ChangeMemoryAsync(
                ProjectId,
                card.Memory.Id,
                new MemoryChange { Reason = "removed from project knowledge" },
                CancellationToken.None
            );
            _services.Toasts.Show("Retired");
            _services.Live.RefreshAll();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _services.Toasts.Fail(exception.Message);
        }
    }

    /// <summary>Approves; one that resembles current memories asks which to replace, or to keep both.</summary>
    public async Task<bool> ApproveWithAsync(MemoryProposal proposal, ProposalApproval approval)
    {
        try
        {
            var result = await _services.Api.ApproveMemoryAsync(ProjectId!, proposal.Id, approval, CancellationToken.None);
            if (result.Status is "needs_decision")
            {
                var decide = new SimilarDecisionDialog(result.Similar ?? [])
                {
                    Title = "This resembles current memories",
                    Sub = "Replace one of them with it, or keep both:",
                    Wide = true,
                    AcceptLabel = "Keep both",
                };
                var chosen = await _services.Dialogs.ShowAsync(decide);
                if (chosen is null)
                {
                    return false;
                }
                return await ApproveWithAsync(
                    proposal,
                    chosen.Length is 0
                        ? new ProposalApproval { Content = approval.Content, KeepBoth = true }
                        : new ProposalApproval { Content = approval.Content, Supersedes = [chosen] }
                );
            }
            _services.Toasts.Show("Approved");
            _services.Live.RefreshAll();
            return true;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _services.Toasts.Fail(exception.Message);
            return false;
        }
    }
}

/// <summary>A proposal's statement edited before approving it.</summary>
public sealed partial class ProposalEditDialog(MemoryInbox inbox, MemoryProposal proposal) : FormDialog<bool>
{
    [ObservableProperty]
    public partial string Content { get; set; } = proposal.Content;

    protected override string? Validate() => Content.Trim().Length is 0 ? "Statement is required." : null;

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        Close(true);
        await inbox.ApproveWithAsync(proposal, new ProposalApproval { Content = Content.Trim() });
        return true;
    }
}

/// <summary>Which current memory a resembling proposal replaces: one of them (its id), or keep both ("").</summary>
public sealed partial class SimilarDecisionDialog(IReadOnlyList<SimilarMemory> similar) : DialogViewModel<string>
{
    public IReadOnlyList<SimilarMemory> Similar { get; } = similar;

    [RelayCommand]
    private void Replace(SimilarMemory memory) => Close(memory.Memory.Id);

    protected override Task AcceptAsync()
    {
        Close("");
        return Task.CompletedTask;
    }
}

/// <summary>Rejecting a memory proposal, with a note (optional).</summary>
public sealed partial class MemoryRejectDialog(UiServices services, string projectId, string proposalId) : FormDialog<bool>
{
    [ObservableProperty]
    public partial string Note { get; set; } = "";

    protected override string? Validate() => null;

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        await services.Api.RejectMemoryAsync(projectId, proposalId, Note.Trim(), token);
        services.Toasts.Show("Rejected");
        services.Live.RefreshAll();
        return true;
    }
}

/// <summary>
/// A level's knowledge (app.js levelKnowledge): the user's (#/knowledge) or a workspace's, its memories by type with
/// the projects that override each, and Add; with nothing yet, what each type is for.
/// </summary>
public sealed partial class LevelKnowledgeViewModel : SectionViewModel
{
    private readonly UiServices _services;

    public LevelKnowledgeViewModel(UiServices services, string level, string? intro)
        : base("", null, ["levels"])
    {
        _services = services;
        Level = level;
        Hint = intro;
        Actions = new KnowledgeActions(services, null, []);
    }

    public string Level { get; }
    public KnowledgeActions Actions { get; }
    public ObservableCollection<MemoryGroup> Groups { get; } = [];
    public IReadOnlyList<TypeChoice> TypeHelp { get; } = [.. MemoryTypes.All.Select(t => new TypeChoice(t))];

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [RelayCommand]
    private Task AddAsync() => Actions.AddAsync(Level);

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var memories = (await _services.Api.LevelAsync(Level, token)).Memories;
        Lists.Sync(
            Groups,
            MemoryTypes.All.Where(t => memories.Any(m => m.Type == t))
                .Select(t => new MemoryGroup(t, [.. memories.Where(m => m.Type == t).Select(m => new MemoryCard(m, Actions, onLevel: true))]))
        );
        IsEmpty = memories.Count is 0;
    }
}

/// <summary>#/knowledge: the user's own knowledge, for every project on this machine (app.js renderUserKnowledge).</summary>
public sealed class UserKnowledgePage : PageViewModel
{
    public UserKnowledgePage(UiServices services)
    {
        Knowledge = new LevelKnowledgeViewModel(services, "user", null);
        Knowledge.Follow(services.Live);
        _ = Knowledge.LoadAsync();
    }

    public LevelKnowledgeViewModel Knowledge { get; }
    public static string Intro => "Knowledge for every project of yours on this machine; a workspace's or a project's own statement takes precedence.";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Knowledge.Dispose();
        }
        base.Dispose(disposing);
    }
}
