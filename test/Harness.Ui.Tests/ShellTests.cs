using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Controls;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The page frame (TASK-0146): the sidebar's workspaces and projects with their states and badges, Home, the project
/// and workspace headers and tabs, following links, Back, unknown pages, and the UI checks on each page.
/// </summary>
public sealed class ShellTests
{
    [Fact]
    public Task TheSidebarListsWorkspacesAndProjectsWithWhatWaits() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, address: "#/");

        var workspace = model.Workspaces.Should().ContainSingle().Subject;
        workspace.Title.Should().Be("Main workspace");
        workspace.Waiting.Should().Be(3, "the fixture project waits with 1 knowledge and 2 task proposals");
        workspace.Members.Should().ContainSingle(p => p.Name == "Fixture project" && p.StateKind == "ok" && p.Waiting == 3);
        var other = model.Outside.Should().ContainSingle().Subject;
        other.Name.Should().Be("other", "a project without a title is called by its folder");
        other.StateKind.Should().Be("bad");
        other.Waiting.Should().Be(2, "a failing check, and its state cannot be read");
    });

    [Fact]
    public Task HomeShowsTheTilesTheWorkspacesAndTheProjectsOutside() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, address: "#/");

        var home = model.Page.Should().BeOfType<HomePage>().Subject;
        home.Tiles.Select(t => (t.Label, t.Value)).Should().Equal(("Projects", "2"), ("Waiting for you", "5"), ("Proposals to review", "3"), ("Failing checks", "1"));
        home.Tiles[0].Note.Should().Be("1 unreadable");
        home.WorkspaceCards.Should().ContainSingle(w => w.Title == "Main workspace");
        home.Outside.Should().ContainSingle(p => p.Name == "other");
        home.OutsideTitle.Should().Be("Projects outside workspaces");
    });

    [Fact]
    public Task FollowingTheSidebarAndTabsNavigatesAndBackReturns() => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await Ui.OpenMainAsync(1280, 800, address: "#/");

        Follow(window, "#/p/prj_fixture/overview");
        await Ui.UntilAsync(() => model.Page is ProjectPage { View: "overview" });
        var page = (ProjectPage)model.Page!;
        page.Body.Should().BeOfType<OverviewViewModel>();
        page.Tabs.Select(t => t.Label).Should().Equal("Overview", "Architecture", "Knowledge", "Work", "Board", "Agents", "Health");
        page.Tabs.Single(t => t.View == "knowledge").Badge.Should().Be(1);
        page.Tabs.Single(t => t.View == "work").Badge.Should().Be(2);
        model.Workspaces[0].Members[0].Current.Should().BeTrue("the sidebar marks the shown project");

        Follow(window, "#/p/prj_fixture/board");
        await Ui.UntilAsync(() => model.Board is { Columns.Count: > 0 });
        model.Page.Should().BeSameAs(page, "a view of the same project keeps the page and its header");
        window.GetVisualDescendants().OfType<BoardView>().Should().ContainSingle();

        model.Navigation.Back();
        await Ui.UntilAsync(() => page.View is "overview");
        model.Navigation.Back();
        await Ui.UntilAsync(() => model.Page is HomePage);
    });

    [Fact]
    public Task AWorkspacePageShowsItsNameToCopyAndItsTabs() => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await Ui.OpenMainAsync(1280, 800, address: "#/w/main/members");

        var page = model.Page.Should().BeOfType<WorkspacePage>().Subject;
        page.Copy.Should().Be("workspace:main");
        page.Tabs.Should().ContainSingle(t => t.Selected).Which.View.Should().Be("members");
        page.Path.Should().StartWith("1 project, created ");
        window.GetVisualDescendants().OfType<Button>().Should().Contain(b => b.Name == "CopyName" && (string?)b.Content == "workspace:main");
    });

    [Fact]
    public Task UnknownPagesSayWhatJavaScriptSays() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, address: "#/p/prj_gone/board");
        model.Page.Should().BeOfType<MessagePage>().Which.Message.Should().Be("Project prj_gone is not registered on this machine.");

        model.Navigate("#/w/nowhere");
        await Ui.UntilAsync(() => model.Page is MessagePage { Message: "There is no workspace nowhere on this machine." });

        model.Navigate("#/p/prj_other/work");
        await Ui.UntilAsync(() => model.Page is ProjectPage);
        ((ProjectPage)model.Page).Body.Should().BeOfType<UnreadableBody>("its state is missing").Which.HealthHref.Should().Be("#/p/prj_other/health");
    });

    [Fact]
    public Task AnEarlierLayoutsAddressIsReplacedByWhereItMoved() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, address: "#/prj_fixture/proposals");

        model.Navigation.Current.Href.Should().Be("#/p/prj_fixture/knowledge/inbox");
        model.Page.Should().BeOfType<ProjectPage>().Which.View.Should().Be("knowledge");
    });

    [Fact]
    public Task RenamingAProjectReviewsAndSendsTheChange() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await Ui.OpenMainAsync(1280, 800, address: "#/p/prj_fixture/overview");
        var page = (ProjectPage)model.Page!;

        _ = page.RenameCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => model.Services.Dialogs.Current is RenameDialog);
        var dialog = (RenameDialog)model.Services.Dialogs.Current!;
        dialog.Short = "fix ture";
        await dialog.AcceptCommand.ExecuteAsync(null);
        dialog.Error.Should().Contain("Short name");
        dialog.Short = "fixture2";
        await dialog.AcceptCommand.ExecuteAsync(null);
        dialog.ReviewText.Should().Be("Short name: fixture → fixture2");
        await dialog.AcceptCommand.ExecuteAsync(null);

        var post = api.Posts.Should().ContainSingle().Subject;
        post.Path.Should().Be("/api/projects/prj_fixture/rename");
        post.Body.Should().Be("""{"name":"fixture2"}""");
        await Ui.UntilAsync(() => !model.Services.Dialogs.IsOpen);
        model.Services.Toasts.Items.Should().Contain(t => t.Text == "Renamed");
    });

    [Fact]
    public Task ARegistryChangeReloadsTheSidebar() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, fastLive: true, address: "#/");
        api.Answers["/api/projects"] = Fixtures.Projects.Replace("\"prj_other\"", "\"prj_third\"", StringComparison.Ordinal);

        model.Changed(new Harness.Ui.Api.LiveChange { Registry = true });

        await Ui.UntilAsync(() => model.Outside.Count is 1 && model.Outside[0].Id is "prj_third");
        model.Outside.Should().ContainSingle(p => p.Id == "prj_third");
    });

    [Theory]
    [InlineData("#/")]
    [InlineData("#/p/prj_fixture/overview")]
    [InlineData("#/w/main/overview")]
    [InlineData("#/p/gone/overview")]
    public Task EveryPagePassesTheUiChecks(string address) =>
        Ui.RunAsync(() => UiChecks.AssertAsync(async (width, height) => (await Ui.OpenMainAsync(width, height, address: address)).Window));

    [Fact]
    public Task OnANarrowScreenTheSidebarOpensOverThePageAndClosesOnALink() => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await Ui.OpenMainAsync(390, 844, address: "#/");
        var sidebar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Sidebar");
        var menu = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Menu");
        sidebar.IsVisible.Should().BeFalse();
        menu.IsVisible.Should().BeTrue();

        menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        sidebar.IsVisible.Should().BeTrue();
        await Ui.UntilAsync(() => sidebar.Bounds.Width > 0);
        Follow(window, "#/p/prj_fixture/overview");
        await Ui.UntilAsync(() => model.Page is ProjectPage);
        sidebar.IsVisible.Should().BeFalse();
    });

    /// <summary>Clicks the link to <paramref name="href"/> on the page (in the sidebar first).</summary>
    private static void Follow(Window window, string href) =>
        window
            .GetVisualDescendants()
            .OfType<AppLink>()
            .First(l => l.Href == href && l.IsEffectivelyVisible)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
