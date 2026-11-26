using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Controls;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// A workspace's projects in the sidebar fold away (app.js ws-open.&lt;name&gt;): the chevron toggles them and remembers it,
/// a stored choice wins, a big workspace starts folded unless the page is in it, and a folded one shows its count and
/// its projects' states.
/// </summary>
public sealed class SidebarFoldTests
{
    private const string Ok =
        "\"live\":{\"state\":\"ok\",\"pending_proposals\":0,\"pending_arch_proposals\":0,\"pending_task_proposals\":0,\"pending_blueprint_proposals\":0,\"removed_from_project\":0,\"checks_failed\":0,\"checks_warned\":0}";

    private static StackPanel Row(Window window, string title) =>
        window.GetVisualDescendants().OfType<Sidebar>().Single().GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "WorkspaceList")
            .GetVisualDescendants().OfType<StackPanel>()
            .First(p => p.DataContext is NavWorkspace w && w.Title == title && p.Parent is ContentPresenter);

    private static ToggleButton Toggle(StackPanel row) => row.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Classes.Contains("twist"));

    private static bool MembersShown(StackPanel row) =>
        row.GetVisualDescendants().OfType<AppLink>().Any(l => l.Classes.Contains("member") && l.IsEffectivelyVisible);

    [Fact]
    public Task TheChevronFoldsAWorkspaceAndRemembersIt() => Ui.RunAsync(async () =>
    {
        var preferences = new InMemoryPreferences();
        var (window, _, model) = await Ui.OpenMainAsync(1280, 800, address: "#/", preferences: preferences);
        var row = Row(window, "Main workspace");
        var toggle = Toggle(row);
        toggle.IsChecked.Should().BeTrue("a small workspace starts open");
        Avalonia.Automation.AutomationProperties.GetName(toggle).Should().Be("Show or hide the projects of Main workspace");
        toggle.Bounds.Height.Should().BeGreaterThanOrEqualTo(24);
        MembersShown(row).Should().BeTrue();

        toggle.Command!.Execute(toggle.CommandParameter);
        Ui.Settle();
        preferences.Get("ws-open.main").Should().Be("0");
        toggle.IsChecked.Should().BeFalse();
        MembersShown(row).Should().BeFalse();
        // folded: its count beside the name and its projects by state under it
        row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("count") && t.IsEffectivelyVisible).Text.Should().Be("1");
        var summary = row.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Classes.Contains("nsum"));
        summary.IsEffectivelyVisible.Should().BeTrue();
        model.Workspaces.Single().States.Should().Equal(new ProjectStateCount("ok", 1));
        model.Workspaces.Single().States[0].Words.Should().Be("1 ok");
        row.GetVisualDescendants().OfType<Icon>().Single(i => i.Classes.Contains("twist")).Classes.Should().Contain("closed");

        // navigating and live reloads keep it folded; the name still opens the workspace
        model.Navigate("#/p/prj_fixture/board");
        await Ui.UntilAsync(() => model.Page is ProjectPage);
        Ui.Settle();
        Toggle(Row(window, "Main workspace")).IsChecked.Should().BeFalse("a stored choice wins over the page being in it");
        model.Navigate("#/w/main");
        await Ui.UntilAsync(() => model.Page is WorkspacePage);
        Ui.Settle();
        Toggle(Row(window, "Main workspace")).IsChecked.Should().BeFalse();

        // the keyboard: Space on the focused chevron opens it again
        toggle = Toggle(Row(window, "Main workspace"));
        toggle.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Ui.Settle();
        preferences.Get("ws-open.main").Should().Be("1");
        MembersShown(Row(window, "Main workspace")).Should().BeTrue();
        window.Close();
    });

    [Theory]
    [InlineData("#/", false)]
    [InlineData("#/w/big", true)]
    [InlineData("#/p/prj_b3/board", true)]
    public Task ABigWorkspaceStartsFoldedUnlessThePageIsInIt(string address, bool open) => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        var ids = Enumerable.Range(1, 10).Select(i => $"prj_b{i}").ToArray();
        api.Answers["/api/projects"] =
            "{\"projects\":[" + string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"title\":\"Big {id[5..]}\",\"root\":\"/{id}\",{Ok},\"workspaces\":[\"big\"]}}")) + "]}";
        api.Answers["/api/workspaces"] =
            "{\"workspaces\":[{\"id\":\"ws_big\",\"name\":\"big\",\"title\":\"Big workspace\",\"members\":[" + string.Join(",", ids.Select(id => $"{{\"project_id\":\"{id}\"}}")) + "]}]}";
        var (window, _, _) = await Ui.OpenMainAsync(1280, 800, api: api, address: address);
        Ui.Settle();

        var row = Row(window, "Big workspace");
        Toggle(row).IsChecked.Should().Be(open);
        MembersShown(row).Should().Be(open);
        window.Close();
    });

    [Fact]
    public Task AStoredChoiceIsAppliedOnLoad() => Ui.RunAsync(async () =>
    {
        var preferences = new InMemoryPreferences();
        preferences.Set("ws-open.main", "0");
        var (window, _, _) = await Ui.OpenMainAsync(1280, 800, address: "#/w/main", preferences: preferences);
        Ui.Settle();
        Toggle(Row(window, "Main workspace")).IsChecked.Should().BeFalse();
        window.Close();
    });

    [Theory]
    [InlineData(1280, false)]
    [InlineData(1280, true)]
    [InlineData(390, false)]
    [InlineData(390, true)]
    public Task AFoldedWorkspacePassesTheUiChecks(int width, bool dark) => Ui.RunAsync(async () =>
    {
        var preferences = new InMemoryPreferences();
        preferences.Set("ws-open.main", "0");
        var (window, _, _) = await Ui.OpenMainAsync(width, 844, address: "#/", preferences: preferences);
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var main = window.GetVisualDescendants().OfType<MainView>().Single();
        main.FindControl<ToggleButton>("Menu")!.IsChecked = true;
        Ui.Settle();
        UiChecks.Check(window).Should().BeEmpty();
        window.Close();
    });
}
