using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Tasks;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Sync;
using Harness.Integration.Web;
using Xunit;

namespace Harness.Integration.Tasks;

/// <summary>
/// An agent proposes a blueprint and tasks naming it by title; whichever the author approves first, the tasks end
/// up in one blueprint.
/// </summary>
[Collection("Provider processes")]
public sealed class BlueprintMatchingTests
{
    private const string Title = "Performance Optimizations";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TasksApprovedAfterTheirBlueprintJoinItByTitle()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);
        var blueprint = store.ProposeBlueprint("", new("", Title, "Fast views"), "measured", "codex", Token);
        var first = Propose(store, "performance optimizations");
        var second = Propose(store, Title);

        store
            .ApproveBlueprint(blueprint.Id, null, "author", cancellationToken: Token)
            .Blueprint.Id.Should()
            .Be("BP-001");

        store
            .Proposals("pending", Token)
            .Select(proposal => proposal.Task.Blueprint)
            .Should()
            .Equal(["BP-001", "BP-001"], "a pending proposal names its blueprint as the blueprints are now");
        store.Approve(first.Id, null, "", "author", Token).Task.Definition.Blueprint.Should().Be("BP-001");
        // the form sends the name the proposal had when the author leaves the box as it was
        var edited = store.GetProposal(second.Id, Token).Task with
        {
            Blueprint = Title,
        };
        store.Approve(second.Id, edited, "", "author", Token).Task.Definition.Blueprint.Should().Be("BP-001");
        store.Blueprints(Token).Should().ContainSingle().Which.Definition.Id.Should().Be("BP-001");
    }

    [Fact]
    public async Task ATaskApprovedFirstCreatesItsBlueprintAndTheBlueprintProposalAsksMergeOrNew()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);
        var merged = store.ProposeBlueprint(
            "",
            new("", Title, "Fast views", ["opens in a second"], "measured"),
            "",
            "codex",
            Token
        );
        var separate = store.ProposeBlueprint("", new("", Title, "Another plan"), "", "codex", Token);

        var task = store.Approve(Propose(store, Title).Id, null, "", "author", Token).Task;
        var made = store.Blueprints(Token).Single().Definition;

        (made.Id, made.Title, made.Goal).Should().Be(("BP-001", Title, ""), "defined, with the next free id");
        task.Definition.Blueprint.Should().Be("BP-001");
        store
            .Approve(Propose(store, "PERFORMANCE OPTIMIZATIONS").Id, null, "", "author", Token)
            .Task.Definition.Blueprint.Should()
            .Be("BP-001");

        FluentActions
            .Invoking(() => store.ApproveBlueprint(merged.Id, null, "author", cancellationToken: Token))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*exists already (BP-001): merge into it*create a separate one*");
        store.GetBlueprintProposal(merged.Id, Token).Status.Should().Be("pending", "nothing is guessed");

        var restated = store.ApproveBlueprint(merged.Id, null, "author", "merge", Token).Blueprint;
        (restated.Id, restated.Goal, restated.Notes).Should().Be(("BP-001", "Fast views", "measured"));
        restated.Exit.Should().Equal("opens in a second");
        store.Blueprints(Token).Single().Total.Should().Be(2, "its tasks stay");

        store.ApproveBlueprint(separate.Id, null, "author", "new", Token).Blueprint.Id.Should().Be("BP-002");
        store.Blueprints(Token).Select(b => b.Definition.Id).Should().Equal("BP-001", "BP-002");
    }

    [Fact]
    public async Task TheCommandLineAndHarnessUiTakeTheSameChoice()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);
        store.Approve(Propose(store, Title).Id, null, "", "author", Token);
        var cli = store.ProposeBlueprint("", new("", Title, "From the command line"), "", "codex", Token);
        var ui = store.ProposeBlueprint("", new("", Title, "From harness ui"), "", "codex", Token);
        async Task<(int Code, string Output, string Error)> HarnessAsync(params string[] args) =>
            await fixture.ProcessAsync("dotnet", [UiFixture.CliPath, "tasks", .. args, "--project", fixture.Root]);

        var refused = await HarnessAsync("approve", cli.Id);
        refused.Code.Should().Be(1);
        refused.Error.Should().Contain("exists already (BP-001)").And.Contain("same-name merge");
        var approved = await HarnessAsync("approve", cli.Id, "--same-name", "merge");
        approved.Code.Should().Be(0, approved.Error);
        approved.Output.Should().Contain("blueprint BP-001");
        store.Blueprints(Token).Single().Definition.Goal.Should().Be("From the command line");

        await using var server = await UiFixture.StartAsync(fixture);
        var path = $"/api/projects/{UiWriteTests.ProjectId(fixture)}/blueprint-proposals/{ui.Id}/approve";
        using (var request = UiWriteTests.Request(server.Client, path, "{}"))
        using (var response = await server.Client.SendAsync(request, Token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync(Token)).Should().Contain("exists already (BP-001)");
        }

        var created = await UiWriteTests.PostAsync(server.Client, path, """{"same_name":"new"}""");
        created["blueprint"]!.GetValue<string>().Should().Be("BP-002");
        store.Blueprints(Token).Should().HaveCount(2);
    }

    [Fact]
    public async Task ATitleEndingInANumberIsATitleWhicheverIsApprovedFirst()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);
        const string Numbered = "Awasome Test 1";
        var blueprint = store.ProposeBlueprint("", new("", Numbered, "Try it out"), "", "codex", Token);
        var waiting = Propose(store, Numbered);

        store
            .Approve(Propose(store, Numbered).Id, null, "", "author", Token)
            .Task.Definition.Blueprint.Should()
            .Be("BP-001");

        var made = store.Blueprints(Token).Should().ContainSingle().Subject.Definition;
        (made.Id, made.Title).Should().Be(("BP-001", Numbered), "harness makes the id; the name is its title");
        store.Proposals("pending", Token).Single(p => p.Id == waiting.Id).Task.Blueprint.Should().Be("BP-001");
        store.BlueprintProposals("pending", Token).Single().SameTitle.Should().Be("BP-001");
        async Task<(int Code, string Output, string Error)> HarnessAsync(params string[] args) =>
            await fixture.ProcessAsync("dotnet", [UiFixture.CliPath, "tasks", .. args, "--project", fixture.Root]);
        var listed = await HarnessAsync("proposals");
        listed.Code.Should().Be(0, listed.Error);
        listed.Output.Should().Contain("blueprint: BP-001").And.Contain("BP-001 has this title already");
        JsonNode.Parse((await HarnessAsync("proposals", "--json")).Output)!["blueprint_proposals"]![0]!["same_title"]!
            .GetValue<string>()
            .Should()
            .Be("BP-001");

        var merged = await HarnessAsync("approve", blueprint.Id, "--same-name", "merge");
        merged.Code.Should().Be(0, merged.Error);
        store.Approve(waiting.Id, null, "", "author", Token).Task.Definition.Blueprint.Should().Be("BP-001");
        var only = store.Blueprints(Token).Should().ContainSingle().Subject;
        (only.Definition.Id, only.Definition.Goal, only.Total).Should().Be(("BP-001", "Try it out", 2));
    }

    [Theory]
    [InlineData("M17", "M17")]
    [InlineData("BP-007", "BP-007")]
    [InlineData("Release 2", "BP-001")]
    [InlineData("Phase-2 cleanup", "BP-001")]
    [InlineData("v2", "v2")]
    public async Task OnlyAWellFormedIdMatchingNoBlueprintStaysAnId(string named, string filed)
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);

        var task = store.Approve(Propose(store, named).Id, null, "", "author", Token).Task;

        task.Definition.Blueprint.Should().Be(filed);
        var blueprint = store.Blueprints(Token).Should().ContainSingle().Subject;
        if (filed == named)
        {
            blueprint.Defined.Should().BeFalse("a well-formed id stays one only tasks name, as before");
        }
        else
        {
            (blueprint.Defined, blueprint.Definition.Title).Should().Be((true, named));
        }
    }

    private static TaskProposal Propose(TaskStore store, string blueprint) =>
        store.Propose(
            new TaskDefinition
            {
                Title = "Measure the views " + Guid.NewGuid().ToString("N")[..6],
                Goal = "Know where the time goes",
                Acceptance = ["Measured"],
                Blueprint = blueprint,
            },
            "seen",
            "codex",
            Token
        );
}
