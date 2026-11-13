using System.Globalization;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Controls;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The Avalonia Overview against the fixture API: the rings, what is in flight, the proposals waiting, what needs
/// attention and the recent activity, each section on its own; since the last visit; a project whose state cannot be
/// read; and the UI checks at every size and theme.
/// </summary>
public sealed class OverviewViewTests
{
    private const string Address = "#/p/prj_fixture/overview";
    private const string Root = "/api/projects/prj_fixture";

    [Fact]
    public Task TheRingsSayHowFarTasksContextAndReadsHaveCome() => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await OpenAsync(1280, 800);
        var summary = Overview(model).Summary;

        summary.Rings.Select(r => (r.Label, r.Percent, r.Text))
            .Should()
            .Equal(("Tasks done", "33%", "1 of 3"), ("Context hits", "—", "not scored yet"), ("Read next", "—", "needs the client hooks"));
        var tasks = summary.Rings[0];
        tasks.Delta.Should().Be("+2 this week");
        tasks.DeltaUp.Should().BeTrue();
        tasks.Bars.Should().HaveCount(30);
        summary.Rings[1].Line.Should().NotBeNull("two weeks of answers are a line");
        summary.Rings[1].Delta.Should().StartWith("▲").And.EndWith("pts vs the week before");
        summary.Rings[2].HasSpark.Should().BeFalse();
        summary.RingValues.Select(v => v.Value).Should().Equal(1 / 3.0, null, null);

