using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Api;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The Avalonia Board against the fixture API: what it shows, what its pickers send, live reloads kept in place, the
/// UI checks (UiChecks) at every size and theme, and the contrast of the colour tokens it draws with.
/// </summary>
public sealed class BoardViewTests
{
    [Fact]
    public Task TheBoardShowsTheServersColumnsAndCards() => Ui.RunAsync(async () =>
    {
        var (window, _, _) = await Ui.OpenMainAsync(1280, 800);

        var columns = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("column")).ToArray();
        columns.Select(c => ((BoardColumnViewModel)c.DataContext!).Label)
            .Should()
            .Equal("backlog", "ready", "claimed", "in progress", "review", "done", "blocked");
        var cards = Cards(window);
        cards.Select(c => ((TaskCardViewModel)c.DataContext!).Id)
            .Should()
            .Equal("TASK-0001", "TASK-0002", "TASK-0003", "TASK-0004");
        var critical = cards[1];
        critical.FindControl<TextBlock>("Priority")!.Classes.Should().Contain("p-critical");
        critical.FindControl<Border>("BlueprintFrame")!.Classes.Should().Contain("bp-yellow");
        critical.FindControl<SelectableTextBlock>("Title")!.Text.Should().Be("Port the Board to Avalonia");
        cards[2].FindControl<Border>("BlueprintFrame")!.IsVisible.Should().BeFalse("TASK-0003 has no blueprint");
        // the id opens the task's dialog; its context menu keeps the task's page in the current UI
        var open = critical.FindControl<Button>("OpenTask")!;
        open.CommandParameter.Should().Be("TASK-0002");
        open.Command.Should().NotBeNull();
        ((TaskCardViewModel)critical.DataContext!).Link.ToString()
            .Should()
            .Be("http://127.0.0.1:17477/#/p/prj_fixture/work/tasks/TASK-0002");
    });

    [Fact]
    public Task ChoosingASortSavesItForTheProjectAndReloadsTheBoard() => Ui.RunAsync(async () =>
    {
        var (window, api, model) = await Ui.OpenMainAsync(1280, 800);
        var gets = api.Gets.Count(g => g.EndsWith("/board", StringComparison.Ordinal));
        api.Answers[$"/api/projects/{Fixtures.ProjectId}/board"] = Fixtures.Board(sort: "id");

        window.GetVisualDescendants().OfType<BoardView>().Single().FindControl<ComboBox>("SortPicker")!.SelectedIndex = 1;
        await model.Board!.SaveCommand.ExecutionTask!;

        var post = api.Posts.Should().ContainSingle().Subject;
        post.Path.Should().Be($"/api/projects/{Fixtures.ProjectId}/board");
        post.Body.Should().Be("""{"sort":"id"}""");
        post.Ui.Should().Be("1");
        api.Gets.Count(g => g.EndsWith("/board", StringComparison.Ordinal)).Should().Be(gets + 1);
        model.Board.Sort!.Label.Should().Be("Id");
    });

    [Fact]
    public Task ALiveChangeToTheShownProjectRedrawsItsBoard() => Ui.RunAsync(async () =>
    {
        var (window, api, model) = await Ui.OpenMainAsync(1280, 800);
        await Ui.UntilAsync(() => model.Services.Live.State is "live");
        api.Answers[$"/api/projects/{Fixtures.ProjectId}/board"] = Fixtures.Board(title: "Changed elsewhere");

        api.Change("""{"projects":["prj_other"],"kinds":{"prj_other":["tasks"]}}""");
        api.Change($$$"""{"projects":["{{{Fixtures.ProjectId}}}"],"kinds":{"{{{Fixtures.ProjectId}}}":["tasks"]}}""");
        await Ui.UntilAsync(() => model.LiveReloads > 0);
        await Ui.UntilAsync(() => Cards(window).Count is 4);

        model.LiveReloads.Should().Be(1, "a change to another project does not reload this Board");
        Cards(window)[1].FindControl<SelectableTextBlock>("Title")!.Text.Should().Be("Changed elsewhere");
    });

    [Fact]
    public Task ALiveReloadUpdatesTheChangedCardInPlaceAndKeepsScrollAndSelection() => Ui.RunAsync(async () =>
    {
        var (window, api, model) = await Ui.OpenMainAsync(1280, 600, Fixtures.Board(moreDone: 30));
        await Ui.UntilAsync(() => model.Services.Live.State is "live");
        var done = window
            .GetVisualDescendants()
            .OfType<Border>()
            .Single(b => b.Classes.Contains("column") && ((BoardColumnViewModel)b.DataContext!).Status is "done");
        var scroller = done.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroller.Extent.Height.Should().BeGreaterThan(scroller.Viewport.Height, "the done column must scroll");
        scroller.Offset = new Vector(0, 300);
        Dispatcher.UIThread.RunJobs();
        var cardsBefore = Cards(window).ToDictionary(c => ((TaskCardViewModel)c.DataContext!).Id, StringComparer.Ordinal);
        var selected = cardsBefore["TASK-0001"].FindControl<SelectableTextBlock>("Title")!;
        selected.SelectionStart = 8;
        selected.SelectionEnd = 11;

        api.Answers[$"/api/projects/{Fixtures.ProjectId}/board"] = Fixtures.Board(title: "Changed elsewhere", moreDone: 30);
        api.Change($$$"""{"projects":["{{{Fixtures.ProjectId}}}"],"kinds":{"{{{Fixtures.ProjectId}}}":["tasks"]}}""");
        await Ui.UntilAsync(() => model.LiveReloads > 0 && !model.Board!.Loading);
        var cardsAfter = Cards(window).ToDictionary(c => ((TaskCardViewModel)c.DataContext!).Id, StringComparer.Ordinal);

        cardsAfter.Keys.Should().BeEquivalentTo(cardsBefore.Keys);
        foreach (var (id, card) in cardsAfter)
        {
            card.Should().BeSameAs(cardsBefore[id], $"{id}'s card is kept, not rebuilt");
        }
        cardsAfter["TASK-0002"].FindControl<SelectableTextBlock>("Title")!.Text.Should().Be("Changed elsewhere");
        scroller.Offset.Y.Should().Be(300);
        selected.SelectedText.Should().Be("the");
    });

    [Fact]
    public Task EveryWidthAndThemePassesTheUiChecks() =>
        Ui.RunAsync(() => UiChecks.AssertAsync(async (width, height) => (await Ui.OpenMainAsync(width, height)).Window));

    [Fact]
    public Task TabMovesThroughThePickersAndCards() => Ui.RunAsync(async () =>
    {
        var (window, _, _) = await Ui.OpenMainAsync(1280, 800);
        var focused = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            focused.Add(Name((Control)window.FocusManager.GetFocusedElement()!));
        }

        focused.SkipWhile(f => f is not "SortPicker").Take(3).Should().Equal("SortPicker", "HideDonePicker", "BlueprintPicker");
    });

    private static List<TaskCard> Cards(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return [.. window.GetVisualDescendants().OfType<TaskCard>()];
    }

    private static string Name(Control control) =>
        control.Name is { Length: > 0 } name ? name : control.GetType().Name;
}
