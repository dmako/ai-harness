using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Api;
using Harness.Ui.Controls;
using Harness.Ui.Services;
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
    [Theory]
    [InlineData(390)]
    [InlineData(1280)]
    public Task HeaderSpansTheWindowAndBreadcrumbsFollowNavigation(int width) => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await Ui.OpenMainAsync(width, 800, address: "#/p/prj_fixture/architecture");
        var main = window.GetVisualDescendants().OfType<MainView>().Single();
        var header = main.FindControl<Border>("Header")!;
        var frame = main.FindControl<Grid>("Frame")!;
        header.Bounds.Width.Should().BeApproximately(frame.Bounds.Width, 1);
        model.Breadcrumbs.Select(c => c.Label).Should().Equal("Home", "Fixture project", "Architecture");
        model.Breadcrumbs[1].Href.Should().Be("#/p/prj_fixture/overview");
        model.Navigate("#/w/main/members");
        await Ui.UntilAsync(() => model.Route.Page is "workspace");
        model.Breadcrumbs.Select(c => c.Label).Should().Equal("Home", "Main workspace", "Members");
        var breadcrumbs = main.FindControl<ItemsControl>("Breadcrumbs")!;
        var home = breadcrumbs.GetVisualDescendants().OfType<AppLink>().First(link => link.Href == "#/");
        main.FindControl<Image>("BrandIcon")!.Source.Should().NotBeNull();
        AutomationProperties.GetName(home).Should().Be("Home");
        home.GetVisualDescendants().OfType<Icon>().Should().Contain(icon => icon.Kind == "home" && icon.IsVisible);
        breadcrumbs.GetVisualDescendants().OfType<TextBlock>()
            .Count(text => text.Text == "›" && text.IsVisible).Should().Be(2);
        home.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        model.Route.Page.Should().Be("home");
        window.Close();
    });

    [Fact]
    public Task ProjectHeaderShowsIdentityHealthAndStack() => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await Ui.OpenMainAsync(1280, 800, address: "#/p/prj_fixture/architecture");
        await Ui.UntilAsync(() => model.Page is ProjectPage { Stack: not null });
        var page = (ProjectPage)model.Page!;
        var view = window.GetVisualDescendants().OfType<ProjectPageView>().Single();
        view.GetVisualDescendants().OfType<Border>().Should().Contain(mark => mark.Classes.Contains("project-mark"));
        view.GetVisualDescendants().OfType<AppLink>().Should().Contain(link => link.Href == page.HealthHref);
        page.Project.Mark.Should().Be("FP");
        page.Project.Live.HealthWords.Should().Be("1 warning");
        page.Stack!.Language.Should().Be("C#");
        window.Close();
    });

    [Theory]
    [InlineData("ok", 0, 0, null, "healthy", "ok")]
    [InlineData("ok", 1, 0, null, "1 check failing", "bad")]
    [InlineData("ok", 0, 2, null, "2 warnings", "warn")]
    [InlineData("ok", 0, 0, "stale", "index stale", "warn")]
    [InlineData("missing", 0, 0, null, "missing", "bad")]
    public void ProjectHealthUsesCurrentState(string state, int failed, int warned, string? stale, string words, string kind)
    {
        var live = new ProjectLive
        {
            State = state, ChecksFailed = failed, ChecksWarned = warned, Stale = stale,
            PendingProposals = 0, PendingArchProposals = 0, PendingTaskProposals = 0,
            PendingBlueprintProposals = 0, RemovedFromProject = 0,
        };
        live.HealthWords.Should().Be(words);
        live.HealthKind.Should().Be(kind);
    }

    [Fact]
    public Task HeaderThemeChoicesPersistAndApply() => Ui.RunAsync(async () =>
    {
        var preferences = new InMemoryPreferences();
        var (window, _, model) = await Ui.OpenMainAsync(1280, 800, address: "#/", preferences: preferences);
        var view = window.GetVisualDescendants().OfType<MainView>().Single();
        var button = view.FindControl<Button>("ThemeButton")!;
        var flyout = button.Flyout.Should().BeOfType<MenuFlyout>().Subject;
        flyout.ShowAt(button);
        Ui.Settle();
        flyout.Items.OfType<MenuItem>().Select(item => item.Header).Should().Equal("System", "Light", "Dark");
        foreach (var (name, variant) in new[] { ("dark", ThemeVariant.Dark), ("light", ThemeVariant.Light), ("system", ThemeVariant.Default) })
        {
            var item = flyout.Items.OfType<MenuItem>().Single(choice => (string)choice.CommandParameter! == name);
            item.Command.Should().NotBeNull();
            item.Command.Execute(item.CommandParameter);
            preferences.Get("theme").Should().Be(name);
            Avalonia.Application.Current!.RequestedThemeVariant.Should().Be(variant);
        }
        model.Navigate("#/machine");
        await Ui.UntilAsync(() => model.Page is MachinePage);
        model.SelectThemeCommand.Execute("dark");
        ((MachinePage)model.Page!).Theme.Value.Should().Be("dark", "the Machine picker stays in sync");
        window.Close();
        var (reopened, _, _) = await Ui.OpenMainAsync(1280, 800, address: "#/", preferences: preferences);
        Avalonia.Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
        reopened.Close();
        Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Default;
    });

    [Fact]
    public Task LiveIndicatorShowsOfflineAndConnectedStates() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Failures["/api/events"] = System.Net.HttpStatusCode.ServiceUnavailable;
        var (window, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, fastLive: true, address: "#/");
        var view = window.GetVisualDescendants().OfType<MainView>().Single();
        var indicator = view.FindControl<TextBlock>("LiveState")!;
        await Ui.UntilAsync(() => model.Services.Live.State is "offline");
        indicator.Classes.Should().Contain("offline");
        indicator.Classes.Should().NotContain("connected");
        indicator.TryFindResource("Critical", indicator.ActualThemeVariant, out var offline).Should().BeTrue();
        indicator.Foreground.Should().Be(offline);
        api.Failures.Remove("/api/events");
        await Ui.UntilAsync(() => model.Services.Live.State is "live");
        indicator.Classes.Should().Contain("connected");
        indicator.Classes.Should().NotContain("offline");
        indicator.TryFindResource("GoodInk", indicator.ActualThemeVariant, out var connected).Should().BeTrue();
        indicator.Foreground.Should().Be(connected);
        window.Close();
    });

    [Fact]
    public Task FindHighlightsAndStepsThroughTheCurrentView() => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await Ui.OpenMainAsync(1280, 800, address: "#/");
        var view = window.GetVisualDescendants().OfType<MainView>().Single();
        view.GetVisualDescendants().OfType<AppLink>().First(link => link.IsEffectivelyVisible).Focus();
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        var bar = view.FindControl<Border>("FindBar")!;
        bar.IsVisible.Should().BeTrue();
        var input = view.FindControl<TextBox>("FindInput")!;
        input.Text = "fixture";
        Ui.Settle();
        var hits = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("find-hit")).ToArray();
        hits.Should().NotBeEmpty();
        var first = hits.Single(t => t.Classes.Contains("find-current"));
        var next = view.FindControl<Button>("FindNext")!;
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (hits.Length > 1)
        {
            first.Classes.Should().NotContain("find-current");
        }
        input.Text = "e";
        Ui.Settle();
        var count = view.FindControl<TextBlock>("FindCount")!.Text!;
        var blocks = view.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Classes.Contains("find-hit"));
        int.Parse(count.Split(" of ", StringSplitOptions.None)[1], System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(blocks, "each occurrence in a text block is a separate match");
        view.FindControl<TextBlock>("FindPreview")!.Inlines.Should().HaveCount(3);
        model.Navigate("#/p/prj_fixture/overview");
        await Ui.UntilAsync(() => model.Page is ProjectPage { View: "overview" });
        input.Text.Should().BeEmpty("a new view must not keep matches from the previous one");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        bar.IsVisible.Should().BeFalse();
        view.GetVisualDescendants().OfType<TextBlock>().Should().OnlyContain(t => !t.Classes.Contains("find-hit"));
    });

    [Fact]
    public Task FindPassesTheUiChecks() => Ui.RunAsync(() => UiChecks.AssertAsync(async (width, height) =>
    {
        var (window, _, _) = await Ui.OpenMainAsync(width, height, address: "#/");
        var view = window.GetVisualDescendants().OfType<MainView>().Single();
        MainView.RequestFind();
        view.FindControl<TextBox>("FindInput")!.Text = "project";
        Ui.Settle();
        return window;
    }));
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
        var menu = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "Menu");
        sidebar.IsVisible.Should().BeFalse();
        menu.IsVisible.Should().BeTrue();

        menu.IsChecked = true;
        sidebar.IsVisible.Should().BeTrue();
        await Ui.UntilAsync(() => sidebar.Bounds.Width > 0);
        Follow(window, "#/p/prj_fixture/overview");
        await Ui.UntilAsync(() => model.Page is ProjectPage);
        sidebar.IsVisible.Should().BeFalse();
    });

    [Theory]
    [InlineData(390, false)]
    [InlineData(390, true)]
    [InlineData(1280, false)]
    [InlineData(1280, true)]
    public Task NavigationToggleReclaimsSpaceAndWorksWithKeyboard(int width, bool dark) => Ui.RunAsync(async () =>
    {
        var (window, _, _) = await Ui.OpenMainAsync(width, 844, address: "#/");
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var main = window.GetVisualDescendants().OfType<MainView>().Single();
        var sidebar = main.FindControl<Border>("Sidebar")!;
        var menu = main.FindControl<ToggleButton>("Menu")!;
        var page = main.FindControl<ScrollViewer>("PageScroller")!;
        AutomationProperties.GetName(menu).Should().Be("Navigation");
        menu.Focus();
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Ui.Settle();
        sidebar.IsVisible.Should().Be(width < 760);
        menu.IsChecked.Should().Be(sidebar.IsVisible);
        var firstWidth = page.Bounds.Width;
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Ui.Settle();
        sidebar.IsVisible.Should().Be(width >= 760);
        menu.IsChecked.Should().Be(sidebar.IsVisible);
        if (width >= 760)
        {
            firstWidth.Should().BeGreaterThan(page.Bounds.Width);
        }
        window.Close();
    });

    /// <summary>Clicks the link to <paramref name="href"/> on the page (in the sidebar first).</summary>
    private static void Follow(Window window, string href) =>
        window
            .GetVisualDescendants()
            .OfType<AppLink>()
            .First(l => l.Href == href && l.IsEffectivelyVisible)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
