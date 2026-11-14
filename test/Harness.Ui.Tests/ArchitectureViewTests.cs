using Avalonia.Controls;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Controls;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;
using Xunit;

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
    public Task StructureShowsWhatItIsBuiltOnAndItsFindingsByKind() => Ui.RunAsync(async () =>
    {
        var api = Api();
        var (_, _, model) = await OpenAsync("architecture", api);
        var s = Architecture(model).Structure;

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
        c.Rows[0].Partners.Should().Equal("Program.cs 75%");
        c.HasChart.Should().BeTrue();
        c.Points.Where(p => p.Named).Select(p => p.Name).Should().Contain("Rules.cs");
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
                    Ui.Settle();
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
