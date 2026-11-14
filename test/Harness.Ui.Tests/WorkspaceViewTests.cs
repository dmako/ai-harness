using Avalonia.Controls;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Controls;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The Avalonia workspace pages against the fixture API: Overview (member cards, at a glance), Architecture (Runtime
/// calls with its smells and their triage, Build packages with its buses, Libraries), Issues (the findings rollup,
/// design issues, shared code, version drift and the versions table) and Members (remove, add, delete), a new workspace
/// from Home, each write's request and the UI checks.
/// </summary>
public sealed class WorkspaceViewTests
{
    private const string Root = "/api/workspaces/main";

    private const string Projects = """
        {"projects":[
          {"id":"prj_fixture","title":"Fixture project","root":"C:\\repos\\fixture","live":{"state":"ok","pending_proposals":1,"pending_arch_proposals":0,"pending_task_proposals":2,"pending_blueprint_proposals":0,"removed_from_project":0,"checks_failed":0,"checks_warned":0,"context_answers_30d":10,"context_hit_rate_30d":0.6},"workspaces":["main"],"last_mcp":"2026-11-13T09:00:00Z"},
          {"id":"prj_api","name":"api","title":"Api","root":"C:\\repos\\api","live":{"state":"ok","pending_proposals":0,"pending_arch_proposals":0,"pending_task_proposals":0,"pending_blueprint_proposals":0,"removed_from_project":0,"checks_failed":0,"checks_warned":0},"workspaces":["main"]},
          {"id":"prj_worker","name":"worker","title":"Worker","root":"C:\\repos\\worker","live":{"state":"ok","pending_proposals":0,"pending_arch_proposals":0,"pending_task_proposals":0,"pending_blueprint_proposals":0,"removed_from_project":0,"checks_failed":0,"checks_warned":0},"workspaces":["main"]},
          {"id":"prj_core","name":"core","title":"Core","root":"C:\\repos\\core","live":{"state":"ok","pending_proposals":0,"pending_arch_proposals":0,"pending_task_proposals":0,"pending_blueprint_proposals":0,"removed_from_project":0,"checks_failed":0,"checks_warned":0},"workspaces":["main"]},
          {"id":"prj_other","title":"Other","root":"C:\\elsewhere\\other","live":{"state":"ok","pending_proposals":0,"pending_arch_proposals":0,"pending_task_proposals":0,"pending_blueprint_proposals":0,"removed_from_project":0,"checks_failed":0,"checks_warned":0}}]}
        """;

    private const string Workspaces = """
        {"workspaces":[{"id":"ws_main","name":"main","title":"Main workspace","created":"2026-11-01T10:00:00Z","members":[
          {"project_id":"prj_fixture","project":"fixture","root":"C:/repos/fixture","title":"Fixture project"},
          {"project_id":"prj_api","project":"api","root":"C:/repos/api","title":"Api"},
          {"project_id":"prj_worker","project":"worker","root":"C:/repos/worker","title":"Worker"},
          {"project_id":"prj_core","project":"core","root":"C:/repos/core","title":"Core"}]}]}
        """;

    private const string System = """
        {"nodes":[
           {"id":"prj_api","name":"Api","kind":"service","member_id":"prj_api","layer":1},
           {"id":"prj_worker","name":"Worker","kind":"service","member_id":"prj_worker","layer":0},
           {"id":"prj_core","name":"Core","kind":"library","member_id":"prj_core","layer":0},
           {"id":"db:orders","name":"Orders","kind":"database","detail":"per tenant","layer":0},
           {"id":"ext:payments","name":"Payments","kind":"external","detail":"worker's appsettings.json","layer":0}],
         "links":[
           {"from":"prj_api","to":"prj_worker","style":"grpc","what":["IOrdersGrpcService"],"contracts":["IOrdersGrpcService"],"uses":2,"evidence":["api: Client.cs:4 → worker: Orders.cs:9 (IOrdersGrpcService)"]},
           {"from":"prj_worker","to":"ext:payments","style":"http","what":["Payments:Url"],"uses":1,"evidence":["worker: appsettings.json:3"]},
           {"from":"prj_api","to":"db:orders","style":"database","what":["OrdersContext"],"uses":1,"evidence":[]}],
         "contracts":[
           {"name":"IOrdersGrpcService","owner":"prj_worker","declared":"orders.proto","servers":["prj_worker"],"clients":["prj_api"]},
           {"name":"IAdminService","owner":"prj_worker","declared":"admin.proto","servers":["prj_worker"],"clients":[]}],
         "smells":[{"kind":"sync-cycle","level":"warn","title":"Api and Worker call each other","key":"sys:1","project":"prj_worker"}],
         "findings":[{"kind":"sync-cycle","level":"warn","title":"Api and Worker call each other","key":"sys:1","project":"prj_worker","status":"new"}],
         "snapshot":{"taken_at":"2026-11-13T09:00:00Z","added":1,"changed":0,"gone":0}}
        """;

    private const string Dependencies = """
        {"nodes":[
           {"project":"fixture","project_id":"prj_fixture","packages":[]},
           {"project":"api","project_id":"prj_api","packages":["Api.Client"]},
           {"project":"worker","project_id":"prj_worker","packages":["Worker.Client"]},
           {"project":"core","project_id":"prj_core","packages":["Core.Contracts","Core.Client","Core.Unused"],"library":true}],
         "edges":[
           {"from":"prj_fixture","to":"prj_core","packages":["Core.Contracts"],"refs":1,"via":[{"path":"src/Fixture.App/Fixture.App.csproj","line":3}]},
           {"from":"prj_api","to":"prj_core","packages":["Core.Contracts","Core.Client"],"refs":2,"via":[{"path":"src/Acme.Api.Web/Acme.Api.Web.csproj","line":3},{"path":"src/Acme.Api.Jobs/Acme.Api.Jobs.csproj","line":4}]},
           {"from":"prj_worker","to":"prj_core","packages":["Core.Contracts"],"refs":1,"via":[]},
           {"from":"prj_api","to":"prj_worker","packages":["Worker.Client"],"refs":1,"via":[]}]}
        """;

    private const string Stack = """
        {"stacks":{"prj_api":{"language":"C#","badge":"C# \u00b7 .NET 10"},"prj_worker":{"language":"C#","badge":"C# \u00b7 .NET 10"},"prj_core":{"language":"C#","badge":"C# \u00b7 .NET 10"}},
         "rows":[
           {"kind":"runtime","name":".NET","versions":{"prj_api":["10.0"],"prj_worker":["9.0"]},"differ":true},
           {"kind":"core","name":"Core.Contracts","versions":{"prj_api":["1.1.0"],"prj_worker":["1.2.0"]},"owner":"prj_core","published":"1.2.0","differ":true},
           {"kind":"core","name":"Core.Client","versions":{"prj_api":["1.1.0"],"prj_worker":["1.2.0"]},"owner":"prj_core","published":"1.2.0","differ":true}]}
        """;

    private const string Rollup = """
        {"rows":[
           {"kind":"member","name":"Fixture project","member_id":"prj_fixture","levels":{},"by_design":0,"review":0,"note":"not synced since findings are recorded (harness sync)"},
           {"kind":"member","name":"Api","member_id":"prj_api","levels":{"warn":1},"by_design":0,"review":0,"snapshot":{"taken_at":"2026-11-13T09:00:00Z"}},
           {"kind":"member","name":"Worker","member_id":"prj_worker","levels":{"problem":1,"info":2},"by_design":1,"review":0,"snapshot":{"taken_at":"2026-11-13T09:00:00Z"}},
           {"kind":"member","name":"Core","member_id":"prj_core","levels":{},"by_design":0,"review":0,"snapshot":{"taken_at":"2026-11-13T09:00:00Z"}},
           {"kind":"system","name":"between the services","levels":{"warn":1},"by_design":0,"review":0,"snapshot":{"taken_at":"2026-11-13T09:00:00Z"}},
           {"kind":"shared","name":"code repeated across members","levels":{"warn":1},"by_design":0,"review":0,"snapshot":{"taken_at":"2026-11-13T09:00:00Z"}}]}
        """;

    private const string Issues = """
        {"issues":[
           {"kind":"cycle","level":"problem","subject":"Api \u21c4 Worker","summary":"each uses the other's packages","title":"Api and Worker use each other's packages","why":"Neither can ship alone.","projects":["api","worker"],"evidence":[{"project":"api","path":"Api.csproj","line":3,"note":"references Worker.Client"}]},
           {"kind":"hub","level":"info","subject":"Core","summary":"3 members depend on it","title":"Core is a hub","why":"Its changes ripple.","projects":["core"],"evidence":[]},
           {"kind":"version-drift","level":"warn","subject":"Core.Contracts","summary":"1.1.0, 1.2.0","title":"Core.Contracts at 1.1.0 and 1.2.0","why":"Two versions in one system.","projects":["api","worker"],"evidence":[]},
           {"kind":"duplicate","level":"warn","subject":"Money","title":"Money twice","why":"copies","projects":[],"evidence":[]}]}
        """;

    private const string Duplicates = """
        {"groups":[
           {"name":"Money","kind":"class","copies":[{"project":"api","path":"Money.cs","lines":20,"line":3},{"project":"worker","path":"Money.cs","lines":20}],"similarity":1,"verdict":"identical","home":{"kind":"member","reason":"Core already holds a copy","member":"Core","knowledge":["decision: money lives in Core"]}},
           {"name":"Helpers","kind":"class","copies":[{"project":"api","path":"Helpers.cs","lines":5},{"project":"worker","path":"Helpers.cs","lines":9}],"similarity":0.2,"verdict":"same name only"}],
         "notes":["similarity compares code lines","Generated files are left out."],
         "findings":[{"kind":"shared-code","level":"warn","title":"Money is written in 2 members","key":"sc:1","project":"prj_api","status":"same"}],
         "snapshot":{"taken_at":"2026-11-13T09:00:00Z","added":0,"changed":0,"gone":0}}
        """;

    private const string Adoption = """
        {"library":"core","packages":["Core.Contracts","Core.Client","Core.Unused"],"public_types":12,
         "consumers":[
           {"project":"api","packages":[{"path":"Api.csproj","matched":"Core.Contracts"},{"path":"Api.csproj","matched":"Core.Client"}],"import_files":7},
           {"project":"worker","packages":[{"path":"Worker.csproj","matched":"Core.Contracts"}],"import_files":2}],
         "non_consumers":["fixture"],
         "possible_local_implementations":[{"project":"worker","declared":{"name":"Money","kind":"class","path":"Money.cs","line":1},"library":{"name":"Money","kind":"class","path":"Money.cs","line":1}}]}
        """;

    private const string Marks = """
        {"packages":{"core.contracts@1.1.0":{"version":"1.1.0","latest":"1.2.0","vulnerabilities":[{"severity":"high","url":"https://example.com/a","versions":"< 1.2.0","fixed":"1.2.0"}]}},"notes":[]}
        """;

    [Fact]
    public Task OverviewShowsTheMembersAndTheWorkspaceAtAGlance() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await OpenAsync("overview", Api());
        var overview = (WorkspaceOverviewViewModel)Page(model).Body!;
        await Ui.UntilAsync(() => overview.Glance.HasContent);

        overview.Tiles.Select(t => t.Name).Should().Equal("Fixture project", "Api", "Core", "Worker");
        overview.Tiles[0].WaitParts.Should().Be("1 knowledge · 2 tasks");
        overview.Tiles[0].ContextLine.Should().Be("60% context hits");
        overview.Glance.Lines.Select(l => l.Text)
            .Should()
            .Equal(
                "2 problems, 4 warnings, 3 notes open",
                "Made of 2 services, 1 library, 1 outside system",
                "Api and Worker call each other",
                "1 database in the code, 1 of them per tenant",
                "Core is used by all 3 other members",
                "1 member uses older versions of packages from Core",
                ".NET versions differ"
            );
        overview.Glance.Lines[0].More.Should().Be("· most in Worker");
        overview.Glance.Lines[1].More.Should().Be("· all C# · .NET 10");
    });

    [Fact]
    public Task RuntimeShowsTheServicesTheirCallsSmellsAndContracts() => Ui.RunAsync(async () =>
    {
        var api = Api();
        var (window, _, model) = await OpenAsync("architecture", api);
        var arch = (WorkspaceArchitectureViewModel)Page(model).Body!;
        var s = arch.System;
        await Ui.UntilAsync(() => s.HasContent);

        s.Summary.Should().Be("2 services");
        s.SummaryMore.Should().Be(" · 1 outside system · 1 database in code · 2 gRPC contracts");
        s.Rows.Select(r => (r.Label, string.Join(",", r.Nodes.Select(n => n.Label)))).Should().Equal(("callers", "Api"), ("called", "Worker"), ("outside", "Payments"));
        s.Rows[0].Nodes[0].Badge.Should().Be("⛁ Orders (per tenant)");
        s.Edges.Select(e => (e.From, e.To, e.Tone)).Should().Equal(("prj_api", "prj_worker", "CDecision"), ("prj_worker", "ext:payments", "Accent"));
        s.Services.Select(x => x.Line).Should().Equal("calls Worker · 1 database", "calls Payments · called by Api");
        s.Pairs.Should().ContainSingle(p => p.Clients == "Api" && p.Servers == "Worker" && p.Contracts.SequenceEqual(new[] { "Orders" }));
        s.IdleHead.Should().Be("1 contract served, used by no other member");
        s.Board.Groups.Should().ContainSingle(g => g.Kind == "sync-cycle");

        var graph = window.GetVisualDescendants().OfType<LayerGraph>().Single(g => g.Name == "SystemGraph");
        graph.Selected = "prj_api";
        s.SelectedName.Should().Be("Api");
        s.Calls.Single().ContractsLine.Should().Be("1 contract: Orders");
        s.Databases.Single().Detail.Should().Be("one per tenant");
        s.PickCommand.Execute("prj_worker");
        s.CalledByHead.Should().Be("Called by (1)");

        // a task between the members goes to the member chosen
        _ = s.Board.TaskCommand.ExecuteAsync(s.Board.Groups[0].Shown[0]);
        var task = await DialogAsync<FindingTaskDialog>(model);
        task.Member.Id.Should().Be("prj_worker");
        task.Member = task.Members.Single(m => m.Id == "prj_api");
        await task.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Should().Be((Root + "/findings/task", """{"key":"sys:1","project":"prj_api"}""", api.Posts[^1].Origin, "1"));
    });

    [Fact]
    public Task BuildDrawsTheMapWithABusAndTheFocusedMember() => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await OpenAsync("architecture/build", Api());
        var build = ((WorkspaceArchitectureViewModel)Page(model).Body!).Build;
        await Ui.UntilAsync(() => build.HasContent);

        build.Model!.Columns.Select(c => string.Join(",", c)).Should().Equal("prj_core", "prj_fixture,prj_worker", "prj_api");
        build.Model.Library.Should().Contain("prj_core");
        build.HasCycles.Should().BeFalse();
        Ui.Settle();
        var map = window.GetVisualDescendants().OfType<DependencyMap>().Single();
        map.HasBus("prj_core").Should().BeTrue();
        map.HasRail("prj_core").Should().BeTrue("a library three members use is a rail on the right");
        map.PlaceOf("prj_core")!.Value.X.Should().BeGreaterThan(map.PlaceOf("prj_api")!.Value.X);

        map.Focused = "prj_core";
        build.FocusName.Should().Be("Core");
        build.UsedBy.Select(u => u.Name).Should().Equal("Api", "Fixture project", "Worker");
        build.UsedBy[0].Via.Should().Be("in Jobs, Web");
        build.UsedBy[0].Packages.Should().Be("Core.Contracts, Core.Client");
        Ui.Settle();
        map.NodeFor("prj_core")!.Classes.Should().Contain("focus");
        map.NodeFor("prj_api")!.Classes.Should().Contain("usedby");

        build.PickCommand.Execute("prj_api");
        build.Uses.Select(u => u.Name).Should().Equal("Core", "Worker");
        build.Indirect = true;
        build.Uses.Select(u => u.Name).Should().Equal("Core", "Worker");
        build.ClearCommand.Execute(null);
        build.HasFocus.Should().BeFalse();
    });

    [Fact]
    public Task LibrariesShowWhoUsesWhichPackage() => Ui.RunAsync(async () =>
    {
        var api = Api();
        var (_, _, model) = await OpenAsync("architecture/libraries", api);
        var libs = ((WorkspaceArchitectureViewModel)Page(model).Body!).Libraries;
        await Ui.UntilAsync(() => libs.HasDetail);

        libs.Libraries.Select(l => (l.Label, l.Count)).Should().Equal(("Core", "used by 3"), ("Worker", "used by 1"));
        api.GetsWithQuery.Should().Contain(Root + "/adoption?library=core");
        libs.Summary.Should().Be("Offers 3 packages; 2 of 3 other projects use it, 1 package nobody here uses.");
        libs.Columns.Should().Equal("Api", "Worker");
        libs.Packages.Select(p => (p.Name, string.Join("", p.Cells), p.UsedBy)).Should().Equal(("Core.Contracts", "✓✓", "2 of 3"), ("Core.Client", "✓·", "1 of 3"), ("Core.Unused", "··", "nobody"));
        libs.Imports.Should().Equal("7", "2");
        libs.Copies.Should().Equal("none", "▲ 1");
        libs.NotUsing.Should().Be("Not using it: fixture.");
    });

    [Fact]
    public Task IssuesRollUpTheFindingsDesignIssuesAndSharedCode() => Ui.RunAsync(async () =>
    {
        var api = Api();
        var (_, _, model) = await OpenAsync("issues", api);
        var issues = (WorkspaceIssuesViewModel)Page(model).Body!;
        await Ui.UntilAsync(() => issues.Rollup.HasContent && issues.Issues.HasContent && issues.Shared.HasContent);

        issues.Rollup.Members.Select(r => r.Name).Should().Equal("Worker", "Api", "Core", "Fixture project");
        issues.Rollup.Members[0].Counts.Should().Be("1 problem, 2 notes");
        issues.Rollup.Members[0].Marks.Should().Be("1 by design");
        issues.Rollup.Members[3].HasNote.Should().BeTrue();
        issues.Rollup.Members[2].Clear.Should().BeTrue();
        issues.Rollup.Between.Select(r => r.Href).Should().Equal("#/w/main/architecture", "#/w/main/issues/shared");

        var design = issues.Issues;
        design.Counts.Should().Be("1 problem · 1 note");
        design.Groups.Select(g => g.Label).Should().Equal("Cycles", "Hubs");
        design.Selection!.Subject.Should().Be("Api ⇄ Worker");
        design.Todo.Should().StartWith("Find the dependency");
        design.Places.Single().Where.Should().Be("Api.csproj:3");
        design.Projects.Should().Equal("Api", "Worker");
        design.Chips[0].On = false;
        design.Groups.Select(g => g.Label).Should().Equal("Hubs");
        design.Selection.Subject.Should().Be("Core");

        var shared = issues.Shared;
        shared.Board.Groups.Should().ContainSingle(g => g.Kind == "shared-code");
        shared.Rows.Select(r => r.Name).Should().Equal("Money");
        shared.Rows[0].Home.Should().Be("Core");
        shared.Rows[0].Where.Should().Equal("Api  Money.cs:3", "Worker  Money.cs");
        shared.Chips.Single(c => c.Key == "same name only").On = true;
        shared.Rows.Select(r => r.Name).Should().Equal("Money", "Helpers");
        shared.Query = "help";
        shared.Rows.Select(r => r.Name).Should().Equal("Helpers");
        shared.Notes.Should().Equal("Generated files are left out.");

        // the shared code's findings are triaged in the workspace
        _ = shared.Board.MarkCommand.ExecuteAsync(shared.Board.Groups[0].Shown[0]);
        var mark = await DialogAsync<FindingMarkDialog>(model);
        mark.Reason = "kept apart";
        await mark.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Should().Be((Root + "/findings/mark", """{"key":"sc:1","reason":"kept apart"}""", api.Posts[^1].Origin, "1"));
    });

    [Fact]
    public Task VersionDriftListsTheDriftAndComparesTheVersions() => Ui.RunAsync(async () =>
    {
        var api = Api();
        var (_, _, model) = await OpenAsync("issues/versions", api);
        var issues = (WorkspaceIssuesViewModel)Page(model).Body!;
        await Ui.UntilAsync(() => issues.Drift.HasContent && issues.Versions.HasContent);

        issues.ShowsVersions.Should().BeTrue();
        issues.Drift.Groups.Should().ContainSingle(g => g.Label == "Version drift");
        var v = issues.Versions;
        v.Columns.Should().Equal("Api", "Core", "Worker");
        v.Rows.Select(r => r.IsGroup ? "# " + r.Group : r.Name).Should().Equal("# Runtime", ".NET", "# The members' own packages", "Core.Contracts");
        var core = v.Rows[3];
        (core.More, core.Owner, core.Declared, core.Stale).Should().Be(("and 1 more package", "from Core", "1.2.0", false));
        core.Cells.Select(c => (c.Text, c.Behind)).Should().Equal(("1.1.0", true), ("—", false), ("1.2.0", false));
        v.Rows[1].Cells.Select(c => (c.Text, c.Behind)).Should().Equal(("10.0", false), ("—", false), ("9.0", true));
        await Ui.UntilAsync(() => core.Cells[0].HasMarks);
        core.Cells[0].Marks.Should().Contain("Core.Contracts: ⓘ newer stable 1.2.0 (you use 1.1.0)").And.Contain("⚠ high vulnerability, fixed in 1.2.0");
        core.Cells[0].Severe.Should().BeTrue();
        api.GetsWithQuery.Should().Contain(g => g.StartsWith("/api/projects/prj_api/packages?packages=", StringComparison.Ordinal));
    });

    [Fact]
    public Task MembersAreRemovedAddedAndTheWorkspaceDeletedAfterAReview() => Ui.RunAsync(async () =>
    {
        var api = Api();
        var (_, _, model) = await OpenAsync("members", api);
        var members = (WorkspaceMembersViewModel)Page(model).Body!;

        members.Common.Should().Be("C:/repos");
        members.Rows.Select(r => (r.Name, r.Path)).Should().Equal(("Api", "api"), ("Core", "core"), ("Fixture project", "fixture"), ("Worker", "worker"));
        members.Query = "wor";
        members.Rows.Select(r => r.Name).Should().Equal("Worker");
        members.AllChosen = true;
        members.RemoveLabel.Should().Be("Remove selected (1)");
        members.Query = "";
        members.Rows.Single(r => r.Name == "Api").Chosen = true;
        members.ChosenCount.Should().Be(2);

        _ = members.RemoveCommand.ExecuteAsync(null);
        var confirm = await DialogAsync<ConfirmDialog>(model);
        confirm.Title.Should().Be("Remove 2 projects from Main workspace?");
        confirm.Message.Should().Contain("− Api").And.Contain("− Worker");
        await confirm.AcceptCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => api.Posts.Count == 2);
        api.Posts.Select(p => (p.Path, p.Body)).Should().Equal((Root + "/leave", """{"project_id":"prj_api"}"""), (Root + "/leave", """{"project_id":"prj_worker"}"""));

        members.CanAdd.Should().BeTrue();
        _ = members.AddCommand.ExecuteAsync(null);
        var add = await DialogAsync<AddProjectsDialog>(model);
        add.Picker.Rows.Select(r => r.Name).Should().Equal("Other");
        await add.AcceptCommand.ExecuteAsync(null);
        add.Error.Should().Be("Choose at least one project.");
        add.Picker.Rows[0].Chosen = true;
        await add.AcceptCommand.ExecuteAsync(null);
        add.Reviewing.Should().BeTrue();
        add.ReviewText.Should().Contain("Joining Main workspace").And.Contain("+ Other");
        await add.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Should().Be((Root + "/join", """{"project_ids":["prj_other"]}""", api.Posts[^1].Origin, "1"));

        _ = members.DeleteCommand.ExecuteAsync(null);
        var delete = await DialogAsync<ConfirmDialog>(model);
        delete.Danger.Should().BeTrue();
        await delete.AcceptCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => api.Posts.Count == 4);
        api.Posts[^1].Path.Should().Be(Root + "/delete");
        await Ui.UntilAsync(() => model.Page is HomePage);
    });

    [Fact]
    public Task ANewWorkspaceIsNamedGivenProjectsAndReviewedFromHome() => Ui.RunAsync(async () =>
    {
        var api = Api();
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, address: "#/");
        await Ui.UntilAsync(() => model.Page is HomePage);
        var home = (HomePage)model.Page!;

        home.NewWorkspaceCommand!.Execute(null);
        var form = await DialogAsync<WorkspaceFormDialog>(model);
        form.DisplayTitle = "Company Services";
        form.ShortName.Should().Be("company-services");
        form.ShortName = "main";
        await form.AcceptCommand.ExecuteAsync(null);
        form.Error.Should().Be("A workspace main exists already.");
        form.ShortName = "company";
        form.DisplayTitle = "Company services";
        form.ShortName.Should().Be("company", "an edited short name no longer follows");
        form.Picker.Query = "other";
        form.Picker.ChooseShownCommand.Execute(null);
        form.Picker.ChosenLine.Should().Be("1 chosen");
        await form.AcceptCommand.ExecuteAsync(null);
        form.ReviewText.Should().Contain("Company services  (workspace:company)").And.Contain("+ Other");
        await form.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Should().Be(("/api/workspaces", """{"name":"company","title":"Company services","project_ids":["prj_other"]}""", api.Posts[^1].Origin, "1"));
        model.Services.Host.Navigation.Current.Href.Should().Be("#/w/company/overview");
    });

    [Fact]
    public void ShortNamesFollowTheDisplayName()
    {
        WorkspaceText.Slug("Café Services!").Should().Be("cafe-services");
        WorkspaceText.Slug("  ").Should().BeEmpty();
        WorkspaceText.IsShortName("a.b_c-d").Should().BeTrue();
        WorkspaceText.IsShortName("-a").Should().BeFalse();
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("architecture")]
    [InlineData("architecture/build")]
    [InlineData("architecture/libraries")]
    [InlineData("issues")]
    [InlineData("issues/versions")]
    [InlineData("members")]
    public Task WorkspacePagesPassTheUiChecks(string view) =>
        Ui.RunAsync(() =>
            UiChecks.AssertAsync(async (width, height) =>
            {
                var (window, _, model) = await OpenAsync(view, Api(), width, height);
                await Ui.UntilAsync(() => Ready(Page(model).Body));
                Ui.Settle();
                return window;
            })
        );

    private static bool Ready(object? body) =>
        body switch
        {
            WorkspaceOverviewViewModel o => o.Glance.HasContent,
            WorkspaceArchitectureViewModel { ShowsRuntime: true } a => a.System.HasContent,
            WorkspaceArchitectureViewModel { ShowsBuild: true } a => a.Build.HasContent,
            WorkspaceArchitectureViewModel a => a.Libraries.HasDetail,
            WorkspaceIssuesViewModel i => i.ShowsFindings ? i.Rollup.HasContent && i.Issues.HasContent && i.Shared.HasContent : i.Drift.HasContent && i.Versions.HasContent,
            _ => true,
        };

    private static FixtureApi Api()
    {
        var api = new FixtureApi();
        api.Answers["/api/projects"] = Projects;
        api.Answers["/api/workspaces"] = Workspaces;
        api.Answers[Root + "/system"] = System;
        api.Answers[Root + "/dependencies"] = Dependencies;
        api.Answers[Root + "/stack"] = Stack;
        api.Answers[Root + "/findings"] = Rollup;
        api.Answers[Root + "/issues"] = Issues;
        api.Answers[Root + "/duplicates"] = Duplicates;
        api.Answers[Root + "/adoption"] = Adoption;
        api.Answers["/api/projects/prj_api/packages"] = Marks;
        api.Answers["/api/projects/prj_worker/packages"] = """{"packages":{},"notes":[]}""";
        return api;
    }

    private static async Task<(Window Window, FixtureApi Api, MainViewModel Model)> OpenAsync(string view, FixtureApi api, int width = 1280, int height = 800)
    {
        var opened = await Ui.OpenMainAsync(width, height, api: api, address: $"#/w/main/{view}");
        await Ui.UntilAsync(() => opened.Model.Page is WorkspacePage { Body: not null });
        Ui.Settle();
        return opened;
    }

    private static WorkspacePage Page(MainViewModel model) => (WorkspacePage)model.Page!;

    private static async Task<T> DialogAsync<T>(MainViewModel model)
        where T : DialogViewModel
    {
        await Ui.UntilAsync(() => model.Services.Dialogs.Current is T);
        Ui.Settle();
        return (T)model.Services.Dialogs.Current!;
    }
}
