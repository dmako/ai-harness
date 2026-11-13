using AwesomeAssertions;
using Harness.Ui.Navigation;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>The router reads and builds every address app.js route()/projectHref()/workspaceHref() do (TASK-0146).</summary>
public sealed class RouteTests
{
    [Theory]
    [InlineData("", "home", "#/")]
    [InlineData("#/", "home", "#/")]
    [InlineData("#", "home", "#/")]
    [InlineData("#/p/prj_1", "project", "#/p/prj_1/overview")]
    [InlineData("#/p/prj_1/board", "project", "#/p/prj_1/board")]
    [InlineData("#/p/prj_1/nonsense", "project", "#/p/prj_1/overview")]
    [InlineData("#/p/prj_1/work/tasks", "project", "#/p/prj_1/work/tasks")]
    [InlineData("#/p/prj_1/work/TASK-0001", "project", "#/p/prj_1/work/TASK-0001")]
    [InlineData("#/w/main", "workspace", "#/w/main/overview")]
    [InlineData("#/w/main/issues/versions", "workspace", "#/w/main/issues/versions")]
    [InlineData("#/w/main/nonsense", "workspace", "#/w/main/overview")]
    [InlineData("#/knowledge", "knowledge", "#/knowledge")]
    [InlineData("#/machine", "machine", "#/machine")]
    [InlineData("#/search", "search", "#/search")]
    [InlineData("#/docs", "docs", "#/docs/guide")]
    [InlineData("#/docs/reference", "docs", "#/docs/reference")]
    public void EveryAddressFormReadsAndRoundTrips(string hash, string page, string href)
    {
        var route = Route.Parse(hash);

        route.Page.Should().Be(page);
        route.Moved.Should().BeFalse();
        route.Href.Should().Be(href);
        Route.Parse(route.Href).Should().Be(route, "an address built from a route reads back as the same route");
    }

    [Fact]
    public void AProjectWithoutAViewGetsTheChosenDefault()
    {
        Route.Parse("#/p/prj_1", defaultProjectView: "board").View.Should().Be("board");
        Route.Parse("#/p/prj_1", defaultProjectView: "nonsense").View.Should().Be("overview");
    }

    [Fact]
    public void ProjectItemsAreEncodedAsOnePieceAndWorkspaceItemsKeptAsWritten()
    {
        Route.ProjectHref("prj 1", "work", "tasks/TASK-1").Should().Be("#/p/prj%201/work/tasks%2FTASK-1");
        Route.Parse("#/p/prj%201/work/tasks%2FTASK-1").Should().BeEquivalentTo(new { Id = "prj 1", View = "work", Item = "tasks/TASK-1" });
        Route.WorkspaceHref("main", "issues", "shared").Should().Be("#/w/main/issues/shared");
    }

    [Fact]
    public void SearchKeepsItsQuery()
    {
        var route = Route.Parse("#/search?q=board%20view&x=1");

        route.Query.Should().Be("board view");
        route.Href.Should().Be("#/search?q=board%20view");
        Route.Parse("#/search?q=a+b").Query.Should().Be("a b");
    }

    [Fact]
    public void WorkspaceViewsThatMovedOpenWhereTheyAreNow()
    {
        Route.Parse("#/w/main/shared").Should().BeEquivalentTo(new { Page = "workspace", View = "issues", Item = "shared" });
        Route.Parse("#/w/main/libraries").Should().BeEquivalentTo(new { View = "architecture", Item = "libraries" });
    }

    [Theory]
    [InlineData("#/prj_1", "#/p/prj_1/overview")]
    [InlineData("#/prj_1/findings", "#/p/prj_1/overview")]
    [InlineData("#/prj_1/context", "#/p/prj_1/agents/context")]
    [InlineData("#/prj_1/providers", "#/p/prj_1/agents/tools")]
    [InlineData("#/prj_1/metrics", "#/p/prj_1/agents/tools")]
    [InlineData("#/prj_1/knowledge", "#/p/prj_1/knowledge")]
    [InlineData("#/prj_1/proposals", "#/p/prj_1/knowledge/inbox")]
    [InlineData("#/prj_1/tasks", "#/p/prj_1/work")]
    [InlineData("#/prj_1/tasks/TASK-0001", "#/p/prj_1/work/TASK-0001")]
    [InlineData("#/prj_1/health", "#/p/prj_1/health")]
    [InlineData("#/workspaces", "#/")]
    [InlineData("#/workspaces/main", "#/w/main/overview")]
    public void AnAddressOfTheEarlierLayoutOpensWhereItMoved(string legacy, string href)
    {
        var route = Route.Parse(legacy);

        route.Moved.Should().BeTrue();
        route.Href.Should().Be(href);
    }

    [Fact]
    public void InMemoryHistoryGoesBackAndForwardAndReplaces()
    {
        var history = new InMemoryNavigation("#/");
        var changes = 0;
        history.Changed += (_, _) => changes++;

        history.Go("#/p/a/board");
        history.Go("#/w/main");
        history.Back();
        history.Current.Href.Should().Be("#/p/a/board");
        history.Forward();
        history.Current.Href.Should().Be("#/w/main/overview");
        history.Back();
        history.Back();
        history.Current.Page.Should().Be("home");
        history.Back();
        history.Current.Page.Should().Be("home", "there is nothing before the first page");
        history.Replace("#/machine");
        history.Forward();
        history.Current.Href.Should().Be("#/p/a/board");
        changes.Should().Be(8, "every move but the Back with nothing before it");
    }
}
