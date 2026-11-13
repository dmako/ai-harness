using System.Collections.ObjectModel;
using Harness.Ui.Api;

namespace Harness.Ui.ViewModels;

/// <summary>A choice in one of the Board's pickers: the value the server takes and what the user reads.</summary>
public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A project's Board: one column per status, sorted and filtered by the server (TaskBoard). Changing a picker saves
/// this machine's choice for the project and reloads the Board, as the JavaScript UI does.
/// </summary>
public sealed class BoardViewModel(HarnessApi api, string projectId, Func<string, string> taskLink) : ViewModel
{
    private static readonly Dictionary<string, string> SortLabels = new(StringComparer.Ordinal)
    {
        ["priority"] = "Priority",
        ["id"] = "Id",
        ["name"] = "Name",
        ["created"] = "Created",
        ["changed"] = "Last changed",
    };

    private bool _applying;
    private Choice? _sort;
    private Choice? _hideDone;
    private Choice? _blueprint;
    private string? _error;
    private bool _loading;

    public string ProjectId { get; } = projectId;
    public ObservableCollection<BoardColumnViewModel> Columns { get; } = [];
    public ObservableCollection<Choice> Sorts { get; } = [];
    public ObservableCollection<Choice> HideDoneOptions { get; } = [];
    public ObservableCollection<Choice> Blueprints { get; } = [];

    public Choice? Sort
    {
        get => _sort;
        set
        {
            if (Set(ref _sort, value) && value is not null)
            {
                Save(new BoardChange { Sort = value.Value });
            }
        }
    }

    public Choice? HideDone
    {
        get => _hideDone;
        set
        {
            if (Set(ref _hideDone, value) && value is not null)
            {
                Save(new BoardChange { HideDoneDays = int.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture) });
            }
        }
    }

    public Choice? Blueprint
    {
        get => _blueprint;
        set
        {
            if (Set(ref _blueprint, value) && value is not null)
            {
                Save(new BoardChange { Blueprint = value.Value });
            }
        }
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool Loading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    /// <summary>Fetches the Board and shows it; a failure is shown, not thrown.</summary>
    public async Task LoadAsync(CancellationToken token)
    {
        Loading = true;
        try
        {
            Apply(await api.BoardAsync(ProjectId, token));
            Error = null;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>Shows <paramref name="board"/>: pickers set to its settings without saving them back.</summary>
    public void Apply(BoardResponse board)
    {
        _applying = true;
        try
        {
            var info = board.Blueprints.ToDictionary(b => b.Id, StringComparer.Ordinal);
            Replace(Sorts, board.Sorts.Select(s => new Choice(s, SortLabels.GetValueOrDefault(s, s))));
            Replace(
                HideDoneOptions,
                board.HideDoneDaysOptions.Select(d =>
                {
                    var days = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return new Choice(days, d is 1 ? "1 day" : days + " days");
                })
            );
            Replace(
                Blueprints,
                [
                    new Choice("", "all blueprints"),
                    .. board.Blueprints.Select(b => new Choice(b.Id, b.Title is { Length: > 0 } t ? $"{b.Id} · {t}" : b.Id)),
                    new Choice("—", "no blueprint"),
                ]
            );
            Sort = Sorts.FirstOrDefault(c => c.Value == board.Settings.Sort);
            var hide = board.Settings.HideDoneDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
            HideDone = HideDoneOptions.FirstOrDefault(c => c.Value == hide);
            Blueprint = Blueprints.FirstOrDefault(c => c.Value == (board.Settings.Blueprint ?? ""));
            Replace(
                Columns,
                board.Columns.Select(c => new BoardColumnViewModel(
                    c.Status,
                    [.. c.Tasks.Select(t => new TaskCardViewModel(t, t.Blueprint is { } b ? info.GetValueOrDefault(b) : null, taskLink(t.Id)))]
                ))
            );
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>The last save a picker started (tests and hosts can wait for it).</summary>
    public Task Saving { get; private set; } = Task.CompletedTask;

    private void Save(BoardChange change)
    {
        if (!_applying)
        {
            Saving = SaveAsync(change);
        }
    }

    private async Task SaveAsync(BoardChange change)
    {
        try
        {
            await api.SetBoardAsync(ProjectId, change, CancellationToken.None);
            await LoadAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}

/// <summary>A status's column: its heading, count and cards.</summary>
public sealed class BoardColumnViewModel(string status, IReadOnlyList<TaskCardViewModel> tasks)
{
    public string Status { get; } = status;
    public string Label { get; } = status.Replace('_', ' ');
    public IReadOnlyList<TaskCardViewModel> Tasks { get; } = tasks;
    public int Count => Tasks.Count;
    public bool IsEmpty => Tasks.Count is 0;
    public string AccessibleName => $"{Label}: {Count}";
}

/// <summary>A task's card: type and id, priority, title and its blueprint's frame.</summary>
public sealed class TaskCardViewModel(TaskSummary task, BlueprintInfo? blueprint, string link)
{
    public string Id { get; } = task.Id;
    public string Title { get; } = task.Title;
    public string Priority { get; } = task.Priority;
    public string Type { get; } = task.Type;
    public Uri Link { get; } = new(link, UriKind.RelativeOrAbsolute);
    public string? BlueprintId { get; } = task.Blueprint;

    /// <summary>The blueprint's title, else its id; null without a blueprint.</summary>
    public string? BlueprintText { get; } = BlueprintName(task.Blueprint, blueprint);

    /// <summary>The blueprint's colour name (one of the 16), or null for the neutral frame.</summary>
    public string? BlueprintColor { get; } = blueprint?.Color;

    public bool HasBlueprint => BlueprintText is not null;
    public string PriorityTip => Priority + " priority";

    private static string? BlueprintName(string? id, BlueprintInfo? blueprint)
    {
        if (id is null)
        {
            return null;
        }
        return blueprint?.Title is { Length: > 0 } title ? title : id;
    }
}
