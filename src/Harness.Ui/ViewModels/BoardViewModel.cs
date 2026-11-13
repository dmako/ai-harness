using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using DynamicData.Binding;
using Harness.Ui.Api;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>A choice in one of the Board's pickers: the value the server takes and what the user reads.</summary>
public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A task as the Board places it: what its card shows and its place in its column (the server's order). A record,
/// so a reload's diff (SourceCache.EditDiff) sees which tasks really changed.
/// </summary>
public sealed record BoardItem(
    string Id,
    string Status,
    int Order,
    string Title,
    string Priority,
    string Type,
    string? BlueprintId,
    string? BlueprintText,
    string? BlueprintColor
);

/// <summary>
/// A project's Board: one column per status, sorted and filtered by the server (TaskBoard). The tasks live in a
/// SourceCache keyed by id: a reload applies only the differences, so unchanged cards keep their controls, scroll
/// position and text selection. Changing a picker saves this machine's choice for the project and reloads. It
/// follows live changes to its project's tasks.
/// </summary>
public sealed partial class BoardViewModel(UiServices services, string projectId, Func<string, string> taskLink)
    : ObservableObject,
        ILiveSection,
        IDisposable
{
    private static readonly string[] LiveKinds = ["tasks"];

    private static readonly Dictionary<string, string> SortLabels = new(StringComparer.Ordinal)
    {
        ["priority"] = "Priority",
        ["id"] = "Id",
        ["name"] = "Name",
        ["created"] = "Created",
        ["changed"] = "Last changed",
    };

    private readonly SourceCache<BoardItem, string> _items = new(item => item.Id);
    private bool _applying;

    public string ProjectId { get; } = projectId;

    public IReadOnlyList<string> Kinds => LiveKinds;

    Task ILiveSection.RefreshAsync(bool force) => LoadAsync(CancellationToken.None);
    public ObservableCollection<BoardColumnViewModel> Columns { get; } = [];
    public ObservableCollection<Choice> Sorts { get; } = [];
    public ObservableCollection<Choice> HideDoneOptions { get; } = [];
    public ObservableCollection<Choice> Blueprints { get; } = [];

    [ObservableProperty]
    public partial Choice? Sort { get; set; }

    [ObservableProperty]
    public partial Choice? HideDone { get; set; }

    [ObservableProperty]
    public partial Choice? Blueprint { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool Loading { get; private set; }

    /// <summary>Fetches the Board and shows it; a failure is shown, not thrown.</summary>
    public async Task LoadAsync(CancellationToken token)
    {
        Loading = true;
        try
        {
            Apply(await services.Api.BoardAsync(ProjectId, token));
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
            Same(Sorts, board.Sorts.Select(s => new Choice(s, SortLabels.GetValueOrDefault(s, s))));
            Same(
                HideDoneOptions,
                board.HideDoneDaysOptions.Select(d => new Choice(Text(d), d is 1 ? "1 day" : Text(d) + " days"))
            );
            Same(
                Blueprints,
                [
                    new Choice("", "all blueprints"),
                    .. board.Blueprints.Select(b => new Choice(b.Id, b.Title is { Length: > 0 } t ? $"{b.Id} · {t}" : b.Id)),
                    new Choice("—", "no blueprint"),
                ]
            );
            Sort = Sorts.FirstOrDefault(c => c.Value == board.Settings.Sort);
            HideDone = HideDoneOptions.FirstOrDefault(c => c.Value == Text(board.Settings.HideDoneDays));
            Blueprint = Blueprints.FirstOrDefault(c => c.Value == (board.Settings.Blueprint ?? ""));
            ShowColumns(board.Columns.Select(c => c.Status).ToArray());
            var info = board.Blueprints.ToDictionary(b => b.Id, StringComparer.Ordinal);
            _items.EditDiff(
                board.Columns.SelectMany(column =>
                    column.Tasks.Select((task, order) => Item(task, order, info))
                ),
                EqualityComparer<BoardItem>.Default
            );
        }
        finally
        {
            _applying = false;
        }
    }

    public void Dispose()
    {
        foreach (var column in Columns)
        {
            column.Dispose();
        }
        _items.Dispose();
    }

    partial void OnSortChanged(Choice? value) => Save(value, v => new BoardChange { Sort = v });

    partial void OnHideDoneChanged(Choice? value) =>
        Save(value, v => new BoardChange { HideDoneDays = int.Parse(v, CultureInfo.InvariantCulture) });

    partial void OnBlueprintChanged(Choice? value) => Save(value, v => new BoardChange { Blueprint = v });

    /// <summary>Saves a picker's choice for the project and reloads the Board with it.</summary>
    [RelayCommand]
    private async Task SaveAsync(BoardChange change)
    {
        try
        {
            await services.Api.SetBoardAsync(ProjectId, change, CancellationToken.None);
            await LoadAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            services.Toasts.Fail("The Board's choice was not saved", exception.Message);
        }
    }

    private void Save(Choice? value, Func<string, BoardChange> change)
    {
        if (!_applying && value is not null)
        {
            _ = SaveCommand.ExecuteAsync(change(value.Value));
        }
    }

    /// <summary>One column per status, created once: later reloads only change the cards inside them.</summary>
    private void ShowColumns(string[] statuses)
    {
        if (Columns.Select(c => c.Status).SequenceEqual(statuses, StringComparer.Ordinal))
        {
            return;
        }
        foreach (var column in Columns)
        {
            column.Dispose();
        }
        Columns.Clear();
        foreach (var status in statuses)
        {
            Columns.Add(new BoardColumnViewModel(status, _items.Connect(), taskLink));
        }
    }

    private static BoardItem Item(TaskSummary task, int order, Dictionary<string, BlueprintInfo> blueprints)
    {
        var blueprint = task.Blueprint is { } id ? blueprints.GetValueOrDefault(id) : null;
        string? text = null;
        if (task.Blueprint is not null)
        {
            text = blueprint?.Title is { Length: > 0 } title ? title : task.Blueprint;
        }
        return new BoardItem(
            task.Id,
            task.Status,
            order,
            task.Title,
            task.Priority,
            task.Type,
            task.Blueprint,
            text,
            blueprint?.Color
        );
    }

    /// <summary>Replaces <paramref name="target"/>'s items only when they differ, so a bound picker keeps its state.</summary>
    private static void Same(ObservableCollection<Choice> target, IEnumerable<Choice> items)
    {
        var wanted = items.ToArray();
        if (target.SequenceEqual(wanted))
        {
            return;
        }
        target.Clear();
        foreach (var item in wanted)
        {
            target.Add(item);
        }
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A status's column: its heading, count and cards. The cards are the Board's SourceCache filtered by status,
/// updated in place and kept in the server's order.
/// </summary>
public sealed partial class BoardColumnViewModel : ObservableObject, IDisposable
{
    private readonly IDisposable _subscription;
    private readonly ReadOnlyObservableCollection<TaskCardViewModel> _cards;

    public BoardColumnViewModel(
        string status,
        IObservable<IChangeSet<BoardItem, string>> items,
        Func<string, string> taskLink
    )
    {
        Status = status;
        Label = status.Replace('_', ' ');
        _subscription = items
            .Filter(item => item.Status == status)
            .TransformWithInlineUpdate(
                item => new TaskCardViewModel(item, taskLink(item.Id)),
                (card, item) => card.Update(item)
            )
            .SortAndBind(out _cards, SortExpressionComparer<TaskCardViewModel>.Ascending(card => card.Order))
            .Subscribe();
        ((INotifyCollectionChanged)_cards).CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(AccessibleName));
        };
    }

    public string Status { get; }
    public string Label { get; }
    public ReadOnlyObservableCollection<TaskCardViewModel> Tasks => _cards;
    public int Count => _cards.Count;
    public bool IsEmpty => _cards.Count is 0;
    public string AccessibleName => $"{Label}: {Count}";

    public void Dispose() => _subscription.Dispose();
}

/// <summary>A task's card: type and id, priority, title and its blueprint's frame; updated in place on reloads.</summary>
public sealed partial class TaskCardViewModel : ObservableObject
{
    public TaskCardViewModel(BoardItem item, string link)
    {
        Id = item.Id;
        Link = new Uri(link, UriKind.RelativeOrAbsolute);
        Update(item);
    }

    public string Id { get; }
    public Uri Link { get; }

    [ObservableProperty]
    public partial int Order { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityTip))]
    public partial string Priority { get; private set; } = "";

    [ObservableProperty]
    public partial string Type { get; private set; } = "";

    [ObservableProperty]
    public partial string? BlueprintId { get; private set; }

    /// <summary>The blueprint's title, else its id; null without a blueprint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlueprint))]
    public partial string? BlueprintText { get; private set; }

    /// <summary>The blueprint's colour name (one of the 16), or null for the neutral frame.</summary>
    [ObservableProperty]
    public partial string? BlueprintColor { get; private set; }

    public bool HasBlueprint => BlueprintText is not null;
    public string PriorityTip => Priority + " priority";

    /// <summary>Takes a reload's values; only those that changed notify, so only they redraw.</summary>
    public void Update(BoardItem item)
    {
        Order = item.Order;
        Title = item.Title;
        Priority = item.Priority;
        Type = item.Type;
        BlueprintId = item.BlueprintId;
        BlueprintText = item.BlueprintText;
        BlueprintColor = item.BlueprintColor;
    }
}
