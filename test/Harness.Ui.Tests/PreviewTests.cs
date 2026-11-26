using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Controls;
using Harness.Ui.Preview;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

public sealed class PreviewTests
{
#if DEBUG
    [Fact]
    public Task PreviewModelsUseOfflineFixturesAndDoNotSetRuntimeDataContext() => Ui.RunAsync(async () =>
    {
        var home = PreviewData.Home;
        home.Tiles.Should().NotBeEmpty();
        home.WorkspaceCards.Should().NotBeEmpty();
        var board = PreviewData.Board;
        var architecture = PreviewData.Architecture;
        var docs = PreviewData.Docs;
        await Ui.UntilAsync(() => board.HasContent && architecture.Structure.HasContent && docs.Doc is not null);
        board.Columns.Should().NotBeEmpty();
        architecture.Structure.Rows.Should().NotBeEmpty();
        docs.Groups.Should().NotBeEmpty();
        docs.Doc!.Body.Should().NotBeNullOrEmpty();
        var boardWindow = Ui.Show(new BoardView { DataContext = board }, 1280, 800);
        boardWindow.GetVisualDescendants().OfType<TaskCard>().Should().NotBeEmpty();
        boardWindow.Close();
        var architectureWindow = Ui.Show(new ArchitectureView { DataContext = architecture }, 1280, 800);
        architectureWindow.GetVisualDescendants().OfType<TextBlock>().Should().Contain(text => text.Text == "Built on");
        architectureWindow.Close();
        var docsWindow = Ui.Show(new DocsView { DataContext = docs }, 1280, 800);
        docsWindow.GetVisualDescendants().OfType<DocsMarkdown>().Single().Markdown.Should().NotBeNullOrEmpty();
        docsWindow.Close();
        var theme = Application.Current!.RequestedThemeVariant;
        new MainView().DataContext.Should().BeNull();
        Application.Current.RequestedThemeVariant.Should().Be(theme);
        new HomeView().DataContext.Should().BeNull();
        new ArchitectureView().DataContext.Should().BeNull();
        var runtimeContext = new HomePage();
        new HomeView { DataContext = runtimeContext }.DataContext.Should().BeSameAs(runtimeContext);
        board.Dispose();
        architecture.Dispose();
    });

    [Fact]
    public Task WorkspaceAndReusableViewPreviewsContainRepresentativeData() => Ui.RunAsync(async () =>
    {
        var workspace = PreviewData.WorkspaceArchitecture;
        var issues = PreviewData.WorkspaceIssues;
        var overview = PreviewData.WorkspaceOverview;
        var knowledge = PreviewData.Knowledge;
        var task = PreviewData.TaskDialog;
        await Ui.UntilAsync(() => workspace.System.HasContent && issues.Rollup.HasContent && issues.Issues.HasContent
            && overview.Glance.HasContent && knowledge.Memories.HasContent && task.Loaded.IsCompleted);
        workspace.System.Columns.Should().NotBeEmpty();
        overview.Tiles.Should().NotBeEmpty();
        issues.Issues.Groups.Should().NotBeEmpty();
        task.Task.Should().NotBeNull();
        PreviewData.MemoryGroup.Cards.Should().NotBeEmpty();
        PreviewData.Findings.Groups.Should().NotBeEmpty();
        PreviewData.TaskForm.TaskTitle.Should().NotBeNullOrEmpty();
        PreviewData.BlueprintForm.BlueprintTitle.Should().NotBeNullOrEmpty();
        PreviewData.RemoveBlueprint.Name.Should().NotBeNullOrEmpty();
        PreviewData.Toasts.Items.Should().HaveCount(2);
        PreviewData.Dialog.IsOpen.Should().BeTrue();
        PreviewData.ArchApprove.TaskTitle.Should().NotBeNullOrEmpty();
        PreviewData.TaskCard.Title.Should().NotBeNullOrEmpty();
        PreviewData.WorkspaceMembers.Rows.Should().NotBeEmpty();
        var window = Ui.Show(new WorkspaceArchitectureView { DataContext = workspace }, 1280, 800);
        window.GetVisualDescendants().OfType<TextBlock>().Should().Contain(text => text.Text == "Runtime: calls");
        window.Close();
        task.Cancel();
        workspace.Dispose();
        issues.Dispose();
        overview.Dispose();
        knowledge.Dispose();
    });
#endif

    [Fact]
    public void PreviewFixturesAreExcludedFromReleaseBuilds()
    {
        var resources = typeof(PreviewData).Assembly.GetManifestResourceNames();
#if DEBUG
        resources.Should().Contain(name => name.StartsWith("PreviewFixtures/", StringComparison.Ordinal));
#else
        resources.Should().NotContain(name => name.StartsWith("PreviewFixtures/", StringComparison.Ordinal));
#endif
    }

    [Fact]
    public async Task PreviewApiRefusesWritesAndUnknownEndpoints()
    {
        using var handler = new PreviewApiHandler();
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://preview.invalid/"),
        };
        using var body = new StringContent("{}");
        using var write = await client.PostAsync(
            "/api/projects/prj_fixture/tasks", body, TestContext.Current.CancellationToken
        );
        using var missing = await client.GetAsync("/api/not-a-preview-fixture", TestContext.Current.CancellationToken);
        write.StatusCode.Should().Be(System.Net.HttpStatusCode.MethodNotAllowed);
        missing.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }
}
