using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Tasks;
using Harness.Integration.Sync;
using Harness.Ui.Api;
using Xunit;

namespace Harness.Integration.Web;

/// <summary>
/// The Avalonia UI's typed contracts (src/Harness.Ui/Api, compiled in here) against a real harness ui on a seeded
/// project: every read the contracts cover deserialises strictly (a required member missing or renamed fails), writes
/// round-trip, a change arrives on /api/events, and a refusal carries the server's message. A view task adds its
/// endpoints here with its contracts.
/// </summary>
[Collection("Provider processes")]
public sealed class UiContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheUiContractsReadWriteAndFollowARealHarnessUi()
    {
        using var fixture = await SyncFixture.CreateAsync();
        UiFixtureShapeTests.Seed(fixture);
        await using var server = await UiFixture.StartAsync(fixture);
        var id = (string)JsonNode.Parse(await server.Client.GetStringAsync("/api/projects", Token))!["projects"]![0]!["id"]!;
        await UiFixtureShapeTests.TitleAsync(server, id, "Fixture project");
        await UiFixtureShapeTests.PostAsync(
            server,
            "/api/workspaces",
            new JsonObject { ["name"] = "main", ["title"] = "Main workspace", ["project_ids"] = new JsonArray(id) }
        );
        var api = new HarnessApi(server.Client) { Origin = "http://" + server.Client.BaseAddress!.Authority };

        // projects and workspaces
        var project = (await api.ProjectsAsync(Token)).Should().ContainSingle().Subject;
        project.Id.Should().Be(id);
        project.DisplayName.Should().Be("Fixture project");
        project.Live.State.Should().Be("ok");
        project.Workspaces.Should().Equal("main");
        var workspace = (await api.WorkspacesAsync(Token)).Should().ContainSingle().Subject;
        workspace.DisplayName.Should().Be("Main workspace");
        workspace.Members.Should().ContainSingle(m => m.ProjectId == id);

        // the Board, and a write that round-trips
        var board = await api.BoardAsync(id, Token);
        board.Columns.Select(c => c.Status).Should().Equal(TaskBoard.Statuses);
        board.Columns.Single(c => c.Status == "ready").Tasks.Should().ContainSingle(t => t.Id == "TASK-0001" && t.Priority == "high");
        board.Blueprints.Should().Contain(b => b.Id == "BP-009" && b.Color == "yellow");
        await api.SetBoardAsync(id, new BoardChange { Sort = "id", HideDoneDays = 14 }, Token);
        var saved = await api.BoardAsync(id, Token);
        saved.Settings.Sort.Should().Be("id");
        saved.Settings.HideDoneDays.Should().Be(14);

        // the Overview: the tasks, the report and trends over a window, and the activity (the seeded claim)
        var tasks = (await api.TasksAsync(id, Token)).Tasks;
        tasks.Select(t => (t.Id, t.Status)).Should().Equal(("TASK-0001", "ready"), ("TASK-0002", "done"), ("TASK-0003", "claimed"));
        tasks.Single(t => t.Id == "TASK-0002").Completion!.CompletedAt.Should().NotBeNullOrEmpty();
        var report = await api.ReportAsync(id, 14, Token);
        report.Days.Should().Be(14);
        report.Context.AfterRead.Should().NotBeNull();
        report.Findings.Should().NotBeNull().And.OnlyContain(f => f.Audience == "user");
        var trends = await api.TrendsAsync(id, 14, Token);
        trends.Days.Should().HaveCount(14);
        trends.Days[^1].Done.Should().Be(1, "TASK-0002 was imported as done today");
        var activity = await api.ActivityAsync(id, 7, 50, Token);
        activity.Items.Should().ContainSingle(i => i.Kind == "task" && i.Ref == "TASK-0003" && i.ProjectId == id);
        (await api.ActivityAsync(null, 7, 50, Token)).Items.Should().HaveCount(activity.Items.Count);

        // Health: the checks, a probe from harness ui's own page, a fix, and a sync that then shows as a run
        var health = await api.HealthAsync(id, probe: false, Token);
        health.Probed.Should().BeFalse();
        health.Status.Project!.Id.Should().Be(id);
        health.Status.Database.Exists.Should().BeTrue();
        health.Checks.Should().Contain(c => c.Name == "claude-code" && c.Level != "ok" && c.Fix != null);
        health.Fixable.Should().Contain("claude-code");
        (await api.HealthAsync(id, probe: true, Token)).Probed.Should().BeTrue();
        var fixedCheck = await api.FixAsync(id, new FixRequest { Check = "claude-code" }, Token);
        fixedCheck.Ok.Should().BeTrue(fixedCheck.Output);
        fixedCheck.Message.Should().Be("harness install --client claude-code done");
        var synced = await api.FixAsync(id, new FixRequest { Sync = true }, Token);
        synced.Ok.Should().BeTrue(synced.Output);
        health = await api.HealthAsync(id, probe: false, Token);
        health.Status.Sync!.SyncedAt.Should().NotBeNull();
        health.Status.Sync.Stale.Should().BeNull();
        var run = health.Status.Sync.Runs.Should().NotBeEmpty().And.Subject.First();
        run.Status.Should().NotBeNullOrEmpty();
        run.HeadCommit.Should().Be(health.Status.Git!.Commit);
        await FluentActions
            .Awaiting(() => api.FixAsync(id, new FixRequest { Check = "config" }, Token))
            .Should()
            .ThrowAsync<HarnessApiException>();

        // the docs
        var docs = await api.DocsAsync(Token);
        docs.Docs.Select(d => d.Name).Should().Equal("guide", "reference");
        docs.Sections.Should().NotBeEmpty();

        // a change arrives on /api/events after a write
        using var follow = CancellationTokenSource.CreateLinkedTokenSource(Token);
        follow.CancelAfter(TimeSpan.FromSeconds(30));
        var opened = new TaskCompletionSource();
        var changes = api.ChangesAsync(() => opened.TrySetResult(), follow.Token).GetAsyncEnumerator(follow.Token);
        var next = changes.MoveNextAsync().AsTask();
        await opened.Task.WaitAsync(follow.Token);
        await api.PostAsync($"api/projects/{id}/rename", new RenameChange { Name = "fixture-short" }, UiJsonContext.Default.RenameChange, Token);
        (await next).Should().BeTrue("a change follows the write");
        (changes.Current.Registry || changes.Current.Projects.Contains(id)).Should().BeTrue();
        await follow.CancelAsync();
        await changes.DisposeAsync();
        (await api.ProjectsAsync(Token)).Single().Name.Should().Be("fixture-short");

        // a refusal carries the server's message
        var refused = await FluentActions
            .Awaiting(() => api.SetBoardAsync(id, new BoardChange { Sort = "nonsense" }, Token))
            .Should()
            .ThrowAsync<HarnessApiException>();
        refused.Which.Message.Should().Contain("400").And.Contain("nonsense");
    }
}
