using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
using System.Text.Json.Nodes;

namespace Harness.Ui.Tests;

/// <summary>
/// The Avalonia Architecture against the fixture API: Structure (built on, summary, findings grouped by kind with
/// their levels, marks and triage), Layers (the graph, selection, the side panel with package marks and users),
/// Complexity (chart, most complex functions by level) and Change coupling (missing partners, hotspots), with each
/// write's request and the UI checks.
/// </summary>
public sealed class ArchitectureViewTests
{
    private const string Root = "/api/projects/prj_fixture";
    private const string Core = "src/Fixture.Core/Fixture.Core.csproj";
    private const string App = "src/Fixture.App/Fixture.App.csproj";

    private const string Structure = """
        {"known":true,"language":"dotnet","input":"solution at the repository root","workspaces":1,
         "architecture":{"language":"dotnet","noun":"project","solutions":[{"path":"Fixture.slnx","projects":[]}],"layers":2,
           "projects":[
             {"name":"Fixture.App","path":"src/Fixture.App/Fixture.App.csproj","layer":1,"kind":"entry","references":["src/Fixture.Core/Fixture.Core.csproj"],"referenced_by":["test/Fixture.Tests/Fixture.Tests.csproj"],"solutions":["Fixture.slnx"]},
             {"name":"Fixture.Core","path":"src/Fixture.Core/Fixture.Core.csproj","layer":0,"kind":"library","package":"Fixture.Core","referenced_by":["src/Fixture.App/Fixture.App.csproj"],
              "packages":[{"name":"Newtonsoft.Json","version":"13.0.1"}],"solutions":["Fixture.slnx"]},
             {"name":"Fixture.Tests","path":"test/Fixture.Tests/Fixture.Tests.csproj","layer":2,"kind":"test","role":"test","references":["src/Fixture.App/Fixture.App.csproj"],"solutions":["Fixture.slnx"]}],
           "smells":[]},
         "package_users":{"Fixture.Core":[{"member_id":"prj_other","member":"other","project":"src/Other/Other.csproj"}]},
         "findings":[
           {"kind":"complexity","level":"problem","title":"Rules.Grade has complexity 60","why":"Many paths.","fix":"Split it.","key":"cx:1","status":"new","evidence":["src/Fixture.Core/Rules.cs:5"],"source":{"provider":"harness","tool":"internal/dotnet"},"basis":"fact"},
           {"kind":"complexity","level":"warn","title":"Rules.Score has complexity 25","why":"Many paths.","fix":"Split it.","key":"cx:2","status":"same"},
           {"kind":"complexity","level":"warn","title":"Program.Main has complexity 22","why":"Many paths.","fix":"Split it.","key":"cx:3","status":"same"},
           {"kind":"boundary","level":"warn","title":"Fixture.Core references Fixture.App","why":"A rule forbids it.","key":"b:1","status":"same",
            "rule":{"line":"Core must not reference App","type":"constraint","memory":"mem_1"}},
           {"kind":"unused","level":"info","title":"Fixture.App is used by nothing here","key":"u:1","status":"same","mark":{"state":"by_design","reason":"an entry point"}},
           {"kind":"cycle","level":"problem","title":"A circle that is gone","key":"c:1","status":"gone","mark":{"state":"task","task_id":"ARCH-0001"}}],
         "snapshot":{"taken_at":"2026-11-13T09:00:00Z","added":1,"changed":0,"gone":1}}
        """;

    private const string Marks = """
        {"packages":{"newtonsoft.json@13.0.1":{"version":"13.0.1","latest":"13.0.3",
          "vulnerabilities":[{"severity":"high","url":"https://github.com/advisories/x","versions":"< 13.0.2","fixed":"13.0.2"}]}},"notes":[]}
        """;

    private const string Coupling = """
        {"hotspots":[
           {"path":"src/Fixture.Core/Rules.cs","commits":8,"partners":[{"path":"src/Fixture.App/Program.cs","together":6,"share":0.75}]},
           {"path":"src/Fixture.App/Program.cs","commits":6},
           {"path":"src/Fixture.Core/Text.cs","commits":2}],
         "commits":10,"window_days":180,"errors":[],
         "changed":["src/Fixture.Core/Rules.cs"],
         "missing":[{"changed":"src/Fixture.Core/Rules.cs","partner":{"path":"src/Fixture.App/Program.cs","together":6,"share":0.75}}]}
        """;

    private const string Complexity = """
        {"languages":[{"language":"csharp","thresholds":{"note":11,"warn":21,"problem":51},"functions":3,"max":60,
           "bins":[{"from":1,"to":5,"count":2},{"from":6,"to":10,"count":0}],
           "top":[{"name":"Rules.Grade","path":"src/Fixture.Core/Rules.cs","line":5,"complexity":60}],
           "files":{"src/Fixture.Core/Rules.cs":60,"src/Fixture.App/Program.cs":3,"src/Fixture.Core/Text.cs":12}}],"bin_width":5}
        """;

