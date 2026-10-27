using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Tasks;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Sync;
using Harness.Integration.Web;
using Xunit;

namespace Harness.Integration.Tasks;

[Collection("Provider processes")]
public sealed class BlueprintCliTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BlueprintsAreAddedEditedRemovedAndTheirProposalsDecidedFromTheCommandLine()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);
        async Task<(int Code, string Output, string Error)> HarnessAsync(params string[] args) =>
            await fixture.ProcessAsync("dotnet", [UiFixture.CliPath, "tasks", .. args, "--project", fixture.Root]);

        var added = await HarnessAsync("blueprints", "add", "--goal", "First goal", "--exit", "one", "--exit", "two");
        added.Code.Should().Be(0, added.Error);
        added.Output.Should().Contain("Added blueprint BP-001: New blueprint");
        var named = await HarnessAsync("blueprints", "add", "--id", "M7", "--title", "Seven", "--json");
        named.Code.Should().Be(0, named.Error);
        JsonNode.Parse(named.Output)!["blueprint"]!["id"]!.GetValue<string>().Should().Be("M7");

        var edited = await HarnessAsync("blueprints", "edit", "BP-001", "--title", "First", "--exit", "");
        edited.Code.Should().Be(0, edited.Error);
        var first = store.Blueprints(Token).Single(b => b.Definition.Id == "BP-001").Definition;
        (first.Title, first.Goal).Should().Be(("First", "First goal"), "what is not given stays");
        first.Exit.Should().BeEmpty();
        (await HarnessAsync("blueprints", "edit", "BP-404", "--title", "x")).Code.Should().Be(1);

        store.Create(
            new TaskDefinition
            {
                Id = "T-0001",
                Title = "Inside",
                Blueprint = "BP-001",
            },
            "",
            new("test", "test", "fixture"),
            Token
        );
        var refused = await HarnessAsync("blueprints", "remove", "BP-001");
        refused.Code.Should().Be(1);
        refused.Error.Should().Contain("1 task(s)");
        (await HarnessAsync("blueprints", "remove", "BP-001", "--tasks", "keep")).Code.Should().Be(2);
        var moved = await HarnessAsync("blueprints", "remove", "BP-001", "--to", "M7");
        moved.Code.Should().Be(0, moved.Error);
        moved.Output.Should().Contain("Moved to M7: T-0001");
        store.Get("T-0001", Token).Definition.Blueprint.Should().Be("M7");
        var deleted = await HarnessAsync("blueprints", "remove", "M7", "--tasks", "delete", "--json");
        deleted.Code.Should().Be(0, deleted.Error);
        JsonNode.Parse(deleted.Output)!["deleted"]![0]!.GetValue<string>().Should().Be("T-0001");
        store.Blueprints(Token).Should().BeEmpty();

        var proposal = store.ProposeBlueprint("", new("", "Proposed", "Its goal"), "seen in review", "agent", Token);
        var rejected = store.ProposeBlueprint("", new("", "Not this", "goal"), "", "agent", Token);
        var listed = await HarnessAsync("proposals");
        listed.Code.Should().Be(0, listed.Error);
        listed
            .Output.Should()
            .Contain($"{proposal.Id}  [pending] new blueprint: Proposed")
            .And.Contain("why:  seen in review");
        var json = JsonNode.Parse((await HarnessAsync("proposals", "--json")).Output)!;
        json["blueprint_proposals"]!.AsArray().Should().HaveCount(2);
        (await HarnessAsync("approve", proposal.Id)).Output.Should().Contain("blueprint BP-001 Proposed");
        (await HarnessAsync("reject", rejected.Id, "--note", "no")).Code.Should().Be(0);
        store.GetBlueprintProposal(rejected.Id, Token).Note.Should().Be("no");
        store.Blueprints(Token).Select(b => b.Definition.Title).Should().Equal("Proposed");
    }
}
