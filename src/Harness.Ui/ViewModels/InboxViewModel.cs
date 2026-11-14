using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>A proposal as the Inbox shows it: its kind and tags, title, the why, Done when, provenance and its review.</summary>
public sealed record ProposalCard(
    string Kind,
    string Id,
    string Head,
    IReadOnlyList<string> Tags,
    string Title,
    IReadOnlyList<Fact> Lines,
    string ListHead,
    IReadOnlyList<string> List,
    string Provenance,
    string Status,
    string ApproveLabel,
    object Proposal
)
{
    public bool Pending => Status is "pending";
    public bool Rejected => Status is "rejected";
    public bool HasList => List.Count > 0;
    public string AccessibleName => $"{Head}: {Title}";
}

/// <summary>
/// Work › Inbox (app.js viewArchProposals): the tasks, blueprints, architectural tasks and by-design marks agents
/// proposed, by status (pending first); nothing changes until one is approved (as proposed or restated) or rejected.
/// </summary>
public sealed partial class InboxViewModel : SectionViewModel
{
    private readonly UiServices _services;
    private IReadOnlyList<BlueprintInfo> _blueprints = [];
    private IReadOnlyList<TaskSummary> _tasks = [];

    public InboxViewModel(UiServices services, string projectId)
        : base("", projectId, ["proposals", "tasks", "findings"])
    {
        _services = services;
        Hint =
            "Tasks, blueprints, architectural tasks and by-design marks agents proposed. Nothing changes until you approve one (it creates the task, the blueprint or the mark, or restates the blueprint) or reject it. Agents are told not to create these themselves; that is steered, not enforced: one editing a backlog file still could.";
        StatusChips = [.. new[] { "pending", "approved", "rejected", "all" }.Select(s => new FilterChip(s, s, 0, Chosen) { On = s is "pending" })];
    }

    public IReadOnlyList<FilterChip> StatusChips { get; }
    public ObservableCollection<ProposalCard> TaskCards { get; } = [];
    public ObservableCollection<ProposalCard> BlueprintCards { get; } = [];
    public ObservableCollection<ProposalCard> ArchCards { get; } = [];

    [ObservableProperty]
    public partial string Status { get; private set; } = "pending";

    [ObservableProperty]
    public partial string? Empty { get; private set; }

    [ObservableProperty]
    public partial bool InboxZero { get; private set; }

    public bool HasTaskCards => TaskCards.Count > 0;
    public bool HasBlueprintCards => BlueprintCards.Count > 0;
    public bool HasArchCards => ArchCards.Count > 0;

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var id = ProjectId!;
        var tasks = _services.Api.TaskProposalsAsync(id, Status, token);
        var blueprints = _services.Api.BlueprintProposalsAsync(id, Status, token);
        var arch = _services.Api.ArchProposalsAsync(id, Status, token);
        Lists.Sync(TaskCards, (await tasks).Proposals.Select(TaskCard));
        Lists.Sync(BlueprintCards, (await blueprints).Proposals.Select(BlueprintCard));
        Lists.Sync(ArchCards, (await arch).Proposals.Select(ArchCard));
        var none = TaskCards.Count + BlueprintCards.Count + ArchCards.Count is 0;
        InboxZero = none && Status is "pending";
        var which = Status is "all" ? "" : Status + " ";
        Empty = none && Status is not "pending" ? $"No {which}proposals." : null;
        OnPropertyChanged(nameof(HasTaskCards));
        OnPropertyChanged(nameof(HasBlueprintCards));
        OnPropertyChanged(nameof(HasArchCards));
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
    private async Task ApproveAsync(ProposalCard card)
    {
        switch (card.Proposal)
        {
            case TaskProposal task:
                await ReadPlanAsync();
                await TaskFormDialog.ApproveAsync(_services, ProjectId!, _blueprints, _tasks, task);
                break;
            case BlueprintProposal blueprint:
                await BlueprintFormDialog.ApproveAsync(_services, ProjectId!, blueprint);
                break;
            case ArchProposal arch:
                await _services.Dialogs.ShowAsync(
                    new ArchApproveDialog(_services, ProjectId!, arch)
                    {
                        Title = arch.Kind is "task" ? "Create this task?" : "Mark the finding by design?",
                        Sub = arch.Title,
                        Wide = true,
                        AcceptLabel = arch.Kind is "task" ? "Create the task" : "Mark by design",
                    }
                );
                break;
        }
    }

    [RelayCommand]
    private async Task RejectAsync(ProposalCard card) =>
        await _services.Dialogs.ShowAsync(
            new RejectDialog(_services, ProjectId!, card.Kind, card.Id)
            {
                Title = "Reject it?",
                Sub = card.Title,
                AcceptLabel = "Reject",
                Danger = true,
            }
        );