    [Fact]
    public Task EvidenceOpensSnippetsAndComparisonsWithPartialFailures() => Ui.RunAsync(async () =>
    {
        var api = Api();
        api.Answers[Root + "/snippet"] = """{"snippet":{"path":"src/A.cs","start":4,"lines":["one","two"]}}""";
        api.Answers[Root + "/snippet?B.cs"] = """{"snippet":null}""";
        var (window, _, model) = await OpenAsync("architecture", api);
        var finding = new FindingRow(new Harness.Ui.Api.Finding
        {
            Kind = "duplicate", Level = "warn", Title = "Copies", Evidence = ["src/A.cs:5", "src/B.cs:8", "src/C.cs:9"],
        }, false);
        finding.CanCompare.Should().BeTrue();
        finding.CanCompareThree.Should().BeTrue();
        _ = model.CompareThreeEvidenceCommand.ExecuteAsync(finding);
        var dialog = await DialogAsync<EvidenceDialog>(model);
        await Ui.UntilAsync(() => dialog.Panes.Count == 3 && dialog.Panes.All(p => p.Text != "Loading…"));
        dialog.Panes.Select(p => p.Location).Should().Equal(finding.Finding.Evidence);
        dialog.Panes[0].Text.Should().Contain("    4  one");
        dialog.Panes[1].Text.Should().StartWith("Source unavailable:");
        dialog.Panes[2].Text.Should().Contain("one");
        api.GetsWithQuery.Count(x => x.StartsWith(Root + "/snippet?", StringComparison.Ordinal)).Should().Be(3);
        dialog.Cancel();
        await Ui.UntilAsync(() => !model.Services.Dialogs.IsOpen);
        _ = model.CompareEvidenceCommand.ExecuteAsync(finding);
        dialog = await DialogAsync<EvidenceDialog>(model);
        await Ui.UntilAsync(() => dialog.Panes.Count == 2 && dialog.Panes.All(p => p.Text != "Loading…"));
        dialog.Cancel();
        await Ui.UntilAsync(() => !model.Services.Dialogs.IsOpen);
        api.Answers[Root + "/snippet"] = """{"snippet":{}}""";
        _ = model.ShowEvidenceCommand.ExecuteAsync("missing.cs:3");
        dialog = await DialogAsync<EvidenceDialog>(model);
        await Ui.UntilAsync(() => dialog.Panes.Count == 1 && dialog.Panes[0].Text != "Loading…");
        dialog.Panes[0].Text.Should().Contain("No source lines available");
        dialog.Cancel();
        window.Close();
    });

