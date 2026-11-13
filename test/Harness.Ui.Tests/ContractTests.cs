using System.Text.Json;
using AwesomeAssertions;
using Harness.Ui.Api;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The contracts are strict (TASK-0149): an answer that lacks a required member, renames one, or sends null where the
/// contract says it cannot be, fails to deserialise instead of showing an empty page. UiContractTests
/// (Harness.Integration) checks the same contracts against a real harness ui.
/// </summary>
public sealed class ContractTests
{
    [Fact]
    public void TheFixturesDeserialiseUnderTheStrictContracts()
    {
        JsonSerializer.Deserialize(Fixtures.Board(), UiJsonContext.Default.BoardResponse)!.Columns.Should().HaveCount(7);
        JsonSerializer.Deserialize(Fixtures.Projects, UiJsonContext.Default.ProjectList)!.Projects.Should().HaveCount(2);
        JsonSerializer.Deserialize(Fixtures.Read("api/workspaces.json"), UiJsonContext.Default.WorkspaceList)!.Workspaces.Should().ContainSingle();
    }

    [Theory]
    [InlineData("columns")]
    [InlineData("settings")]
    [InlineData("blueprints")]
    public void ABoardWithoutARequiredMemberFails(string member)
    {
        var board = System.Text.Json.Nodes.JsonNode.Parse(Fixtures.Board())!.AsObject();
        board.Remove(member);

        FluentActions
            .Invoking(() => JsonSerializer.Deserialize(board.ToJsonString(), UiJsonContext.Default.BoardResponse))
            .Should()
            .Throw<JsonException>()
            .WithMessage($"*{member}*");
    }

    [Fact]
    public void ARenamedMemberFailsAsAMissingOne()
    {
        var renamed = Fixtures.Projects.Replace("\"checks_failed\"", "\"failed_checks\"", StringComparison.Ordinal);

        FluentActions
            .Invoking(() => JsonSerializer.Deserialize(renamed, UiJsonContext.Default.ProjectList))
            .Should()
            .Throw<JsonException>()
            .WithMessage("*checks_failed*");
    }

    [Fact]
    public void NullWhereTheContractSaysItCannotBeFails()
    {
        var nulled = Fixtures.Board().Replace("\"status\":\"ready\"", "\"status\":null", StringComparison.Ordinal);

        FluentActions
            .Invoking(() => JsonSerializer.Deserialize(nulled, UiJsonContext.Default.BoardResponse))
            .Should()
            .Throw<JsonException>();
    }

    [Fact]
    public void OptionalMembersMayBeMissing()
    {
        var project = """{"projects":[{"id":"p","root":"/r","live":{"state":"ok","pending_proposals":0,"pending_arch_proposals":0,"pending_task_proposals":0,"pending_blueprint_proposals":0,"removed_from_project":0,"checks_failed":0,"checks_warned":0}}]}""";

        var read = JsonSerializer.Deserialize(project, UiJsonContext.Default.ProjectList)!.Projects.Single();

        read.Title.Should().BeNull();
        read.Workspaces.Should().BeEmpty();
        read.Live.Kind.Should().Be("ok");
        read.DisplayName.Should().Be("r");
    }
}