    /// <summary>The project's blueprints and tasks, for the approve form's pickers.</summary>
    private async Task ReadPlanAsync()
    {
        try
        {
            var blueprints = _services.Api.BlueprintsAsync(ProjectId!, CancellationToken.None);
            _tasks = (await _services.Api.TasksAsync(ProjectId!, CancellationToken.None)).Tasks;
            _blueprints = (await blueprints).Blueprints;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            // the form still works: no blueprints to pick, no tasks to wait for
        }
    }

    private static string Decided(Proposal x)
    {
        if (x.Status is "pending")
        {
            return "";
        }
        var result = x.Result is { Length: > 0 } r && r is not "marked" ? $" → {r}" : "";
        return $" · {x.Status}{result}";
    }

    private static string Provenance(Proposal x, params string?[] first)
    {
        var parts = first.OfType<string>().Where(p => p.Length > 0).ToList();
        parts.Add("proposed by " + (x.ProposedBy is { Length: > 0 } by ? by : "unknown"));
        parts.Add(Shell.Ago(x.ProposedAt));
        if (x.DecidedBy is { Length: > 0 } decided)
        {
            parts.Add($"{x.Status} by {decided}");
        }
        if (x.Note is { Length: > 0 } note)
        {
            parts.Add("note " + note);
        }
        return string.Join(" · ", parts);
    }

    private static IReadOnlyList<Fact> Lines(params (string Label, string? Text)[] lines) =>
        [.. lines.Where(l => l.Text is { Length: > 0 }).Select(l => new Fact(l.Label, l.Text!))];

    private static ProposalCard TaskCard(TaskProposal x)
    {
        var d = x.Task;
        var tags = new List<string> { $"{d.Type ?? "feature"} · {d.Priority ?? "medium"}" };
        if (d.Blueprint is { Length: > 0 } bp)
        {
            tags.Add(bp);
        }
        return new ProposalCard(
            "task",
            x.Id,
            "Task" + Decided(x),
            tags,
            d.Title,
            Lines(("Goal", d.Goal), ("Why proposed", x.Reason)),
            $"Done when ({d.Acceptance.Count})",
            d.Acceptance,
            Provenance(
                x,
                d.DependsOn.Count > 0 ? "depends on " + string.Join(", ", d.DependsOn) : null,
                d.Scope.Count > 0 ? "scope " + string.Join(", ", d.Scope) : null
            ),
            x.Status,
            "Create the task…",
            x
        );
    }

    private static ProposalCard BlueprintCard(BlueprintProposal x)
    {
        var d = x.Blueprint;
        var isNew = x.Changes is null;
        return new ProposalCard(
            "blueprint",
            x.Id,
            (isNew ? "New blueprint" : "Blueprint change") + Decided(x),
            isNew ? [] : [x.Changes!],
            d.Title ?? "",
            Lines(
                ("Same title as", x.SameTitle is { Length: > 0 } same && x.Status is "pending" ? $"{same} (approving asks: merge into it, or a separate one)" : null),
                ("Goal", d.Goal),
                ("Why proposed", x.Reason),
                ("Notes", d.Notes)
            ),
            $"Done when ({d.Exit.Count})",
            d.Exit,
            Provenance(x),
            x.Status,
            isNew ? "Create the blueprint…" : "Apply the change…",
            x
        );
    }

    private static ProposalCard ArchCard(ArchProposal x)
    {
        var task = x.Kind is "task";
        var own = x.Basis is "observation";
        var head = "By design";
        if (task)
        {
            head = own ? "Task from the agent's observation" : "Task from a finding";
        }
        return new ProposalCard(
            "arch",
            x.Id,
            head + Decided(x),
            [own ? "the agent's reading" : $"harness finding · {x.Basis}"],
            x.Title,
            Lines((task ? "Proposed" : "Why intended", x.Body)),
            $"Evidence ({x.Evidence.Count})",
            x.Evidence,
            Provenance(x, x.Scope, x.FindingKey is { Length: > 0 } key ? "from a " + FindingKind(key) : null),
            x.Status,
            task ? "Create the task…" : "Mark by design…",
            x
        );
    }

    /// <summary>A finding named by its key's kind ("duplicate:a|b": "copy finding").</summary>
    private static string FindingKind(string key)
    {
        var kind = key.Split(':')[0];
        var noun = kind switch
        {
            "duplicate" => "copy",
            "local-copy" => "local copy",
            "sequence" => "repeated statements",
            _ => kind.Replace('-', ' '),
        };
        return noun + " finding";
    }
}
