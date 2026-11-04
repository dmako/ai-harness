using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Core.Tasks;
using Xunit;

namespace Harness.Unit.Tasks;

public sealed class TaskBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static TaskRecord Task(
        string id,
        string status = "ready",
        string priority = "medium",
        string title = "",
        string blueprint = "",
        int ordinal = 0,
        double createdDaysAgo = 10,
        double changedDaysAgo = 1,
        double? completedDaysAgo = null
    ) =>
        new()
        {
            Definition = new TaskDefinition
            {
                Id = id,
                Title = title.Length is 0 ? id : title,
                Priority = priority,
                Blueprint = blueprint,
            },
            Status = status,
            Ordinal = ordinal,
            CreatedAt = new MemoryTimestamp(Now.AddDays(-createdDaysAgo)),
            UpdatedAt = new MemoryTimestamp(Now.AddDays(-changedDaysAgo)),
            CompletedAt = completedDaysAgo is { } days ? new MemoryTimestamp(Now.AddDays(-days)) : null,
        };

    private static string Shown(IReadOnlyList<BoardColumn> columns, string status) =>
        string.Join(",", columns.Single(c => c.Status == status).Tasks.Select(t => t.Id));

    [Fact]
    public void EveryStatusButDeferredHasAColumnInWorkOrderAndEveryTaskIsInItsOwnOnce()
    {
        TaskBoard.Statuses.Should().Equal("backlog", "ready", "claimed", "in_progress", "review", "done", "blocked");
        TaskBoard.Statuses.Should().BeSubsetOf(TaskWorkflow.Statuses).And.NotContain("deferred");
        TaskRecord[] tasks =
        [
            .. TaskWorkflow.Statuses.Select((s, i) => Task("T-" + i, s, completedDaysAgo: s is "done" ? 0.5 : null)),
        ];
        var columns = TaskBoard.Columns(tasks, TaskBoard.Defaults, Now);
        columns.Select(c => c.Status).Should().Equal(TaskBoard.Statuses);
        columns
            .SelectMany(c => c.Tasks)
            .Should()
            .BeEquivalentTo(tasks.Where(t => t.Status is not "deferred"), "every task but the deferred shows once");
        columns.Should().AllSatisfy(c => c.Tasks.Should().AllSatisfy(t => t.Status.Should().Be(c.Status)));
    }

    [Fact]
    public void CardsSortByPriorityByDefaultAndByIdNameCreatedOrChanged()
    {
        TaskRecord[] tasks =
        [
            Task("T-3", priority: "low", title: "alpha", ordinal: 0, createdDaysAgo: 1, changedDaysAgo: 9),
            Task("T-1", priority: "critical", title: "Gamma", ordinal: 2, createdDaysAgo: 5, changedDaysAgo: 2),
            Task("T-2", priority: "high", title: "beta", ordinal: 1, createdDaysAgo: 3, changedDaysAgo: 0.1),
            Task("T-4", priority: "high", title: "Beta", ordinal: 3, createdDaysAgo: 9, changedDaysAgo: 5),
        ];
        string Sorted(string sort) => Shown(TaskBoard.Columns(tasks, new(sort), Now), "ready");
        Sorted("priority").Should().Be("T-1,T-2,T-4,T-3", "highest first, then the backlog's order");
        Sorted("id").Should().Be("T-1,T-2,T-3,T-4");
        Sorted("name").Should().Be("T-3,T-2,T-4,T-1", "by title in any case, same titles by id");
        Sorted("created").Should().Be("T-3,T-2,T-1,T-4", "newest first");
        Sorted("changed").Should().Be("T-2,T-1,T-4,T-3", "last changed first");
        TaskBoard.Defaults.Sort.Should().Be("priority");
        FluentActions
            .Invoking(() => TaskBoard.ValidSort("random"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*priority*");
    }

    [Theory]
    [InlineData(1, "T-new")]
    [InlineData(3, "T-new,T-two")]
    [InlineData(7, "T-new,T-two,T-six")]
    [InlineData(14, "T-new,T-two,T-six,T-ten")]
    public void DoneTasksFinishedLongerAgoThanTheDoneAgeAreHidden(int days, string shown)
    {
        TaskRecord[] tasks =
        [
            Task("T-new", "done", completedDaysAgo: 0.5),
            Task("T-two", "done", completedDaysAgo: 2),
            Task("T-six", "done", completedDaysAgo: 6),
            Task("T-ten", "done", completedDaysAgo: 10),
            Task("T-old", "done", completedDaysAgo: 30),
            // imported done, never completed here: its last change counts
            Task("T-imp", "done", changedDaysAgo: 40),
            Task("T-open", "ready", changedDaysAgo: 40),
        ];
        var columns = TaskBoard.Columns(tasks, new("id", days), Now);
        Shown(columns, "done")
            .Split(',')
            .Order(StringComparer.Ordinal)
            .Should()
            .Equal(shown.Split(',').Order(StringComparer.Ordinal));
        Shown(columns, "ready").Should().Be("T-open", "only done tasks age out");
        FluentActions.Invoking(() => TaskBoard.ValidHideDoneDays(5)).Should().Throw<ArgumentException>();
        TaskBoard.HideDoneDays.Should().Equal(1, 3, 7, 14);
    }

    [Fact]
    public void TheBlueprintFilterKeepsOneBlueprintsTasksOrThoseWithout()
    {
        TaskRecord[] tasks = [Task("T-1", blueprint: "M1"), Task("T-2", blueprint: "M2"), Task("T-3")];
        Shown(TaskBoard.Columns(tasks, new("id", 7, "M2"), Now), "ready").Should().Be("T-2");
        Shown(TaskBoard.Columns(tasks, new("id", 7, "—"), Now), "ready").Should().Be("T-3");
        Shown(TaskBoard.Columns(tasks, new("id", 7, ""), Now), "ready").Should().Be("T-1,T-2,T-3");
    }

    [Fact]
    public void BlueprintColoursAreSixteenGivenOutFirstFreeAndThenAgainFromTheFirst()
    {
        BlueprintColors.Names.Should().HaveCount(16).And.OnlyHaveUniqueItems();
        BlueprintColors.Next([]).Should().Be("teal");
        BlueprintColors.Next(["teal", "blue"]).Should().Be("orange", "the first one no other blueprint uses");
        BlueprintColors.Next(["", "unknown"]).Should().Be("teal", "no colour or an unknown one uses none");
        BlueprintColors.Next(BlueprintColors.Names).Should().Be("teal", "all 16 used: from the first again");
        BlueprintColors.Next([.. BlueprintColors.Names, "teal"]).Should().Be("orange");
        BlueprintColors.Normalize(" Rose ").Should().Be("rose");
        BlueprintColors.Normalize("").Should().BeEmpty();
        FluentActions
            .Invoking(() => BlueprintColors.Normalize("mauve"))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*mauve*teal*");
    }
}
