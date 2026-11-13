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
