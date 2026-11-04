using System.Net;
using AwesomeAssertions;
using Harness.Core.Tasks;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Sync;
using Harness.Integration.Web;
using Xunit;

namespace Harness.Integration.Tasks;

/// <summary>Every blueprint id harness generates or accepts for a new blueprint is valid: letters, an optional dash, digits.</summary>
[Collection("Provider processes")]
public sealed class BlueprintIdTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnInvalidIdLeftInTheDatabaseShapesNoGeneratedId()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);
        // as the bug of TASK-0075 left it: a blueprint only a task names, its title for an id
        store.Create(
            new TaskDefinition { Title = "Left behind", Blueprint = "Awasome Test 1" },
            "",
            new("test", "test", "fixture"),
            Token
        );

        store.NextBlueprintId(Token).Should().Be("BP-001");
        var proposal = store.ProposeBlueprint("", new("", "Awasome Test 312", "Try again"), "", "codex", Token);
        store
            .ApproveBlueprint(proposal.Id, null, "author", cancellationToken: Token)
            .Blueprint.Id.Should()
            .Be("BP-001");
        var task = store.Propose(
            new TaskDefinition
            {
                Title = "Into a new one",
                Goal = "Its blueprint is made by the approval",
                Acceptance = ["filed"],
                Blueprint = "Awasome Test 313",
            },
            "seen",
            "codex",
            Token
        );
        store.Approve(task.Id, null, "", "author", Token).Task.Definition.Blueprint.Should().Be("BP-002");

        store.NextBlueprintId(Token).Should().Be("BP-003");
        await using var server = await UiFixture.StartAsync(fixture);
        (await UiTests.ReadAsync(server.Client, $"/api/projects/{UiWriteTests.ProjectId(fixture)}/blueprints-next-id"))[
            "id"
        ]!
            .GetValue<string>()
            .Should()
            .Be("BP-003", "harness ui suggests a valid id too");
    }

    [Fact]
    public async Task ANewBlueprintWithAnInvalidIdIsRefusedWhileAnExistingWordIdStillWorks()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var store = new TaskStore(database);
        // a word id, as an older harness or a backlog import left it
        fixture.Sql(
            "INSERT INTO blueprints(id,title,goal,exit,notes,source,ordinal,updated_at) VALUES('Metrics','Code metrics','','[]','','import',0,'2026-01-01T00:00:00Z')"
        );

        foreach (var id in new[] { "Awasome Test 2", "Metrics2x", "Release", "BP-", "-12" })
        {
            FluentActions
                .Invoking(() => store.CreateBlueprint(new(id, "Some title"), "test", Token))
                .Should()
                .Throw<ArgumentException>()
                .WithMessage("*not valid: use letters, an optional dash and digits, e.g. BP-001*", id);
        }

        store.CreateBlueprint(new("M17", "Seventeen"), "test", Token).Id.Should().Be("M17");
        store.EditBlueprint("Metrics", new("Metrics", "Code metrics, restated", "Measured"), "test", Token);
        store.Blueprints(Token).Single(b => b.Definition.Id == "Metrics").Definition.Goal.Should().Be("Measured");

        var cli = await fixture.ProcessAsync(
            "dotnet",
            [UiFixture.CliPath, "tasks", "blueprints", "add", "--id", "Release", "--project", fixture.Root]
        );
        cli.Code.Should().Be(1);
        cli.Error.Should().Contain("not valid: use letters, an optional dash and digits");

        await using var server = await UiFixture.StartAsync(fixture);
        using var request = UiWriteTests.Request(
            server.Client,
            $"/api/projects/{UiWriteTests.ProjectId(fixture)}/blueprints",
            """{"id":"Bad id","title":"From harness ui"}"""
        );
        using var response = await server.Client.SendAsync(request, Token);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Token)).Should().Contain("blueprint id");
    }
}
