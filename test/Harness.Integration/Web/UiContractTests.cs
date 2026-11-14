using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Tasks;
using Harness.Integration.Sync;
using Harness.Ui.Api;
using Xunit;
using BlueprintApproval = Harness.Ui.Api.BlueprintApproval;
using BlueprintRemoval = Harness.Ui.Api.BlueprintRemoval;

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
        tasks.Select(t => (t.Id, t.Status))
            .Should()
            .BeEquivalentTo([("TASK-0001", "ready"), ("TASK-0002", "done"), ("TASK-0003", "claimed"), ("TASK-0004", "backlog")]);
        tasks.Single(t => t.Id == "TASK-0001").Acceptance.Should().ContainSingle(c => c.Criterion == "It runs" && c.Result == "pending");
        tasks.Single(t => t.Id == "TASK-0003").Claim!.Claimant.Should().Be("claude-code");
        tasks.Single(t => t.Id == "TASK-0004").DependsOn.Should().ContainSingle(d => d.Id == "TASK-0001" && d.Status == "ready");
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

        // Work: the reads, then every write the view sends
        var blueprints = (await api.BlueprintsAsync(id, Token)).Blueprints;
        blueprints.Select(b => b.Id).Should().Equal("BP-009", "BP-008");
        blueprints[0].Exit.Should().Equal("Released", "Documented");
        blueprints[0].Tasks.Should().ContainKey("ready");
        (await api.NextTaskIdAsync(id, Token)).Id.Should().Be("TASK-0005");
        (await api.NextBlueprintIdAsync(id, Token)).Id.Should().Be("BP-010");
        (await api.TaskEventsAsync(id, "TASK-0001", Token)).Events.Should().Contain(e => e.Event == "unlocked" && e.To == "ready");
        (await api.TaskSessionsAsync(id, "TASK-0001", Token)).Sessions.Should().BeEmpty();
        var taskProposal = (await api.TaskProposalsAsync(id, "pending", Token)).Proposals.Should().ContainSingle().Subject;
        taskProposal.Task.DependsOn.Should().Equal("TASK-0001");
        var blueprintProposal = (await api.BlueprintProposalsAsync(id, "pending", Token)).Proposals.Should().ContainSingle().Subject;
        blueprintProposal.Blueprint.Exit.Should().Equal("Done when shipped");
        var archProposal = (await api.ArchProposalsAsync(id, "pending", Token)).Proposals.Should().ContainSingle().Subject;
        archProposal.Basis.Should().Be("observation");

        await api.CreateBlueprintAsync(id, new BlueprintForm { Id = "BP-010", Title = "Offline", Exit = ["Works offline"], Color = "teal" }, Token);
        await api.EditBlueprintAsync(id, "BP-010", new BlueprintForm { Title = "Offline search", Notes = "- one\n- two" }, Token);
        await api.CreateTaskAsync(
            id,
            new TaskForm { Title = "Search offline", Type = "bug", Priority = "high", Blueprint = "BP-010", DependsOn = ["TASK-0001"], Acceptance = ["Works"], Scope = ["src"] },
            Token
        );
        await api.EditTaskAsync(id, "TASK-0005", new TaskForm { Title = "Search offline, restated", Blueprint = "BP-010", Acceptance = ["Works"] }, Token);
        await api.MoveTaskAsync(id, "TASK-0005", "BP-009", Token);
        await api.OrderBlueprintsAsync(id, ["BP-010", "BP-009", "BP-008"], Token);
        var created = (await api.TasksAsync(id, Token)).Tasks.Single(t => t.Id == "TASK-0005");
        (created.Title, created.Blueprint, created.Type, created.Priority).Should().Be(("Search offline, restated", "BP-009", "feature", "medium"));
        var ordered = (await api.BlueprintsAsync(id, Token)).Blueprints;
        ordered.Select(b => b.Id).Should().Equal("BP-010", "BP-009", "BP-008");
        (ordered[0].Title, ordered[0].Color, ordered[0].Notes).Should().Be(("Offline search", "teal", "- one\n- two"));
        await api.RemoveBlueprintAsync(id, "BP-010", new BlueprintRemoval(), Token);
        await api.DeleteTaskAsync(id, "TASK-0005", Token);
        (await api.TasksAsync(id, Token)).Tasks.Should().NotContain(t => t.Id == "TASK-0005");

        await api.ApproveTaskAsync(id, taskProposal.Id, new TaskForm { Title = "Proposed task", Blueprint = "BP-009", Acceptance = ["Checked"] }, Token);
        await api.ApproveBlueprintAsync(id, blueprintProposal.Id, new BlueprintApproval { Edit = new BlueprintForm { Title = "Proposed blueprint" } }, Token);
        await api.RejectAsync(id, "arch", archProposal.Id, "not now", Token);
        (await api.TaskProposalsAsync(id, "approved", Token)).Proposals.Should().ContainSingle(p => p.Result != null);
        (await api.BlueprintProposalsAsync(id, "approved", Token)).Proposals.Should().ContainSingle();
        (await api.ArchProposalsAsync(id, "rejected", Token)).Proposals.Should().ContainSingle(p => p.Note == "not now");
        await FluentActions
            .Awaiting(() => api.EditTaskAsync(id, "TASK-0003", new TaskForm { Title = "Claimed" }, Token))
            .Should()
            .ThrowAsync<HarnessApiException>("a claimed task's definition is fixed");

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