        var view = window.GetVisualDescendants().OfType<OverviewView>().Single();
        view.GetVisualDescendants().OfType<Rings>().Single().Values.Should().HaveCount(3);
        view.GetVisualDescendants().OfType<AppLink>().Select(l => l.Href)
            .Should()
            .Contain("#/p/prj_fixture/work").And.Contain("#/p/prj_fixture/agents");
    });

    [Fact]
    public Task AScoredContextAndHooksFillTheirRings() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/report"] = Fixtures.Report(scored: true);
        var (_, _, model) = await OpenAsync(1280, 800, api);

        Overview(model).Summary.Rings.Select(r => (r.Percent, r.Text))
            .Should()
            .Equal(("33%", "1 of 3"), ("65%", "65% per answer"), ("70%", "7 of 10 files"));
    });

    [Fact]
    public Task TheNowCardShowsWhatIsInFlightThenWhatIsReady() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/tasks"] = Fixtures.Tasks(("TASK-0004", "in_progress", "Working on it"), ("TASK-0005", "review", "Check it"), ("TASK-0006", "ready", "Next"));
        var (_, _, model) = await OpenAsync(1280, 800, api);
        var summary = Overview(model).Summary;

        summary.NowLabel.Should().Be("In flight");
        summary.NowNote.Should().Be("3 in flight · 2 ready");
        summary.Now.Select(r => (r.Id, r.ShowStatus))
            .Should()
            .Equal(("TASK-0003", true), ("TASK-0004", true), ("TASK-0005", true), ("TASK-0001", false));
        summary.HasMoreTasks.Should().BeTrue("TASK-0006 is not shown");
        summary.Now[0].Href.Should().Be("#/p/prj_fixture/work/TASK-0003");
    });

    [Fact]
    public Task WhenEveryTaskIsDoneTheNowCardShowsTheLastOnesDone() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/tasks"] = """{"tasks":[{"id":"T-1","title":"Old","status":"done","completion":{"completed_at":"2026-01-01T00:00:00Z"}},{"id":"T-2","title":"New","status":"done","completion":{"completed_at":"2026-02-01T00:00:00Z"}}]}""";
        var (_, _, model) = await OpenAsync(1280, 800, api);
        var summary = Overview(model).Summary;

        summary.NowEmpty.Should().Be("✓ Every task is done; the last ones:");
        summary.AllDone.Should().BeTrue();
        summary.Now.Select(r => r.Id).Should().Equal("T-2", "T-1");
    });

    [Fact]
    public Task NeedsAttentionListsWhatWaitsMostUrgentFirst() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/projects"] = Fixtures.Projects.Replace("\"checks_failed\": 0", "\"checks_failed\": 2", StringComparison.Ordinal);
        api.Answers[Root + "/tasks"] = Fixtures.Tasks(("TASK-0009", "blocked", "Stuck"));
        api.Answers[Root + "/report"] = Fixtures.Report(false, ("warn", "graphify answered empty for 3 of 5 calls"), ("info", "just so you know"));
        var (_, _, model) = await OpenAsync(1280, 800, api);
        var summary = Overview(model).Summary;

        summary.Attention.Select(a => (a.Level, a.Message, a.Href))
            .Should()
            .Equal(
                ("problem", "2 doctor checks failing", "#/p/prj_fixture/health"),
                ("warn", "1 knowledge proposal waits for review", "#/p/prj_fixture/knowledge/inbox"),
                ("warn", "2 task proposals wait for review", "#/p/prj_fixture/work/inbox"),
                ("warn", "TASK-0009 is blocked: waits for the server", "#/p/prj_fixture/work/TASK-0009"),
                ("warn", "graphify answered empty for 3 of 5 calls", "#/p/prj_fixture/agents")
            );
        summary.HasFindings.Should().BeTrue();
        summary.Proposals.Should().Be("3");
        summary.ProposalsNote.Should().Be("1 knowledge · 2 tasks");
        summary.ProposalsHref.Should().Be("#/p/prj_fixture/knowledge/inbox");
    });

    [Fact]
    public Task NothingWaitingIsAllClear() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/projects"] = Fixtures.Projects
            .Replace("\"pending_proposals\": 1", "\"pending_proposals\": 0", StringComparison.Ordinal)
            .Replace("\"pending_task_proposals\": 2", "\"pending_task_proposals\": 0", StringComparison.Ordinal);
        var (window, _, model) = await OpenAsync(1280, 800, api);
        var summary = Overview(model).Summary;

        summary.HasAttention.Should().BeFalse();
        summary.ProposalsNote.Should().Be("nothing waiting");
        window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "AllClear").IsEffectivelyVisible.Should().BeTrue();
    });

    [Fact]
    public Task TheActivityShowsTheWeekAndTheNewestLines() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        var items = new List<System.Text.Json.Nodes.JsonObject>
        {
            Fixtures.Item("session", "claude-code used harness: 12 call(s) over 5 min, 2 failed", 10, "get_context ×8, search_project ×4", "claude-code", "warn"),
            Fixtures.Item("task", "TASK-0002 done: Done one", 20, actor: "claude-code", id: "TASK-0002"),
            Fixtures.Item("memory", "decision recorded", 30, "Use Avalonia for the new UI", "claude-code", id: "mem_1"),
            Fixtures.Item("sync", "sync ok", 40, "+2 ~5 -1"),
        };
        items.AddRange(Enumerable.Range(0, 8).Select(i => Fixtures.Item("task", $"TASK-01{i:00} claimed: More", 60 + i, id: $"TASK-01{i:00}")));
        api.Answers["/api/activity"] = Fixtures.Activity([.. items]);
        var (_, _, model) = await OpenAsync(1280, 800, api);
        var activity = Overview(model).Activity;

        activity.Rows.Should().HaveCount(8);
        var session = activity.Rows[0];
        (session.Head, session.HeadMuted, session.Failed, session.Glyph, session.Meta)
            .Should()
            .Be(("12 calls", "over 5 min", "2 failed", "C", "Claude Code · get_context ×8, search_project ×4"));
        session.Href.Should().Be("#/p/prj_fixture/agents/tools");
        activity.Rows[1].Href.Should().Be("#/p/prj_fixture/work/TASK-0002");
        activity.Rows[2].Meta.Should().Be("Use Avalonia for the new UI · claude-code");
        activity.Rows[3].Href.Should().Be("#/p/prj_fixture/health");
        activity.WeekSummary.Should().Be("12 calls in 1 session · 10 changes to tasks and knowledge");
        activity.Bars.Should().HaveCount(7);
        activity.Week[6].Label.Should().Be("today");
        activity.HasMore.Should().BeTrue();

        activity.ShowAllCommand.Execute(null);

        activity.Rows.Should().HaveCount(12);
        activity.HasMore.Should().BeFalse();
    });

    [Fact]
    public Task AQuietWeekSaysSo() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/activity"] = Fixtures.Activity();
        var (_, _, model) = await OpenAsync(1280, 800, api);

        Overview(model).Activity.Quiet.Should().BeTrue();
    });

    [Fact]
    public Task EachSectionLoadsAndFailsOnItsOwn() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Failures["/api/activity"] = System.Net.HttpStatusCode.InternalServerError;
        api.Failures[Root + "/trends"] = System.Net.HttpStatusCode.InternalServerError;
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, address: Address);
        var overview = Overview(model);
        await Ui.UntilAsync(() => overview.Activity.HasError && overview.Summary.HasContent);

        overview.Activity.Error.Should().Contain("500");
        overview.Summary.HasError.Should().BeFalse("without the trends the rings have no picture, nothing more");
        overview.Summary.Rings[0].Bars.Should().BeNull();

        api.Failures.Clear();
        await overview.Activity.RetryCommand.ExecuteAsync(null);
        overview.Activity.HasError.Should().BeFalse();
    });

    [Fact]
    public Task SinceTheLastVisitSaysWhatHappened() => Ui.RunAsync(async () =>
    {
        var preferences = new InMemoryPreferences();
        preferences.Set("seen.prj_fixture", DateTimeOffset.UtcNow.AddHours(-3).ToString("O", CultureInfo.InvariantCulture));
        var api = new FixtureApi();
        api.Answers["/api/activity"] = Fixtures.Activity(
            Fixtures.Item("session", "codex used harness: 4 call(s) over 2 min", 10, actor: "codex"),
            Fixtures.Item("task", "TASK-0002 done: Done one", 20, id: "TASK-0002"),
            Fixtures.Item("task", "TASK-0003 claimed: Claimed one", 30, id: "TASK-0003"),
            Fixtures.Item("memory", "decision recorded", 40),
            Fixtures.Item("task", "TASK-0001 done: Before the visit", 5 * 60, id: "TASK-0001")
        );
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, address: Address, preferences: preferences);
        var overview = Overview(model);
        await Ui.UntilAsync(() => overview.HasSince);

        overview.Since.Should().Be("Since your last visit 3 h ago: 4 agent calls · 1 task done · 1 other task change · 1 knowledge change");
        DateTimeOffset.Parse(preferences.Get("seen.prj_fixture")!, CultureInfo.InvariantCulture)
            .Should()
            .BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    });

    [Fact]
    public Task AFirstVisitOrAReturnWithinTheHourSaysNothing() => Ui.RunAsync(async () =>
    {
        var preferences = new InMemoryPreferences();
        preferences.Set("seen.prj_fixture", DateTimeOffset.UtcNow.AddMinutes(-20).ToString("O", CultureInfo.InvariantCulture));
        var (_, api, model) = await OpenAsync(1280, 800, preferences: preferences);

        Overview(model).HasSince.Should().BeFalse();
        api.Gets.Count(g => g == "/api/activity").Should().Be(1, "only the activity section read it");
    });

    [Fact]
    public Task AProjectWhoseStateCannotBeReadPointsToHealth() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/projects"] = Fixtures.Projects.Replace("\"state\": \"ok\"", "\"state\": \"error\"", StringComparison.Ordinal);
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, address: Address);

        var body = ((ProjectPage)model.Page!).Body.Should().BeOfType<UnreadableBody>().Subject;
        UnreadableBody.Message.Should().Be("This project's state cannot be read until the problem above is fixed.");
        body.HealthHref.Should().Be("#/p/prj_fixture/health");
        api.Gets.Should().NotContain(g => g.EndsWith("/tasks", StringComparison.Ordinal));
    });

    [Fact]
    public Task ALiveChangeToTheProjectsTasksReloadsTheSummary() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, fastLive: true, address: Address);
        var summary = Overview(model).Summary;
        await Ui.UntilAsync(() => summary.HasContent && model.Services.Live.State is "live");
        api.Answers[Root + "/tasks"] = Fixtures.Tasks(("TASK-0004", "done", "Also done"));

        api.Change("""{"projects":["prj_fixture"],"kinds":{"prj_fixture":["tasks"]}}""");

        await Ui.UntilAsync(() => summary.Rings[0].Text == "2 of 4");
        summary.Rings[0].Value.Should().Be(0.5);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TheOverviewPassesTheUiChecks(bool busy) =>
        Ui.RunAsync(() =>
            UiChecks.AssertAsync(async (width, height) =>
            {
                var api = new FixtureApi();
                if (busy)
                {
                    api.Answers[Root + "/tasks"] = Fixtures.Tasks(
                        ("TASK-0004", "in_progress", "A task whose title is long enough to be cut off in the Now card on a phone"),
                        ("TASK-0009", "blocked", "Stuck")
                    );
                    api.Answers[Root + "/report"] = Fixtures.Report(true, ("problem", "the graph provider failed on every call for three days in a row"));
                    api.Answers["/api/activity"] = Fixtures.Activity(
                        Fixtures.Item("session", "claude-code used harness: 12 call(s) over 5 min, 2 failed", 10, "get_context ×8, search_project ×4", "claude-code", "warn"),
                        Fixtures.Item("memory", "decision recorded", 30, "Use Avalonia for the new UI with a long detail that wraps", "claude-code")
                    );
                }
                return (await OpenAsync(width, height, api)).Window;
            })
        );

    private static async Task<(Window Window, FixtureApi Api, MainViewModel Model)> OpenAsync(
        int width,
        int height,
        FixtureApi? api = null,
        IPreferences? preferences = null
    )
    {
        var opened = await Ui.OpenMainAsync(width, height, api: api, address: Address, preferences: preferences);
        await Ui.UntilAsync(() =>
            opened.Model.Page is ProjectPage { Body: OverviewViewModel { Summary.HasContent: true, Activity.HasContent: true } }
        );
        return opened;
    }

    private static OverviewViewModel Overview(MainViewModel model) => (OverviewViewModel)((ProjectPage)model.Page!).Body!;
}