    [Theory]
    [InlineData(390, false, 1)]
    [InlineData(390, true, 1)]
    [InlineData(390, false, 2)]
    [InlineData(390, true, 2)]
    [InlineData(390, false, 3)]
    [InlineData(390, true, 3)]
    [InlineData(1280, false, 1)]
    [InlineData(1280, true, 1)]
    [InlineData(1280, false, 2)]
    [InlineData(1280, true, 2)]
    [InlineData(1280, false, 3)]
    [InlineData(1280, true, 3)]
    public Task EvidenceComparisonUsesWideScrollableDialog(int width, bool dark, int count) => Ui.RunAsync(async () =>
    {
        var api = Api();
        var line = new string('x', 500);
        var lines = new JsonArray(Enumerable.Range(0, 80).Select(_ => (JsonNode?)JsonValue.Create(line)).ToArray());
        api.Answers[Root + "/snippet"] = new JsonObject
        {
            ["snippet"] = new JsonObject { ["path"] = "src/A.cs", ["start"] = 1, ["lines"] = lines },
        }.ToJsonString();
        var (window, _, model) = await OpenAsync("architecture", api, width);
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var finding = new FindingRow(new Harness.Ui.Api.Finding
        {
            Kind = "duplicate", Level = "warn", Title = "Copies", Evidence = ["src/A.cs:1", "src/B.cs:1", "src/C.cs:1"],
        }, false);
        if (count == 1) { _ = model.ShowEvidenceCommand.ExecuteAsync("src/A.cs:1"); }
        else if (count == 2) { _ = model.CompareEvidenceCommand.ExecuteAsync(finding); }
        else { _ = model.CompareThreeEvidenceCommand.ExecuteAsync(finding); }
        var dialog = await DialogAsync<EvidenceDialog>(model);
        await Ui.UntilAsync(() => dialog.Panes.Count == count && dialog.Panes.All(p => p.Text != "Loading…"));
        Ui.Settle();
        var layer = window.GetVisualDescendants().OfType<DialogLayer>().Single();
        var frame = layer.FindControl<Border>("Dialog")!;
        frame.Bounds.Width.Should().BeApproximately(width - 32, 2);
        var panes = layer.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("inset") && b.DataContext is EvidencePane).ToArray();
        panes.Should().HaveCount(count);
        var paneList = layer.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "EvidencePanes");
        foreach (var pane in panes)
        {
            pane.Bounds.Width.Should().BeApproximately(paneList.Bounds.Width / count - 12, 2);
            var source = pane.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Classes.Contains("evidence-source"));
            source.Extent.Width.Should().BeGreaterThan(source.Viewport.Width);
            source.GetVisualDescendants().OfType<SelectableTextBlock>().Single().Text.Should().Contain(line);
        }
        var close = layer.FindControl<Button>("Accept")!;
        close.IsEffectivelyVisible.Should().BeTrue();
        close.IsFocused.Should().BeTrue();
        close.Classes.Should().Contain("evidence-close");
        close.CornerRadius.TopLeft.Should().BeGreaterThan(0);
        close.TryFindResource("Surface", close.ActualThemeVariant, out var surface).Should().BeTrue();
        close.Background.Should().Be(surface);
        var dialogScroll = layer.FindControl<ScrollViewer>("DialogScroll")!;
        dialogScroll.Extent.Height.Should().BeGreaterThan(dialogScroll.Viewport.Height);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Ui.UntilAsync(() => !model.Services.Dialogs.IsOpen);
        window.Close();
    });

    [Theory]
    [InlineData(390, false)]
    [InlineData(390, true)]
    [InlineData(1280, false)]
    [InlineData(1280, true)]
    public Task ComplexityLocationOpensSourceFromTheView(int width, bool dark) => Ui.RunAsync(async () =>
    {
        var api = Api();
        api.Answers[Root + "/snippet"] = """{"snippet":{"path":"src/Fixture.Core/Rules.cs","start":5,"lines":["return 1;"]}}""";
        var (window, _, model) = await OpenAsync("architecture/complexity", api, width);
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "src/Fixture.Core/Rules.cs:5");
        button.Command.Should().NotBeNull();
        button.Command.Execute(button.CommandParameter);
        var dialog = await DialogAsync<EvidenceDialog>(model);
        await Ui.UntilAsync(() => dialog.Panes.Count == 1 && dialog.Panes[0].Text.Contains("return 1", StringComparison.Ordinal));
        window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Should().Contain(x => x.Text != null && x.Text.Contains("return 1", StringComparison.Ordinal));
        dialog.Cancel();
        window.Close();
    });

    [Fact]
    public Task FindingEvidenceOpensFromTheView() => Ui.RunAsync(async () =>
    {
        var api = Api();
        api.Answers[Root + "/snippet"] = """{"snippet":{"path":"src/Fixture.Core/Rules.cs","start":5,"lines":["rule"]}}""";
        var (window, _, model) = await OpenAsync("architecture", api);
        window.GetVisualDescendants().OfType<Expander>()
            .First(e => AutomationProperties.GetName(e) == "3 × functions with many paths through them").IsExpanded = true;
        Ui.Settle();
        var evidence = window.GetVisualDescendants().OfType<Expander>()
            .First(e => e.Header is string label && label == "Evidence (1)");
        evidence.IsExpanded = true;
        Ui.Settle();
        var button = evidence.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "src/Fixture.Core/Rules.cs:5");
        button.Command.Should().NotBeNull();
        button.Command.Execute(button.CommandParameter);
        var dialog = await DialogAsync<EvidenceDialog>(model);
        await Ui.UntilAsync(() => dialog.Panes.Count == 1 && dialog.Panes[0].Text.Contains("rule", StringComparison.Ordinal));
        dialog.Cancel();
        window.Close();
    });

    [Fact]
    public Task StructureShowsWhatItIsBuiltOnAndItsFindingsByKind() => Ui.RunAsync(async () =>
    {
        var api = Api();
        var (window, _, model) = await OpenAsync("architecture", api);
        var s = Architecture(model).Structure;
        var view = window.GetVisualDescendants().OfType<ArchitectureView>().Single();
        var card = view.FindControl<Border>("BuiltOn")!;
        card.GetVisualDescendants().OfType<TextBlock>().Should().Contain(text => text.Name == "Summary");
        card.GetVisualDescendants().OfType<Border>().Should().Contain(mark => mark.Classes.Contains("language-mark"));
        view.GetVisualDescendants().OfType<TextBlock>().Should().Contain(text => text.Text == "Smells");
        view.GetVisualDescendants().OfType<Border>().Should().Contain(border => border.Classes.Contains("smell"));

        Architecture(model).ShowsStructure.Should().BeTrue();
        s.Language.Should().Be("C#");
        s.Parts.Select(p => p.Name).Should().Equal(".NET");
        s.Summary.Should().Be("2 projects in 2 layers · 1 test projects");
        s.Detail.Should().Be("solution: Fixture.slnx · graphify loads: solution at the repository root");
        s.Board.SnapshotLine.Should().StartWith("Since the last change (").And.EndWith("): 1 new, 0 changed, 1 gone");
        s.Board.Groups.Select(g => (g.Kind, g.IsGroup)).Should().Equal(("complexity", true), ("boundary", false));
        var cx = s.Board.Groups[0];
        cx.Title.Should().Be("3 × functions with many paths through them");
        cx.FreshTag.Should().Be("1 new or changed");
        cx.HasSameFix.Should().BeTrue();
        cx.Summary.Should().Be("hard to understand and test: bugs hide in the paths · worst: Rules.Grade, Rules.Score, Program.Main");
        cx.Chips.Select(c => c.Label).Should().Equal("1 problems", "2 warnings");
        cx.Shown.Should().HaveCount(3);
        cx.Shown[0].HasFix.Should().BeFalse("the group says what could help once");
        cx.Shown[0].FoundBy.Should().Be("Found by harness, reading the files (internal/dotnet)");

        cx.Chips[1].On = false;
        cx.Shown.Select(r => r.Finding.Key).Should().Equal("cx:1");

        var boundary = s.Board.Groups[1].Shown.Single();
        boundary.RuleLine.Should().Be("Rule: Core must not reference App — constraint (mem_1)");
        (boundary.CanMark, boundary.CanTask, boundary.CanChangeKnowledge, boundary.CanUnmark).Should().Be((true, true, true, false));
        boundary.TaskLabel.Should().Be("Fix the code (task)…");

        s.Board.MarkedCount.Should().Be(1);
        s.Board.ShowMarked = true;
        s.Board.Groups.Select(g => g.Kind).Should().Equal("complexity", "boundary", "unused");
        var marked = s.Board.Groups[2].Shown.Single();
        (marked.MarkTag, marked.Quiet, marked.CanMark, marked.UnmarkLabel).Should().Be(("by design", true, false, "Take the mark back"));
        s.Board.Gone.Select(g => g.Key).Should().Equal("c:1");
    });

    [Fact]
    public Task FindingsAreMarkedByDesignMadeTasksAndUnmarked() => Ui.RunAsync(async () =>
    {
        var api = Api();
        var (_, _, model) = await OpenAsync("architecture", api);
        var s = Architecture(model).Structure;
        var boundary = s.Board.Groups[1].Shown.Single();

        _ = s.Board.MarkCommand.ExecuteAsync(boundary);
        var mark = await DialogAsync<FindingMarkDialog>(model);
        await mark.AcceptCommand.ExecuteAsync(null);
        model.Services.Dialogs.Current.Should().BeSameAs(mark, "the reason is required");
        mark.Reason = "kept on purpose";
        await mark.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Should().Be((Root + "/findings/mark", """{"key":"b:1","reason":"kept on purpose"}""", api.Posts[^1].Origin, "1"));

        _ = s.Board.TaskCommand.ExecuteAsync(boundary);
        var confirm = await DialogAsync<ConfirmDialog>(model);
        await confirm.AcceptCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => api.Posts.Count == 2);
        api.Posts[^1].Should().Be((Root + "/findings/task", """{"key":"b:1","project":""}""", api.Posts[^1].Origin, "1"));

        s.Board.ShowMarked = true;
        await s.Board.UnmarkCommand.ExecuteAsync(s.Board.Groups[2].Shown.Single());
        api.Posts[^1].Should().Be((Root + "/findings/unmark", """{"key":"u:1"}""", api.Posts[^1].Origin, "1"));
        await Ui.UntilAsync(() => model.Services.Toasts.Items.Any(t => t.Text == "Mark taken back"));
    });

    [Fact]
    public Task WithoutAProjectStructureItSaysSo() => Ui.RunAsync(async () =>
    {
        var api = Api();
        api.Answers[Root + "/structure"] = """{"known":false}""";
        var (_, _, model) = await OpenAsync("architecture", api);

        Architecture(model).Structure.Known.Should().BeFalse();
        Architecture(model).Structure.Board.Groups.Should().BeEmpty();
    });

    [Fact]
    public Task LayersDrawTheGraphAndTheSelectedUnitWithItsPackagesAndUsers() => Ui.RunAsync(async () =>
    {
        var api = Api();
        var (window, _, model) = await OpenAsync("architecture/layers", api);
        var s = Architecture(model).Structure;

        Architecture(model).ShowsLayers.Should().BeTrue();
        s.Rows.Select(r => (r.Label, string.Join(",", r.Nodes.Select(n => n.Label)))).Should().Equal(("tests", "Tests"), ("layer 1", "App"), ("layer 0", "Core"));
        s.Rows.Select(row => row.Highlight).Should().Equal(true, false, true);
        s.Rows[2].Nodes[0].Badge.Should().Be("↗ 1");
        s.Edges.Should().Contain(new GraphEdge(App, Core));
        s.Big.Should().BeFalse();
        await Ui.UntilAsync(() => api.GetsWithQuery.Any(g => g.StartsWith(Root + "/packages?packages=", StringComparison.Ordinal)));

        var graph = window.GetVisualDescendants().OfType<LayerGraph>().Single();
        graph.NodeFor(Core)!.Command.Should().BeNull();
        graph.Selected = Core;
        s.Selected.Should().Be(Core);
        s.Unit!.Name.Should().Be("Fixture.Core");
        s.UnitLine.Should().Be("library · layer 0");
        s.Publishes.Should().Be("publishes Fixture.Core");
        s.ReferencedBy.Select(r => r.Name).Should().Equal("App");
        s.Users.Should().Equal(new UserRow("other", "Other"));
        await Ui.UntilAsync(() => s.Packages.Single().HasVulnerable);
        var package = s.Packages.Single();
        package.Newer.Should().Be("Newer stable version 13.0.3 available (you use 13.0.1)");
        package.Vulnerable.Should().Be("Known high vulnerability in 13.0.1: https://github.com/advisories/x (affects < 13.0.2); fixed in 13.0.2");
        package.Severe.Should().BeTrue();
        Ui.Settle();
        graph.NodeFor(Core)!.Classes.Should().Contain("sel");
        graph.NodeFor(App)!.Classes.Should().Contain("rel-in");
        graph.NodeFor("test/Fixture.Tests/Fixture.Tests.csproj")!.Classes.Should().Contain("dim");

        s.PickCommand.Execute(s.ReferencedBy[0]);
        s.Unit.Name.Should().Be("Fixture.App");
        graph.Selected.Should().Be(App);
    });

    [Theory]
    [InlineData(390)]
    [InlineData(1280)]
    public Task LayersUseAlternatingBandsAndRoleOutlinesInBothThemes(int width) => Ui.RunAsync(() =>
    {
        var roles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model"] = "CFact", ["base"] = "CFact", ["data"] = "CConstraint", ["logic"] = "CDecision",
            ["feature"] = "CDecision", ["api"] = "CConvention", ["page"] = "CConvention", ["test"] = "CWarning",
            ["hooks"] = "COpenQuestion", ["ui"] = "CGoal",
        };
        var graph = new LayerGraph
        {
            Rows = [new GraphRow("first", [.. roles.Keys.Select(role => new GraphNode(role, role, Meta: role == "model" ? "type" : null, Role: role))], true),
                    new GraphRow("second", [new GraphNode("other", "other")]),
                    new GraphRow("third", [new GraphNode("last", "last")], true)],
        };
        var window = Ui.Show(graph, width, 800);
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = theme;
            graph.Selected = null;
            Ui.Settle();
            var bands = graph.Children.OfType<Border>().Where(band => band.Classes.Contains("layer-band")).ToArray();
            bands.Should().HaveCount(2);
            graph.Children.OfType<TextBlock>().First(label => label.Text == "first").Margin.Left.Should().BeGreaterThan(0);
            var first = graph.NodeFor("model")!;
            var second = graph.NodeFor("base")!;
            first.Bounds.Height.Should().Be(second.Bounds.Height);
            var meta = ((StackPanel)((Border)first.Content!).Child!).Children.OfType<StackPanel>().Single();
            meta.Children.OfType<TextBlock>().Single().Classes.Should().Contain("muted");
            var row = roles.Keys.Select(role => graph.NodeFor(role)!).Where(node => Math.Abs(node.Bounds.Y - first.Bounds.Y) < 0.1).ToArray();
            var left = row.Min(node => node.Bounds.Left);
            var right = row.Max(node => node.Bounds.Right);
            (left - LayerGraph.LabelWidth).Should().BeApproximately(graph.Bounds.Width - right, 1);
            graph.TryFindResource("Border", theme, out var neutral).Should().BeTrue();
            first.BorderBrush.Should().Be(neutral);
            graph.TryFindResource("Surface2", theme, out var background).Should().BeTrue();
            bands.Should().OnlyContain(band => band.Background == background);
            foreach (var (role, token) in roles)
            {
                graph.TryFindResource(token, theme, out var brush).Should().BeTrue();
                ((Border)graph.NodeFor(role)!.Content!).BorderBrush.Should().Be(brush);
            }
            graph.Selected = "model";
            Ui.Settle();
            graph.TryFindResource("Accent", theme, out var selected).Should().BeTrue();
            graph.NodeFor("model")!.BorderBrush.Should().Be(selected, "selection overrides the role outline");
        }
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(390)]
    [InlineData(1280)]
    public Task LargeLayerGraphsSwitchToAnAccessibleDependencyMatrix(int width) => Ui.RunAsync(async () =>
    {
        var data = JsonNode.Parse(Structure)!;
        var projects = data["architecture"]!["projects"]!.AsArray();
        projects.Clear();
        for (var i = 0; i < 25; i++)
        {
            var project = new JsonObject
            {
                ["name"] = $"Project{i:00}", ["path"] = $"src/Project{i:00}.csproj", ["layer"] = i is 0 ? 1 : 0,
                ["kind"] = "library",
            };
            if (i is 0)
            {
                project["references"] = new JsonArray("src/Project01.csproj");
                project["build_order"] = new JsonArray("src/Project02.csproj");
            }
            if (i is 1)
            {
                project["references"] = new JsonArray("src/Project00.csproj");
            }
            projects.Add(project);
        }
        var api = Api();
        api.Answers[Root + "/structure"] = data.ToJsonString();
        var (window, _, model) = await OpenAsync("architecture/layers", api, width, 1000);
        var structure = Architecture(model).Structure;
        structure.Big.Should().BeTrue();
        var view = window.GetVisualDescendants().OfType<ArchitectureView>().Single();
        var toggle = view.FindControl<Button>("MatrixSwitch")!;
        var map = view.FindControl<Button>("MapSwitch")!;
        toggle.IsVisible.Should().BeTrue();
        var graph = view.FindControl<LayerGraph>("Graph")!;
        var matrix = view.FindControl<LayerMatrix>("Matrix")!;
        toggle.Command!.Execute(null);
        Ui.Settle();
        matrix.IsEffectivelyVisible.Should().BeTrue();
        graph.IsEffectivelyVisible.Should().BeFalse();
        var marks = matrix.Children.OfType<Button>().Where(button => button.Tag is GraphEdge).ToArray();
        var guides = matrix.Guides();
        guides.Should().HaveCount(3);
        guides.Should().ContainSingle(line => line.Dashed && line.Start.X < line.End.X && line.Start.Y < line.End.Y);
        guides.Should().ContainSingle(line => !line.Dashed && Math.Abs(line.Start.X - line.End.X) < 0.1);
        guides.Should().ContainSingle(line => !line.Dashed && Math.Abs(line.Start.Y - line.End.Y) < 0.1);
        marks.Should().HaveCount(3);
        marks.Select(AutomationProperties.GetName).Should().Contain(name => name.Contains("references", StringComparison.Ordinal));
        marks.Select(AutomationProperties.GetName).Should().Contain(name => name.Contains("builds first", StringComparison.Ordinal));
        marks.Should().ContainSingle(button => button.Classes.Contains("back"));
        ((Border)marks.Single(button => ((GraphEdge)button.Tag!).Dashed).Content!).Classes.Should().Contain("build");
        matrix.Children.OfType<TextBlock>().Where(text => text.Classes.Contains("matrix-group")).Select(text => text.Text).Should().Equal("L1", "L0");
        var row = matrix.Children.OfType<Button>().First(button => button.Classes.Contains("matrix-row") && (string?)button.Tag == "src/Project00.csproj");
        row.Focus().Should().BeTrue();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        structure.Selected.Should().Be("src/Project00.csproj");
        structure.Unit!.Name.Should().Be("Project00");
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = theme;
            UiChecks.Check(window).Should().BeEmpty($"the matrix should fit and remain readable in {theme}");
        }
        map.Command!.Execute(null);
        Ui.Settle();
        graph.Selected.Should().Be("src/Project00.csproj");
        graph.IsEffectivelyVisible.Should().BeTrue();
        if (width == 390)
        {
            toggle.Command.Execute(null);
            Ui.Settle();
            matrix.GetVisualAncestors().OfType<ScrollViewer>().Should().Contain(viewer => viewer.Extent.Width > viewer.Viewport.Width);
        }
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    });

    [Fact]
    public Task PackageIndicatorsExposeDetailsWithoutInlineStatusText() => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await OpenAsync("architecture/layers", Api());
        var structure = Architecture(model).Structure;
        structure.Selected = Core;
        await Ui.UntilAsync(() => structure.Packages.Any(package => package.HasVulnerable));
        structure.Packages.Clear();
        structure.Packages.Add(new PackageRow("Current", "1", null, null, false));
        structure.Packages.Add(new PackageRow("Older", "1", "Newer version 2", null, false));
        structure.Packages.Add(new PackageRow("Unsafe", "1", null, "Known high vulnerability", true));
        structure.Packages.Add(new PackageRow("Both", "1", "Newer version 2", "Known low vulnerability", false));
        Ui.Settle();
        var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(control => control.Name is "Packages");
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = theme;
            Ui.Settle();
            var buttons = list.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("package-up") || button.Classes.Contains("package-vuln")).ToArray();
            foreach (var row in structure.Packages)
            {
                var rowButtons = buttons.Where(button => ReferenceEquals(button.DataContext, row)).ToArray();
                rowButtons.Single(button => button.Classes.Contains("package-up")).IsVisible.Should().Be(row.HasNewer);
                rowButtons.Single(button => button.Classes.Contains("package-vuln")).IsVisible.Should().Be(row.HasVulnerable);
                foreach (var button in rowButtons.Where(button => button.IsVisible))
                {
                    var detail = button.Classes.Contains("package-up") ? row.NewerLabel : row.VulnerableLabel;
                    ToolTip.GetTip(button).Should().Be(detail);
                    AutomationProperties.GetName(button).Should().Be(detail);
                    button.Focusable.Should().BeTrue();
                    var token = (button.Classes.Contains("package-up"), row.Severe) switch
                    {
                        (true, _) => "Accent",
                        (false, true) => "Critical",
                        _ => "Warning",
                    };
                    button.TryFindResource(token, theme, out var color).Should().BeTrue();
                    button.Foreground.Should().Be(color);
                    var flyout = button.Flyout.Should().BeOfType<Flyout>().Subject;
                    flyout.ShowAt(button);
                    Ui.Settle();
                    ((TextBlock)flyout.Content!).Text.Should().Be(detail);
                    flyout.Hide();
                }
            }
            list.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible)
                .Should().NotContain(text => text.Text == "Newer version 2" || text.Text == "Known high vulnerability" || text.Text == "Known low vulnerability");
        }
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    });

    [Fact]
    public void ComplexityChartExtendsSparseBinsPastTheProblemThreshold()
    {
        ComplexityBar[] sparse = [new(1, 5, 3)];
        var shown = ComplexityChart.DisplayBins(sparse, 5, 51);
        shown.Should().HaveCount(13);
        shown[^1].To.Should().BeGreaterThanOrEqualTo(62);
        shown.Sum(bin => bin.Count).Should().Be(3);
        shown.Skip(1).Should().OnlyContain(bin => bin.Count == 0);
        var high = Enumerable.Range(0, 15).Select(i => new ComplexityBar(i * 5 + 1, (i + 1) * 5, i)).ToArray();
        ComplexityChart.DisplayBins(high, 5, 51).Should().Equal(high);
        ComplexityChart.DisplayBins([], 10, 20)[^1].To.Should().BeGreaterThanOrEqualTo(24);
    }

    [Fact]
    public Task ComplexityShowsEachLanguageAndItsMostComplexFunctions() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await OpenAsync("architecture/complexity", Api(complexity: false));
        var languages = Architecture(model).Complexity.Languages;

        Architecture(model).ShowsComplexity.Should().BeTrue();
        var cs = languages.Single();
        (cs.Title, cs.Line).Should().Be(("C#", "2 functions, the most complex at 15"));
        cs.Bars.Select(b => b.Count).Should().Equal(1, 0, 1);
        cs.Top.Select(t => (t.Function.Name, t.Level)).Should().Equal(("Rules.Grade", "info"), ("Program.Main", "low"));
        cs.Chips.Select(c => c.Label).Should().Equal("1 notes", "1 below the thresholds");
        cs.Chips[1].On = false;
        cs.Top.Select(t => t.Function.Name).Should().Equal("Rules.Grade");
    });

    [Fact]
    public Task ChangeCouplingLoadsOnlyOnceShownWithMissingPartnersAndHotspots() => Ui.RunAsync(async () =>
    {
        var api = Api();
        var (_, _, model) = await OpenAsync("architecture", api);
        api.UiGets.Should().NotContain(Root + "/coupling");

        model.Navigate("#/p/prj_fixture/architecture/coupling");
        await Ui.UntilAsync(() => Architecture(model).ShowsCoupling);
        var c = Architecture(model).Coupling;
        await Ui.UntilAsync(() => c.HasContent);

        api.UiGets.Should().Contain(Root + "/coupling");
        c.MissingHead.Should().Be("Your uncommitted change leaves out 1 file that usually change with it (a reminder, not a rule)");
        c.Missing.Should().Equal("src/Fixture.App/Program.cs usually changes with src/Fixture.Core/Rules.cs (6 commits together, 75% of its commits)");
        c.Rows.Select(r => (r.Name, r.ComplexityText, r.Level)).Should().Equal(("Rules.cs", "60", "problem"), ("Program.cs", "3", "low"), ("Text.cs", "12", "info"));
        c.Rows[0].Partners.Select(partner => partner.Label).Should().Equal("Program.cs 75%");
        c.Rows[0].Partners[0].Detail.Should().Contain("src/Fixture.App/Program.cs: 6 commits together");
        c.HasChart.Should().BeTrue();
        c.Points.Where(p => p.Named).Select(p => p.Name).Should().Contain("Rules.cs");
    });

    [Theory]
    [InlineData(390)]
    [InlineData(1280)]
    public Task ChangeCouplingUsesSeparateAlignedColumns(int width) => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await OpenAsync("architecture/coupling", Api(), width, 800);
        await Ui.UntilAsync(() => Architecture(model).Coupling.HasContent);
        Ui.Settle();
        var view = window.GetVisualDescendants().OfType<ArchitectureView>().Single();
        var header = view.FindControl<Grid>("CouplingHeader")!;
        header.Children.OfType<TextBlock>().Select(text => text.Text)
            .Should().Equal("file", "commits", "complexity", "changes together with");
        header.Children.OfType<TextBlock>().Where(text => Grid.GetColumn(text) is 1 or 2)
            .Should().OnlyContain(text => text.TextAlignment == Avalonia.Media.TextAlignment.Center);
        var rows = view.FindControl<ItemsControl>("CouplingRows")!;
        var row = rows.GetVisualDescendants().OfType<Grid>().First(grid => grid.ColumnDefinitions.Count == 4);
        row.Children.OfType<StackPanel>().Single(panel => Grid.GetColumn(panel) == 1).HorizontalAlignment
            .Should().Be(Avalonia.Layout.HorizontalAlignment.Center);
        row.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 2).TextAlignment
            .Should().Be(Avalonia.Media.TextAlignment.Center);
        var partnerColumn = rows.GetVisualDescendants().OfType<ItemsControl>()
            .First(control => control.Classes.Contains("coupling-partners") && control.IsVisible);
        Grid.GetColumn(partnerColumn).Should().Be(3);
        var badge = partnerColumn.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("coupling-badge"));
        ToolTip.GetTip(badge).Should().Be("src/Fixture.App/Program.cs: 6 commits together, 75% of its commits");
        if (width == 390)
        {
            var scroll = rows.GetVisualAncestors().OfType<ScrollViewer>()
                .First(viewer => viewer.Extent.Width > viewer.Viewport.Width);
            scroll.HorizontalScrollBarVisibility.Should().Be(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        }
    });

    [Theory]
    [InlineData(390)]
    [InlineData(1280)]
    public Task ChangeCouplingWrapsLongFilesAndPartnersWithoutLosingAlignment(int width) => Ui.RunAsync(async () =>
    {
        var data = JsonNode.Parse(Coupling)!;
        var first = data["hotspots"]![0]!;
        first["path"] = "src/very-long-directory-name-that-should-wrap/another-long-directory/ExtremelyLongSourceFileNameThatMustWrapWithoutBeingCutOff.cs";
        first["partners"] = new JsonArray(
            new JsonObject { ["path"] = "src/FirstPartnerWithAnEspeciallyLongNameThatMustWrap.cs", ["together"] = 5, ["share"] = 0.7 },
            new JsonObject { ["path"] = "src/SecondPartnerWithAnEspeciallyLongNameThatMustWrap.cs", ["together"] = 4, ["share"] = 0.6 },
            new JsonObject { ["path"] = "src/ThirdPartner.cs", ["together"] = 3, ["share"] = 0.4 });
        var api = Api();
        api.Answers[Root + "/coupling"] = data.ToJsonString();
        var (window, _, model) = await OpenAsync("architecture/coupling", api, width, 800);
        await Ui.UntilAsync(() => Architecture(model).Coupling.HasContent);
        Ui.Settle();
        var view = window.GetVisualDescendants().OfType<ArchitectureView>().Single();
        var header = view.FindControl<Grid>("CouplingHeader")!;
        var rows = view.FindControl<ItemsControl>("CouplingRows")!;
        var grids = rows.GetVisualDescendants().OfType<Grid>().Where(grid => grid.ColumnDefinitions.Count == 4).ToArray();
        grids.Should().HaveCount(3);
        grids[0].Bounds.Height.Should().BeGreaterThan(grids[1].Bounds.Height);
        foreach (var grid in grids)
        {
            for (var column = 0; column < 4; column++)
            {
                grid.ColumnDefinitions[column].ActualWidth.Should().BeApproximately(header.ColumnDefinitions[column].ActualWidth, 1);
            }
        }
        HotspotChart.WarningBandBrush.Should().Be("Warning");
        HotspotChart.WarningBandOpacity.Should().BeApproximately(0.08, 0.001);
        var file = grids[0].GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("coupling-file-name"));
        file.TextWrapping.Should().Be(Avalonia.Media.TextWrapping.Wrap);
        file.Text.Should().EndWith("WithoutBeingCutOff.cs");
        var badges = grids[0].GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("coupling-badge")).ToArray();
        badges.Should().HaveCount(3);
        var partnerColumn = grids[0].Children.OfType<ItemsControl>().Single(control => control.Classes.Contains("coupling-partners"));
        var positions = badges.Select(badge => badge.TranslatePoint(default, partnerColumn)!.Value).ToArray();
        positions.Select(point => point.Y).Distinct().Count().Should().BeGreaterThan(1, $"column {partnerColumn.Bounds}, badges {string.Join(", ", badges.Select((b, i) => $"{positions[i]} {b.Bounds}"))}");
        for (var i = 0; i < badges.Length; i++)
        {
            var badge = badges[i];
            var partner = Architecture(model).Coupling.Rows[0].Partners[i];
            var name = badge.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("coupling-partner-name"));
            var percent = badge.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("coupling-partner-percent"));
            name.Text.Should().Be(partner.Name);
            name.TextTrimming.Should().Be(Avalonia.Media.TextTrimming.CharacterEllipsis);
            name.TextWrapping.Should().Be(Avalonia.Media.TextWrapping.NoWrap);
            percent.Text.Should().Be(partner.Percentage);
            percent.Bounds.Width.Should().BeGreaterThan(0);
            name.Bounds.Height.Should().BeApproximately(percent.Bounds.Height, 1);
            (positions[i].X + badge.Bounds.Width).Should().BeLessThanOrEqualTo(partnerColumn.Bounds.Width + 1);
            ToolTip.GetTip(badge).Should().Be(partner.Detail);
            Avalonia.Automation.AutomationProperties.GetName(badge).Should().Be(partner.Detail);
        }
        grids[0].GetVisualDescendants().OfType<Meter>().Single().BrushKey.Should().Be("Accent");
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Application.Current!.RequestedThemeVariant = theme;
            UiChecks.Check(window).Should().BeEmpty();
        }
        window.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    });

    [Theory]
    [InlineData("architecture")]
    [InlineData("architecture/layers")]
    [InlineData("architecture/complexity")]
    [InlineData("architecture/coupling")]
    public Task ArchitecturePassesTheUiChecks(string view) =>
        Ui.RunAsync(() =>
            UiChecks.AssertAsync(async (width, height) =>
            {
                var (window, _, model) = await OpenAsync(view, Api(), width, height);
                if (view.EndsWith("layers", StringComparison.Ordinal))
                {
                    Architecture(model).Structure.Selected = Core;
                    await Ui.UntilAsync(() => Architecture(model).Structure.Packages.Any(package => package.HasVulnerable));
                }
                if (view.EndsWith("structure", StringComparison.Ordinal) || view == "architecture")
                {
                    window.GetVisualDescendants().OfType<Expander>().First().IsExpanded = true;
                    Ui.Settle();
                }
                return window;
            })
        );

    private static FixtureApi Api(bool complexity = true)
    {
        var api = new FixtureApi();
        api.Answers[Root + "/structure"] = Structure;
        api.Answers[Root + "/packages"] = Marks;
        api.Answers[Root + "/coupling"] = Coupling;
        if (complexity)
        {
            api.Answers[Root + "/complexity"] = Complexity;
        }
        return api;
    }

    private static async Task<(Window Window, FixtureApi Api, MainViewModel Model)> OpenAsync(string view, FixtureApi api, int width = 1280, int height = 800)
    {
        var opened = await Ui.OpenMainAsync(width, height, api: api, address: $"#/p/prj_fixture/{view}");
        await Ui.UntilAsync(() =>
            opened.Model.Page is ProjectPage { Body: ArchitectureViewModel { Structure.HasContent: true, Complexity.HasContent: true } a }
            && (!a.ShowsCoupling || a.Coupling.HasContent)
        );
        Ui.Settle();
        return opened;
    }

    private static async Task<T> DialogAsync<T>(MainViewModel model)
        where T : DialogViewModel
    {
        await Ui.UntilAsync(() => model.Services.Dialogs.Current is T);
        Ui.Settle();
        return (T)model.Services.Dialogs.Current!;
    }

    private static ArchitectureViewModel Architecture(MainViewModel model) => (ArchitectureViewModel)((ProjectPage)model.Page!).Body!;
}
