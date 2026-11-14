using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Core.Tasks;
using Harness.Infrastructure.Memory;
using Harness.Integration.Sync;
using Harness.Ui.Api;
using Xunit;
using BlueprintApproval = Harness.Ui.Api.BlueprintApproval;
using BlueprintRemoval = Harness.Ui.Api.BlueprintRemoval;

namespace Harness.Integration.Web;

/// <summary>
/// The Avalonia UI's typed contracts (src/Harness.Ui/Api, compiled in here) against a real harness ui on a seeded
/// project: every read the contracts cover deserialises strictly (a required member missing or renamed fails), writes
/// round-trip, a change arrives on /api/events, and a refusal carries the server's message. A view task adds its
/// endpoints here with its contracts.
/// </summary>
[Collection("Provider processes")]
public sealed class UiContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheUiContractsReadWriteAndFollowARealHarnessUi()
    {
        using var fixture = await SyncFixture.CreateAsync();
        UiFixtureShapeTests.Seed(fixture);
        await UiFixtureShapeTests.CodeAsync(fixture);
        using (var database = fixture.Database())
        {
            // a rule the code breaks: a boundary finding, which every read recomputes, to triage
            new MemoryStore(database).Put(new() { Type = "constraint", Content = "forbid: Fixture.App -> Fixture.Core", Force = true }, Token);
        }
        await using var server = await UiFixture.StartAsync(fixture);
        var id = (string)JsonNode.Parse(await server.Client.GetStringAsync("/api/projects", Token))!["projects"]![0]!["id"]!;
        await UiFixtureShapeTests.TitleAsync(server, id, "Fixture project");
        await UiFixtureShapeTests.PostAsync(
            server,
            "/api/workspaces",
            new JsonObject { ["name"] = "main", ["title"] = "Main workspace", ["project_ids"] = new JsonArray(id) }
        );
        await UiFixtureShapeTests.KnowledgeAsync(server);
        await UiFixtureShapeTests.MachineAsync(server);
        var api = new HarnessApi(server.Client) { Origin = "http://" + server.Client.BaseAddress!.Authority };

        // projects and workspaces
        var project = (await api.ProjectsAsync(Token)).Should().ContainSingle().Subject;
        project.Id.Should().Be(id);
        project.DisplayName.Should().Be("Fixture project");
        project.Live.State.Should().Be("ok");
        project.Workspaces.Should().Equal("main");
        var workspace = (await api.WorkspacesAsync(Token)).Should().ContainSingle().Subject;
        workspace.DisplayName.Should().Be("Main workspace");
        workspace.Members.Should().ContainSingle(m => m.ProjectId == id);

        // the Board, and a write that round-trips
        var board = await api.BoardAsync(id, Token);
        board.Columns.Select(c => c.Status).Should().Equal(TaskBoard.Statuses);
        board.Columns.Single(c => c.Status == "ready").Tasks.Should().ContainSingle(t => t.Id == "TASK-0001" && t.Priority == "high");
        board.Blueprints.Should().Contain(b => b.Id == "BP-009" && b.Color == "yellow");
        await api.SetBoardAsync(id, new BoardChange { Sort = "id", HideDoneDays = 14 }, Token);
        var saved = await api.BoardAsync(id, Token);
        saved.Settings.Sort.Should().Be("id");
        saved.Settings.HideDoneDays.Should().Be(14);

        // the Overview: the tasks, the report and trends over a window, and the activity (the seeded claim)
        var tasks = (await api.TasksAsync(id, Token)).Tasks;
        tasks.Select(t => (t.Id, t.Status))
            .Should()
            .BeEquivalentTo([("TASK-0001", "ready"), ("TASK-0002", "done"), ("TASK-0003", "claimed"), ("TASK-0004", "backlog")]);
        tasks.Single(t => t.Id == "TASK-0001").Acceptance.Should().ContainSingle(c => c.Criterion == "It runs" && c.Result == "pending");
        tasks.Single(t => t.Id == "TASK-0003").Claim!.Claimant.Should().Be("claude-code");
        tasks.Single(t => t.Id == "TASK-0004").DependsOn.Should().ContainSingle(d => d.Id == "TASK-0001" && d.Status == "ready");
        tasks.Single(t => t.Id == "TASK-0002").Completion!.CompletedAt.Should().NotBeNullOrEmpty();
        var report = await api.ReportAsync(id, 14, Token);
        report.Days.Should().Be(14);
        report.Context.AfterRead.Should().NotBeNull();
        report.Findings.Should().NotBeNull().And.OnlyContain(f => f.Audience == "user");
        var trends = await api.TrendsAsync(id, 14, Token);
        trends.Days.Should().HaveCount(14);
        trends.Days[^1].Done.Should().Be(1, "TASK-0002 was imported as done today");
        var activity = await api.ActivityAsync(id, 7, 50, Token);
        activity.Items.Should().ContainSingle(i => i.Kind == "task" && i.Ref == "TASK-0003" && i.ProjectId == id);
        (await api.ActivityAsync(null, 7, 50, Token)).Items.Should().HaveCount(activity.Items.Count);

        // Health: the checks, a probe from harness ui's own page, a fix, and a sync that then shows as a run
        var health = await api.HealthAsync(id, probe: false, Token);
        health.Probed.Should().BeFalse();
        health.Status.Project!.Id.Should().Be(id);
        health.Status.Database.Exists.Should().BeTrue();
        health.Checks.Should().Contain(c => c.Name == "claude-code" && c.Level != "ok" && c.Fix != null);
        health.Fixable.Should().Contain("claude-code");
        (await api.HealthAsync(id, probe: true, Token)).Probed.Should().BeTrue();
        var fixedCheck = await api.FixAsync(id, new FixRequest { Check = "claude-code" }, Token);
        fixedCheck.Ok.Should().BeTrue(fixedCheck.Output);
        fixedCheck.Message.Should().Be("harness install --client claude-code done");
        var synced = await api.FixAsync(id, new FixRequest { Sync = true }, Token);
        synced.Ok.Should().BeTrue(synced.Output);
        health = await api.HealthAsync(id, probe: false, Token);
        health.Status.Sync!.SyncedAt.Should().NotBeNull();
        health.Status.Sync.Stale.Should().BeNull();
        var run = health.Status.Sync.Runs.Should().NotBeEmpty().And.Subject.First();
        run.Status.Should().NotBeNullOrEmpty();
        run.HeadCommit.Should().Be(health.Status.Git!.Commit);
        await FluentActions
            .Awaiting(() => api.FixAsync(id, new FixRequest { Check = "config" }, Token))
            .Should()
            .ThrowAsync<HarnessApiException>();

        // Work: the reads, then every write the view sends
        var blueprints = (await api.BlueprintsAsync(id, Token)).Blueprints;
        blueprints.Select(b => b.Id).Should().Equal("BP-009", "BP-008");
        blueprints[0].Exit.Should().Equal("Released", "Documented");
        blueprints[0].Tasks.Should().ContainKey("ready");
        (await api.NextTaskIdAsync(id, Token)).Id.Should().Be("TASK-0005");
        (await api.NextBlueprintIdAsync(id, Token)).Id.Should().Be("BP-010");
        (await api.TaskEventsAsync(id, "TASK-0001", Token)).Events.Should().Contain(e => e.Event == "unlocked" && e.To == "ready");
        (await api.TaskSessionsAsync(id, "TASK-0001", Token)).Sessions.Should().BeEmpty();
        var taskProposal = (await api.TaskProposalsAsync(id, "pending", Token)).Proposals.Should().ContainSingle().Subject;
        taskProposal.Task.DependsOn.Should().Equal("TASK-0001");
        var blueprintProposal = (await api.BlueprintProposalsAsync(id, "pending", Token)).Proposals.Should().ContainSingle().Subject;
        blueprintProposal.Blueprint.Exit.Should().Equal("Done when shipped");
        var archProposal = (await api.ArchProposalsAsync(id, "pending", Token)).Proposals.Should().ContainSingle().Subject;
        archProposal.Basis.Should().Be("observation");

        await api.CreateBlueprintAsync(id, new BlueprintForm { Id = "BP-010", Title = "Offline", Exit = ["Works offline"], Color = "teal" }, Token);
        await api.EditBlueprintAsync(id, "BP-010", new BlueprintForm { Title = "Offline search", Notes = "- one\n- two" }, Token);
        await api.CreateTaskAsync(
            id,
            new TaskForm { Title = "Search offline", Type = "bug", Priority = "high", Blueprint = "BP-010", DependsOn = ["TASK-0001"], Acceptance = ["Works"], Scope = ["src"] },
            Token
        );
        await api.EditTaskAsync(id, "TASK-0005", new TaskForm { Title = "Search offline, restated", Blueprint = "BP-010", Acceptance = ["Works"] }, Token);
        await api.MoveTaskAsync(id, "TASK-0005", "BP-009", Token);
        await api.OrderBlueprintsAsync(id, ["BP-010", "BP-009", "BP-008"], Token);
        var created = (await api.TasksAsync(id, Token)).Tasks.Single(t => t.Id == "TASK-0005");
        (created.Title, created.Blueprint, created.Type, created.Priority).Should().Be(("Search offline, restated", "BP-009", "feature", "medium"));
        var ordered = (await api.BlueprintsAsync(id, Token)).Blueprints;
        ordered.Select(b => b.Id).Should().Equal("BP-010", "BP-009", "BP-008");
        (ordered[0].Title, ordered[0].Color, ordered[0].Notes).Should().Be(("Offline search", "teal", "- one\n- two"));
        await api.RemoveBlueprintAsync(id, "BP-010", new BlueprintRemoval(), Token);
        await api.DeleteTaskAsync(id, "TASK-0005", Token);
        (await api.TasksAsync(id, Token)).Tasks.Should().NotContain(t => t.Id == "TASK-0005");

        await api.ApproveTaskAsync(id, taskProposal.Id, new TaskForm { Title = "Proposed task", Blueprint = "BP-009", Acceptance = ["Checked"] }, Token);
        await api.ApproveBlueprintAsync(id, blueprintProposal.Id, new BlueprintApproval { Edit = new BlueprintForm { Title = "Proposed blueprint" } }, Token);
        await api.RejectAsync(id, "arch", archProposal.Id, "not now", Token);
        (await api.TaskProposalsAsync(id, "approved", Token)).Proposals.Should().ContainSingle(p => p.Result != null);
        (await api.BlueprintProposalsAsync(id, "approved", Token)).Proposals.Should().ContainSingle();
        (await api.ArchProposalsAsync(id, "rejected", Token)).Proposals.Should().ContainSingle(p => p.Note == "not now");
        await FluentActions
            .Awaiting(() => api.EditTaskAsync(id, "TASK-0003", new TaskForm { Title = "Claimed" }, Token))
            .Should()
            .ThrowAsync<HarnessApiException>("a claimed task's definition is fixed");

        // Knowledge: a project's memories with the levels', what applies, a memory's history, the proposals, the
        // levels themselves, and every write
        var memories = (await api.MemoriesAsync(id, null, inactive: true, Token)).Memories;
        memories.Select(m => m.Scope).Should().Contain(["project", "path:src/Harness.Ui", "user", "workspace:main"]);
        var decision = memories.Single(m => m.Type == "decision" && m.Scope == "project");
        decision.SourceType.Should().Be("user");
        var superseded = memories.Single(m => m.Content == "The old warning");
        superseded.SupersededBy.Should().NotBeNullOrEmpty();
        (await api.MemoriesAsync(id, "compiled", inactive: false, Token)).Memories.Should().ContainSingle(m => m.Type == "convention");
        (await api.MemoriesAsync(id, null, false, Token, removed: true)).Memories.Should().BeEmpty();
        var history = await api.MemoryAsync(id, superseded.SupersededBy, null, Token);
        history.History.Select(m => m.Content).Should().Equal("The old warning", "The corrected warning");
        history.Events.Should().NotBeEmpty();
        var applies = await api.AppliesAsync(id, "path:src/Harness.Ui", Token);
        applies.Decisions.Should().Contain(m => m.Content == "Views use compiled bindings.");
        var memoryProposal = (await api.MemoryProposalsAsync(id, "pending", Token)).Proposals.Should().ContainSingle().Subject;
        memoryProposal.Rationale.Should().Be("seen in the code");
        var user = await api.LevelAsync("user", Token);
        user.Memories.Should().ContainSingle(m => m.Content == "Write commit messages as one line" && m.SourceRef == "review");
        (await api.LevelAsync("workspace:main", Token)).Memories.Should().ContainSingle(m => m.Content == "The workspace ships together");
        (await api.MemoryAsync(null, user.Memories[0].Id, "user", Token)).Memory.Scope.Should().Be("user");

        var added = await api.AddMemoryAsync(id, new MemoryAdd { Type = "fact", Content = "Tenant ids come from a table", Scope = "subsystem:tenants", SourceRef = "wiki" }, Token);
        (added.Status, added.Memory!.Scope, added.Memory.SourceRef).Should().Be(("created", "subsystem:tenants", "wiki"));
        await api.ChangeMemoryAsync(id, added.Memory.Id, new MemoryChange { Content = "Tenant ids come from the tenants table", Reason = "precise" }, Token);
        var corrected = (await api.MemoriesAsync(id, "tenants table", false, Token)).Memories.Single();
        await api.ChangeMemoryAsync(id, corrected.Id, new MemoryChange { Reason = "test complete" }, Token);
        var dry = await api.PromoteAsync(id, decision.Id, new Promotion { To = "user", DryRun = true }, Token);
        (dry.Status, dry.Level).Should().Be(("would_create", "user"));
        var shared = await api.ShareAsync(id, decision.Id, share: true, Token);
        shared.Shared.Should().Be("project");
        (await api.ShareAsync(id, decision.Id, share: false, Token)).Shared.Should().NotBe("project");
        var levelAdded = await api.AddMemoryAsync(null, new MemoryAdd { Type = "warning", Content = "Never push on Fridays", Level = "user", SourceRef = "team rule" }, Token);
        levelAdded.Memory!.SourceRef.Should().Be("team rule", "the level endpoint takes source_ref as every other write does");
        await api.ChangeMemoryAsync(null, levelAdded.Memory.Id, new MemoryChange { Content = "Never deploy on Fridays", Reason = "deploy, not push", Level = "user" }, Token);
        // approved at once, or first asked which current memory it replaces
        var approval = await api.ApproveMemoryAsync(id, memoryProposal.Id, new ProposalApproval(), Token);
        if (approval.Status is "needs_decision")
        {
            approval.Similar.Should().NotBeNullOrEmpty();
            approval = await api.ApproveMemoryAsync(id, memoryProposal.Id, new ProposalApproval { KeepBoth = true }, Token);
        }
        approval.Status.Should().NotBe("needs_decision");
        (await api.MemoryProposalsAsync(id, "approved", Token)).Proposals.Should().ContainSingle(p => p.MemoryId != null);

        // Machine, Search and Docs: the installation, Board defaults, telemetry, SCM connections and graphify; a search
        // across the machine; a docs section
        var machine = await api.MachineAsync(Token);
        machine.Build.Version.Should().NotBeNullOrEmpty();
        machine.Paths.Global.Should().NotBeNullOrEmpty();
        machine.Telemetry.Enabled.Should().BeFalse();
        (await api.BoardDefaultsAsync(Token)).Defaults.Sort.Should().Be("priority");
        (await api.SetBoardDefaultsAsync(new BoardDefaultsChange { HideDoneDays = 3 }, Token)).HideDoneDays.Should().Be(3);
        var on = await api.SetTelemetryAsync(new TelemetryChange { Enabled = true, Endpoint = "http://127.0.0.1:9/v1/report", Token = "t" }, Token);
        (on.Enabled, on.Endpoint).Should().Be((true, "http://127.0.0.1:9/v1/report"));
        (await api.SetTelemetryAsync(new TelemetryChange { Enabled = false }, Token)).Enabled.Should().BeFalse();
        var scm = (await api.ScmAsync(Token)).Scm.Should().ContainSingle().Subject;
        (scm.Name, scm.Type, scm.Address, scm.TokenAvailable).Should().Be(("work", "gitlab", "https://gitlab.example.com", true));
        scm.Token.Should().NotContain("glpat-fixture-token", "a token never comes back");
        scm.Aliases.Should().Equal("ssh-gitlab");
        await api.SaveScmAsync("work", new ScmForm { Name = "work", Type = "gitlab", Address = "https://gitlab.example.org", Aliases = ["ssh-gitlab", "git.example.org"] }, Token);
        var edited = (await api.ScmAsync(Token)).Scm.Single();
        (edited.Address, edited.TokenAvailable).Should().Be(("https://gitlab.example.org", true), "an empty token keeps the stored one");
        edited.Aliases.Should().Equal("ssh-gitlab", "git.example.org");
        await api.SaveScmAsync(null, new ScmForm { Name = "spare", Type = "forgejo", Address = "https://forgejo.example.com", Token = "t" }, Token);
        await api.RemoveScmAsync("spare", Token);
        (await api.ScmAsync(Token)).Scm.Select(e => e.Name).Should().Equal("work");
        (await api.GraphifySessionsAsync(id, Token)).Sessions.Should().NotBeNull();
        var found = await api.SearchAsync("Fixture", Token);
        found.Hits.Should().Contain(h => h.Kind == "project" && h.ProjectId == id);
        found.PerKind.Should().BePositive();
        var doc = await api.DocAsync("guide", Token);
        (doc.Doc, doc.Slug).Should().Be(("guide", "guide"));
        doc.Body.Should().StartWith("# ");

        // Agents: the report's use, the providers and architecture evidence, the project's SCM card and its writes, hooks
        var serving = await api.ReportAsync(id, 30, Token);
        serving.Metrics.Should().NotBeNull();
        serving.Knowledge!.CurrentMemories.Should().BePositive();
        var providers = await api.ProvidersAsync(id, 30, Token);
        providers.Architecture.Should().Contain(c => c.Capability == "arch.dependencies");
        (await api.ArchitectureRawAsync(id, Token)).ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
        var card = await api.ProjectScmAsync(id, 30, Token);
        (card.Configured, card.Repository, card.Scm!.Status).Should().Be((true, true, "no_remote"));
        card.Stats!.Days.Should().Be(30);
        card.Stats.Trend!.Days.Should().NotBeEmpty();
        (await api.ChooseScmAsync(id, "", Token)).Choice.Should().BeNull();
        (await FluentActions.Awaiting(() => api.TestProjectScmAsync(id, Token)).Should().ThrowAsync<HarnessApiException>())
            .Which.Message.Should().Contain("no SCM to test");
        var hooks = await api.HooksAsync(id, Token);
        hooks.Git.Should().Contain(g => g.Name == "post-commit");
        hooks.Clients.Should().Contain(c => c.Client == "codex");
        hooks.Events.Should().NotBeNull();

        // Architecture: the structure with its findings and their triage, the stack, complexity, change coupling, package marks
        var structure = await api.StructureAsync(id, Token);
        (structure.Known, structure.Architecture!.Language, structure.Architecture.Layers).Should().Be((true, "dotnet", 3));
        structure.Architecture.Projects.Select(p => p.Name).Should().BeEquivalentTo("Fixture.App", "Fixture.Core", "Fixture.Tests");
        structure.Architecture.Projects.Single(p => p.Name == "Fixture.Core").Packages.Should().ContainSingle(p => p.Name == "Newtonsoft.Json");
        var boundary = structure.Findings!.Should().ContainSingle(f => f.Kind == "boundary").Subject;
        boundary.Rule!.Memory.Should().StartWith("mem_");
        await api.FindingAsync(id, "mark", new FindingAction { Key = boundary.Key!, Reason = "an entry point" }, Token);
        var marked = (await api.StructureAsync(id, Token)).Findings!.Single(f => f.Key == boundary.Key);
        (marked.Mark!.State, marked.Mark.Reason).Should().Be(("by_design", "an entry point"));
        await api.FindingAsync(id, "unmark", new FindingAction { Key = boundary.Key! }, Token);
        (await api.StructureAsync(id, Token)).Findings!.Single(f => f.Key == boundary.Key).Mark.Should().BeNull();
        await api.FindingAsync(id, "task", new FindingAction { Key = boundary.Key!, Project = "" }, Token);
        var tasked = (await api.StructureAsync(id, Token)).Findings!.Single(f => f.Key == boundary.Key);
        tasked.Mark!.State.Should().Be("task");
        tasked.Mark.TaskId.Should().StartWith("ARCH-");
        (await api.StackAsync(id, Token)).Stack!.Runtime!.Name.Should().Be(".NET");
        var complexity = (await api.ComplexityAsync(id, Token)).Languages.Should().ContainSingle().Subject;
        (complexity.Language, complexity.Max).Should().Be(("csharp", 15));
        complexity.Top.Should().Contain(f => f.Name == "Rules.Grade");
        var coupling = await api.CouplingAsync(id, Token);
        coupling.Hotspots.Should().Contain(h => h.Path == "src/Fixture.Core/Rules.cs");
        (await api.PackageMarksAsync(id, [], Token)).Packages.Should().BeEmpty();

        // the workspace pages: system, build dependencies, stack, findings rollup, issues, shared code, a library, members
        var system = await api.SystemAsync("main", Token);
        system.Nodes.Should().NotBeNull();
        system.Links.Should().NotBeNull();
        var member = (await api.DependenciesAsync("main", Token)).Nodes.Should().ContainSingle(n => n.ProjectId == id).Subject;
        (await api.WorkspaceStackAsync("main", Token)).Stacks.Should().ContainKey(id);
        (await api.RollupAsync("main", Token)).Rows.Select(r => r.Kind).Should().Equal("member", "system", "shared");
        (await api.IssuesAsync("main", Token)).Issues.Should().NotBeNull();
        (await api.DuplicatesAsync("main", Token)).Groups.Should().NotBeNull();
        (await api.AdoptionAsync("main", member.Project, Token)).Library.Should().Be(member.Project);
        var discovered = await api.DiscoverAsync(Path.GetDirectoryName(fixture.Root)!, Token);
        discovered.Projects.Should().Contain(p => p.ProjectId == id);
        var spare = await api.CreateWorkspaceAsync(new WorkspaceCreate { Name = "spare", Title = "Spare", ProjectIds = [] }, Token);
        spare.Workspace.Should().Be("spare");
        (await api.JoinAsync("spare", [id], Token)).Added.Should().Equal(id);
        await api.LeaveAsync("spare", id, Token);
        (await api.WorkspacesAsync(Token)).Single(w => w.Name == "spare").Members.Should().BeEmpty();
        await api.DeleteWorkspaceAsync("spare", Token);
        (await api.WorkspacesAsync(Token)).Select(w => w.Name).Should().Equal("main");

        // the docs
        var docs = await api.DocsAsync(Token);
        docs.Docs.Select(d => d.Name).Should().Equal("guide", "reference");
        docs.Sections.Should().NotBeEmpty();

        // a change arrives on /api/events after a write
        using var follow = CancellationTokenSource.CreateLinkedTokenSource(Token);
        follow.CancelAfter(TimeSpan.FromSeconds(30));
        var opened = new TaskCompletionSource();
        var changes = api.ChangesAsync(() => opened.TrySetResult(), follow.Token).GetAsyncEnumerator(follow.Token);
        var next = changes.MoveNextAsync().AsTask();
        await opened.Task.WaitAsync(follow.Token);
        await api.PostAsync($"api/projects/{id}/rename", new RenameChange { Name = "fixture-short" }, UiJsonContext.Default.RenameChange, Token);
        (await next).Should().BeTrue("a change follows the write");
        (changes.Current.Registry || changes.Current.Projects.Contains(id)).Should().BeTrue();
        await follow.CancelAsync();
        await changes.DisposeAsync();
        (await api.ProjectsAsync(Token)).Single().Name.Should().Be("fixture-short");

        // a refusal carries the server's message
        var refused = await FluentActions
            .Awaiting(() => api.SetBoardAsync(id, new BoardChange { Sort = "nonsense" }, Token))
            .Should()
            .ThrowAsync<HarnessApiException>();
        refused.Which.Message.Should().Contain("400").And.Contain("nonsense");
    }
}
