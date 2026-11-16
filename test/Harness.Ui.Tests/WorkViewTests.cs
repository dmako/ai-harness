using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Api;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The Avalonia Work against the fixture API: Blueprints (cards, filter, create, edit, colour, remove, order), Tasks
/// (filters, rows, the task dialog, create, edit, move, delete), the Inbox (approve and reject each kind), the Board's
/// card id, dragging with the pointer, and the UI checks at every size and theme. Each action's request is checked.
/// </summary>
public sealed class WorkViewTests
{
    private const string Root = "/api/projects/prj_fixture";

    [Fact]
    public Task BlueprintsShowEachBlueprintsStateProgressAndDoneWhen() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await OpenAsync("blueprints");
        var plan = Work(model).Plan;

        plan.CardChips.Single(c => c.On).Key.Should().Be("open");
        plan.Cards.Select(c => (c.Id, c.StateLabel)).Should().Equal(("BP-009", "in progress"));
        var bp9 = plan.Cards[0];
        bp9.Heading.Should().Be("V13 Features and bugfixes");
        bp9.FrameClass.Should().Be("bp-yellow");
        bp9.ProgressText.Should().Be("0 of 2 tasks done");
        bp9.Counts.Select(c => c.Text).Should().Equal("ready 1");
        bp9.ExitShown.Should().Equal("Released", "Documented");
        bp9.NoteItems.Should().Equal("one", "two");

        plan.CardChips.Single(c => c.Key == "all").On = true;

        plan.Cards.Select(c => (c.Id, c.StateLabel)).Should().Equal(("BP-009", "in progress"), ("BP-008", "done"));
        plan.Cards[0].CanMoveUp.Should().BeFalse();
        plan.Cards[0].CanMoveDown.Should().BeTrue();
    });

    [Fact]
    public Task ANewBlueprintIsSentWithItsSuggestedIdAndChosenColour() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("blueprints");

        _ = Work(model).Plan.NewBlueprintCommand.ExecuteAsync(null);
        var form = await DialogAsync<BlueprintFormDialog>(model);
        await Ui.UntilAsync(() => form.BlueprintId == "BP-010");
        form.Colors[0].IsAuto.Should().BeTrue("a new blueprint may leave its colour to harness");
        await form.AcceptCommand.ExecuteAsync(null);
        form.Error.Should().StartWith("Title");
        form.BlueprintTitle = "Search works offline";
        form.Goal = "No network needed";
        form.Exit[0].Text = "Works on a plane";
        form.Color = form.Colors.Single(c => c.Name == "teal");
        await form.AcceptCommand.ExecuteAsync(null);

        var post = api.Posts.Should().ContainSingle().Subject;
        post.Path.Should().Be(Root + "/blueprints");
        post.Body.Should().Be("""{"id":"BP-010","title":"Search works offline","goal":"No network needed","exit":["Works on a plane"],"notes":"","color":"teal"}""");
        model.Services.Toasts.Items.Should().Contain(t => t.Text == "Blueprint BP-010 created");
    });

    [Fact]
    public Task EditingABlueprintKeepsItsIdAndRemovingOneAsksAboutItsTasks() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("blueprints");
        var plan = Work(model).Plan;

        _ = plan.EditBlueprintCommand.ExecuteAsync(plan.Cards[0]);
        var form = await DialogAsync<BlueprintFormDialog>(model);
        form.AsksId.Should().BeFalse();
        form.Color.Name.Should().Be("yellow");
        form.Notes = "";
        await form.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Path.Should().Be(Root + "/blueprints/BP-009/edit");
        api.Posts[^1].Body.Should().Be("""{"title":"V13 Features and bugfixes","goal":"Ship v13","exit":["Released","Documented"],"notes":"","color":"yellow"}""");

        _ = plan.RemoveBlueprintCommand.ExecuteAsync(plan.Cards[0]);
        var remove = await DialogAsync<RemoveBlueprintDialog>(model);
        remove.Sub.Should().Be("It has 2 tasks. What happens to them?");
        remove.MoveTo = remove.Targets.Single(t => t.Value == "BP-008");
        await remove.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Path.Should().Be(Root + "/blueprints/BP-009/remove");
        api.Posts[^1].Body.Should().Be("""{"tasks":"move","move_to":"BP-008"}""");

        _ = plan.RemoveBlueprintCommand.ExecuteAsync(plan.Cards[0]);
        remove = await DialogAsync<RemoveBlueprintDialog>(model);
        remove.Delete = true;
        remove.Move.Should().BeFalse();
        await remove.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Body.Should().Be("""{"tasks":"delete"}""");
    });

    [Fact]
    public Task MovingABlueprintDownSavesTheWholeOrder() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("blueprints");
        var plan = Work(model).Plan;
        plan.CardChips.Single(c => c.Key == "all").On = true;

        await plan.MoveCardDownCommand.ExecuteAsync(plan.Cards[0]);

        api.Posts.Should().ContainSingle().Which.Should().Be((Root + "/blueprints-order", """{"order":["BP-008","BP-009"]}""", api.Posts[0].Origin, "1"));
        model.Services.Toasts.Items.Should().Contain(t => t.Text == "Blueprint order saved");
    });

    [Fact]
    public Task TasksShowTheOpenOnesByBlueprintAndFilterByStatusAndBlueprint() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await OpenAsync("tasks");
        var plan = Work(model).Plan;

        plan.StatusChips.Where(c => c.On).Select(c => c.Key).Should().Equal("ready", "claimed", "in_progress", "review", "blocked");
        plan.Groups.Select(g => g.Id).Should().Equal("BP-009");
        var rows = plan.Groups[0].Rows;
        rows.Select(r => (r.Id, r.Status, r.Acceptance, r.Editable)).Should().Equal(("TASK-0001", "ready", "0/1", true), ("TASK-0003", "claimed", "", false));
        rows[1].Claim.Should().StartWith("claude-code");
        plan.Groups[0].CountText.Should().Be("2 shown");
        plan.Targets.Select(t => t.Id).Should().Equal("BP-008", "—");
        plan.DoneText.Should().Be("1 of 3 tasks done");
        plan.Parts.Select(p => p.Text).Should().Equal("1 done", "1 in flight", "1 ready");

        plan.StatusChips.Single(c => c.Key == "done").On = true;
        plan.Groups.Select(g => g.Id).Should().Equal("BP-009", "BP-008");

        plan.SelectedBlueprint = plan.BlueprintChoices.Single(c => c.Value == "BP-008");
        plan.Groups.Select(g => g.Id).Should().Equal("BP-008");
        plan.Groups[0].Movable.Should().BeFalse("blueprints are ordered while all are shown");
    });

    [Fact]
    public Task StuckTasksAreListed() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/tasks"] = Fixtures.Tasks(("TASK-0009", "blocked", "Stuck"));
        var (_, _, model) = await OpenAsync("tasks", api);
        var plan = Work(model).Plan;

        plan.Stuck.Select(s => (s.Id, s.Why)).Should().Contain(("TASK-0009", "blocked: waits for the server"));
        plan.StuckNote.Should().EndWith("stuck");
    });

    [Fact]
    public Task TheTaskDialogShowsTheTaskItsSessionsAndItsAuditTrail() => Ui.RunAsync(async () =>
    {
        var (window, api, model) = await OpenAsync("tasks");

        _ = Work(model).Plan.OpenTaskCommand.ExecuteAsync("TASK-0001");
        var dialog = await DialogAsync<TaskDialog>(model);
        await dialog.Loaded;

        dialog.HasCancel.Should().BeFalse();
        dialog.Task!.Title.Should().Be("Ready one");
        dialog.CriteriaHead.Should().Be("Acceptance criteria · 0 of 1 met");
        dialog.Criteria.Select(c => (c.Mark, c.Text)).Should().Equal(("○", "It runs"));
        dialog.Facts.Select(f => f.Label).Should().Equal("Status", "Priority", "Type", "Blueprint", "Scope", "Validation");
        dialog.Editable.Should().BeTrue();
        dialog.SessionsNote.Should().StartWith("None recorded.");
        api.Gets.Should().Contain(Root + "/tasks/TASK-0001/sessions");

        MainView.RequestFind();
        var view = window.GetVisualDescendants().OfType<MainView>().Single();
        view.FindControl<TextBox>("FindInput")!.Text = "It runs";
        Ui.Settle();
        view.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Should().Contain(t => t.Text == "It runs" && t.Classes.Contains("find-current"));
        view.FindControl<Button>("FindClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await dialog.ShowTrailCommand.ExecuteAsync(null);
        dialog.Trail.Select(e => (e.Event, e.Move)).Should().Equal(("imported", ""), ("unlocked", "backlog → ready"));

        dialog.MoveTo = dialog.MoveChoices.Single(c => c.Value == "BP-008");
        await dialog.MoveCommand.ExecuteAsync(null);
        api.Posts.Should().ContainSingle().Which.Should().Be((Root + "/tasks/TASK-0001/blueprint", """{"blueprint":"BP-008"}""", api.Posts[0].Origin, "1"));
    });

    [Fact]
    public Task SessionsSayWhatTheyUsed() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/tasks/{task}/sessions"] = """
            {"sessions":[{"client":"claude-code","session_id":"0123456789abcdef","origin":"hooks","status":"working","started_at":"2026-11-13T09:00:00Z","ended_at":"2026-11-13T09:30:00Z","model":"claude-fable-5-2","input_tokens":12000,"output_tokens":3000,"cache_read_tokens":500000,"cache_write_tokens":0,"reasoning_tokens":0,"active_ms":600000,"cost_usd":1.5}],
             "totals":{"input_tokens":12000,"output_tokens":3000,"cache_read_tokens":500000,"cache_write_tokens":0,"reasoning_tokens":0,"active_ms":600000,"cost_usd":1.5}}
            """;
        var (_, _, model) = await OpenAsync("tasks", api);

        _ = Work(model).Plan.OpenTaskCommand.ExecuteAsync("TASK-0003");
        var dialog = await DialogAsync<TaskDialog>(model);
        await dialog.Loaded;

        var session = dialog.Sessions.Should().ContainSingle().Subject;
        (session.Client, session.ShortId, session.Status).Should().Be(("Claude Code", "01234567", "working"));
        session.Usage.Should().Be("claude-fable-5-2 · 515.0k tokens · in 12.0k · cache read 500.0k · cache write 0 · out 3000 · $1.50 API-equivalent · active 10 min");
        api.Answers[Root + "/tasks/TASK-0003/sessions/claude-code/0123456789abcdef/tail"] = """
            {"entries":[{"kind":"user","text":"Hello"},{"kind":"tool","tool":"Read","text":"file.cs"}],
             "resume":"claude --resume 0123456789abcdef"}
            """;
        await session.ShowTranscriptCommand.ExecuteAsync(null);
        session.Transcript.Should().Be("You: Hello\n\nRead: file.cs\n\nResume in a terminal: claude --resume 0123456789abcdef");
        dialog.Editable.Should().BeFalse("a claimed task's definition is fixed");
    });

    [Fact]
    public Task StartingAnAgentOffersModelsAndModes() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/tasks/{task}/sessions"] = """
            {"sessions":[],"totals":{},"hosts":[],"permission_modes":{"claude-code":[
              {"id":"auto","label":"Auto","description":"Automatic","default":true,"unsafe":false},
              {"id":"bypass","label":"Bypass","description":"Unrestricted","default":false,"unsafe":true}]}}
            """;
        api.Answers[Root + "/agent-models"] = """
            {"clients":{"claude-code":{"default":{"id":"sonnet","label":"Sonnet"},
             "models":[{"id":"opus","label":"Opus","source":"seen"}],"refreshing":false}}}
            """;
        var (_, _, model) = await OpenAsync("tasks", api);
        _ = Work(model).Plan.OpenTaskCommand.ExecuteAsync("TASK-0001");
        var dialog = await DialogAsync<TaskDialog>(model);
        await dialog.Loaded;
        var form = dialog.AgentSessions;
        form.FormOpen = true;
        await Ui.UntilAsync(() => form.Models.Any(m => m.Value == "opus"));
        form.Models.Select(m => m.Label).Should().Equal("Default (Sonnet)", "Opus (seen)", "Other…");
        form.Modes[1].Display.Should().Contain("unsafe");
        form.Mode = form.Modes[1];
        form.Model = form.Models[^1];
        form.OtherModel = "custom-model";
        form.Prompt = "Do it";
        await form.StartCommand.ExecuteAsync(null);
        api.UiGets.Should().Contain(Root + "/agent-models");
        api.Posts.Should().ContainSingle(p => p.Path == Root + "/tasks/TASK-0001/agents" &&
            p.Body.Contains("\"model\":\"custom-model\"", StringComparison.Ordinal) &&
            p.Body.Contains("\"mode\":\"bypass\"", StringComparison.Ordinal));
        dialog.Cancel();
    });

    [Fact]
    public Task AgentLogReplaysThenFollowsAndAcceptsCommands() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/tasks/{task}/sessions"] = """
            {"sessions":[],"totals":{},"hosts":[{"key":"host1234","client":"codex","status":"idle","alive":true,
              "requested_model":"unknown","model_unlisted":true}],"permission_modes":{}}
            """;
        api.Answers[Root + "/agents/host1234/log"] = """
            {"status":"idle","alive":true,"events":[{"seq":1,"event":"text","text":"first"}]}
            """;
        api.Delays[Root + "/agents/host1234/log"] = TimeSpan.FromMilliseconds(250);
        var (_, _, model) = await OpenAsync("tasks", api);
        _ = Work(model).Plan.OpenTaskCommand.ExecuteAsync("TASK-0001");
        var dialog = await DialogAsync<TaskDialog>(model);
        await dialog.Loaded;
        var panel = dialog.AgentSessions.Hosts.Should().ContainSingle().Subject;
        panel.Heading.Should().Contain("unknown").And.Contain("not in the model list");
        api.Agent("""{"project":"prj_fixture","key":"host1234","events":[{"seq":2,"event":"approval","id":"a1","tool":"Read","text":"file"},{"seq":3,"event":"text","text":"second"}]}""");
        await Ui.UntilAsync(() => panel.Entries.Count is 3);
        panel.Entries.Select(e => e.Text).Should().Equal("first", "Wants to use Read: file", "second");
        await panel.Entries[1].AllowCommand.ExecuteAsync(null);
        api.Agent("""{"project":"prj_fixture","key":"host1234","events":[{"seq":4,"event":"answered","id":"a1","allow":true}]}""");
        await Ui.UntilAsync(() => panel.Entries[1].Answer == "allowed");
        panel.Message = "Continue";
        await panel.SendCommand.ExecuteAsync(null);
        await panel.StopCommand.ExecuteAsync(null);
        api.Posts.Select(p => p.Path).Should().ContainInOrder(
            Root + "/agents/host1234/approve", Root + "/agents/host1234/send", Root + "/agents/host1234/stop");
        api.Answers[Root + "/agents/host1234/log"] = """
            {"status":"idle","alive":true,"events":[{"seq":5,"event":"text","text":"after reconnect"}]}
            """;
        api.Drop();
        await Ui.UntilAsync(() => panel.Entries.Any(e => e.Text == "after reconnect"));
        api.GetsWithQuery.Should().Contain(Root + "/agents/host1234/log?after=4");
        dialog.Cancel();
    });

    [Fact]
    public Task AgentSessionFormPassesTheUiChecks() => Ui.RunAsync(() => UiChecks.AssertAsync(async (width, height) =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/agent-models"] = """{"clients":{}}""";
        api.Answers[Root + "/tasks/{task}/sessions"] = """
            {"sessions":[{"client":"claude-code","session_id":"6f1e2d3c-0000-4000-8000-000000000001",
             "origin":"hooks","status":"working","started_at":"2026-11-13T09:00:00Z","ended_at":"2026-11-13T09:30:00Z"}],
             "totals":{},"hosts":[{"key":"host1234","client":"codex","status":"idle","alive":true}],
             "permission_modes":{"claude-code":[{"id":"auto","label":"Auto","description":"Automatic","default":true}]}}
            """;
        api.Answers[Root + "/agents/host1234/log"] = """
            {"status":"idle","alive":true,"events":[{"seq":1,"event":"approval","id":"a","tool":"Read","text":"file"}]}
            """;
        var (window, _, model) = await OpenAsync("tasks", api, width, height);
        _ = Work(model).Plan.OpenTaskCommand.ExecuteAsync("TASK-0001");
        var dialog = await DialogAsync<TaskDialog>(model);
        await dialog.Loaded;
        dialog.AgentSessions.FormOpen = true;
        await Ui.UntilAsync(() => dialog.AgentSessions.Hosts[0].Entries.Count is 1);
        Ui.Settle();
        return window;
    }));

    [Fact]
    public Task AgentLogPollsWhenTheStreamIsOffline() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        var path = Root + "/agents/host1234/log";
        api.Answers[path] = """{"status":"idle","alive":true,"events":[]}""";
        using var http = new HttpClient(api) { BaseAddress = Ui.Server };
        using var services = Ui.Services(http, fastLive: true);
        using var panel = new AgentPanel(services, "prj_fixture", "TASK-0001", new AgentHost
        {
            Key = "host1234", Client = "codex", Status = "idle", Alive = true,
        }, () => Task.CompletedTask);
        await Ui.UntilAsync(() => api.Gets.Contains(path));
        api.Answers[path] = """
            {"status":"idle","alive":true,"events":[{"seq":1,"event":"text","text":"polled"}]}
            """;
        await Ui.UntilAsync(() => panel.Entries.Count is 1);
        panel.Entries[0].Text.Should().Be("polled");
        api.Gets.Count(p => p == path).Should().BeGreaterThanOrEqualTo(2);
    });

    [Fact]
    public Task ANewTaskIsCheckedThenSentWithEveryField() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("tasks");

        _ = Work(model).Plan.NewTaskCommand.ExecuteAsync("BP-009");
        var form = await DialogAsync<TaskFormDialog>(model);
        await Ui.UntilAsync(() => form.IdHint == "TASK-0005");
        form.Blueprint.Value.Should().Be("BP-009");
        await form.AcceptCommand.ExecuteAsync(null);
        form.Error.Should().StartWith("Title");
        form.TaskTitle = "Add offline search";
        form.Goal = "Search without a network";
        form.TaskType = "bug";
        form.Priority = "high";
        form.Criteria[0].Text = "Works offline";
        form.AddCriterionCommand.Execute(null);
        form.Criteria[1].Text = "Documented";
        form.ToggleDependencyCommand.Execute("TASK-0001");
        form.DependsQuery = "other:TASK-0007";
        form.AddTypedDependencyCommand.Execute(null);
        form.ScopeText = "src/search, doc";
        form.AddScopeCommand.Execute(null);
        form.Options.Select(o => o.Id).Should().Equal("TASK-0001", "TASK-0003");
        await form.AcceptCommand.ExecuteAsync(null);

        var post = api.Posts.Should().ContainSingle().Subject;
        post.Path.Should().Be(Root + "/tasks");
        post.Body.Should().Be("""{"title":"Add offline search","goal":"Search without a network","type":"bug","priority":"high","blueprint":"BP-009","depends_on":["TASK-0001","other:TASK-0007"],"acceptance":["Works offline","Documented"],"scope":["src/search","doc"]}""");
    });

    [Fact]
    public Task EditingATaskKeepsItsIdAndDeletingOneAsksFirst() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("tasks");
        var plan = Work(model).Plan;

        await plan.EditTaskCommand.ExecuteAsync("TASK-0003");
        model.Services.Dialogs.IsOpen.Should().BeFalse("a claimed task cannot be edited");

        _ = plan.EditTaskCommand.ExecuteAsync("TASK-0001");
        var form = await DialogAsync<TaskFormDialog>(model);
        form.IdEditable.Should().BeFalse();
        form.Criteria.Select(c => c.Text).Should().Equal("It runs");
        form.TaskTitle = "Ready one, restated";
        await form.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Path.Should().Be(Root + "/tasks/TASK-0001/edit");
        api.Posts[^1].Body.Should().Be("""{"title":"Ready one, restated","goal":"Try it out","type":"spike","priority":"high","blueprint":"BP-009","depends_on":[],"acceptance":["It runs"],"scope":["src"]}""");
        model.Services.Toasts.Items.Should().Contain(t => t.Text == "TASK-0001 saved");

        var deleting = plan.DeleteTaskCommand.ExecuteAsync("TASK-0001");
        var confirm = await DialogAsync<ConfirmDialog>(model);
        confirm.Danger.Should().BeTrue();
        await confirm.AcceptCommand.ExecuteAsync(null);
        await deleting;
        api.Posts[^1].Should().Be((Root + "/tasks/TASK-0001/delete", "{}", api.Posts[^1].Origin, "1"));
    });

    [Fact]
    public Task TheInboxListsEachKindAndApprovesOrRejectsIt() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("inbox");
        var inbox = Work(model).Inbox;

        inbox.TaskCards.Single().Title.Should().Be("Proposed task");
        inbox.TaskCards[0].Provenance.Should().StartWith("depends on TASK-0001 · scope src · proposed by agent:test");
        inbox.BlueprintCards.Single().Head.Should().Be("New blueprint");
        inbox.BlueprintCards[0].List.Should().Equal("Done when shipped");
        inbox.ArchCards.Single().Head.Should().Be("Task from the agent's observation");
        inbox.ArchCards[0].Tags.Should().Equal("the agent's reading");

        _ = inbox.ApproveCommand.ExecuteAsync(inbox.TaskCards[0]);
        var task = await DialogAsync<TaskFormDialog>(model);
        task.TaskTitle.Should().Be("Proposed task");
        task.Depends.Should().Equal("TASK-0001");
        await task.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Path.Should().Be(Root + "/task-proposals/tpr_d83fd9b21428d3a1/approve");
        api.Posts[^1].Body.Should().Be("""{"task":{"title":"Proposed task","goal":"Its goal","type":"feature","priority":"medium","blueprint":"BP-009","depends_on":["TASK-0001"],"acceptance":["Checked"],"scope":["src"]}}""");

        _ = inbox.ApproveCommand.ExecuteAsync(inbox.BlueprintCards[0]);
        var blueprint = await DialogAsync<BlueprintFormDialog>(model);
        blueprint.AsksId.Should().BeFalse();
        await blueprint.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Path.Should().Be(Root + "/blueprint-proposals/bpr_d5e2ea05446c7188/approve");
        api.Posts[^1].Body.Should().Be("""{"edit":{"title":"Proposed blueprint","goal":"Its goal","exit":["Done when shipped"],"notes":"a note"}}""");

        _ = inbox.ApproveCommand.ExecuteAsync(inbox.ArchCards[0]);
        var arch = await DialogAsync<ArchApproveDialog>(model);
        arch.IsTask.Should().BeTrue();
        await arch.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Should().Be((Root + "/arch-proposals/apr_94991f3b07840fe6/approve", """{"title":"Split the service","body":"It does two things"}""", api.Posts[^1].Origin, "1"));

        _ = inbox.RejectCommand.ExecuteAsync(inbox.ArchCards[0]);
        var reject = await DialogAsync<RejectDialog>(model);
        reject.Note = "not now";
        await reject.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Should().Be((Root + "/arch-proposals/apr_94991f3b07840fe6/reject", """{"note":"not now"}""", api.Posts[^1].Origin, "1"));
    });

    [Fact]
    public Task TheInboxFiltersByStatus() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("inbox");
        var inbox = Work(model).Inbox;
        api.Answers[Root + "/task-proposals"] = """{"proposals":[]}""";
        api.Answers[Root + "/blueprint-proposals"] = """{"proposals":[]}""";
        api.Answers[Root + "/arch-proposals"] = """{"proposals":[]}""";

        inbox.StatusChips.Single(c => c.Key == "rejected").On = true;

        await Ui.UntilAsync(() => inbox.Empty is not null);
        inbox.Empty.Should().Be("No rejected proposals.");
        inbox.StatusChips.Where(c => c.On).Select(c => c.Key).Should().Equal("rejected");
    });

    [Fact]
    public Task ATasksAddressOpensItsDialog() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, address: "#/p/prj_fixture/work/TASK-0001");

        var dialog = await DialogAsync<TaskDialog>(model);
        Work(model).Section.Should().Be("tasks");
        dialog.Title.Should().Be("TASK-0001 · Ready one");
        dialog.Sub.Should().Be("In blueprint BP-009");
    });

    [Fact]
    public Task TheBoardsCardIdOpensTheSameTaskDialog() => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await Ui.OpenMainAsync(1280, 800);

        var open = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "OpenTask" && (string?)b.Content == "TASK-0001");
        open.Command!.Execute(open.CommandParameter);

        var dialog = await DialogAsync<TaskDialog>(model);
        await dialog.Loaded;
        dialog.Task!.Id.Should().Be("TASK-0001");
    });

    [Fact]
    public Task DraggingATaskOntoAnotherBlueprintMovesIt() => Ui.RunAsync(async () =>
    {
        var (window, api, model) = await OpenAsync("tasks");
        var view = window.GetVisualDescendants().OfType<WorkView>().Single();
        var row = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("drag-task") && b.DataContext is TaskRow { Id: "TASK-0001" });
        var from = Center(row, window);

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(0, 20));
        Ui.Settle();
        var target = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("drop-blueprint") && b.DataContext is TaskGroup { Id: "BP-008" } && b.IsEffectivelyVisible);
        var to = Center(target, window);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);

        await Ui.UntilAsync(() => api.Posts.Count > 0);
        api.Posts.Should().ContainSingle().Which.Should().Be((Root + "/tasks/TASK-0001/blueprint", """{"blueprint":"BP-008"}""", api.Posts[0].Origin, "1"));
        model.Services.Dialogs.IsOpen.Should().BeFalse("a drop is not a click");
    });

    [Fact]
    public Task DraggingABlueprintOntoAnotherReordersThem() => Ui.RunAsync(async () =>
    {
        var (window, api, model) = await OpenAsync("blueprints", height: 1800);
        Work(model).Plan.CardChips.Single(c => c.Key == "all").On = true;
        Ui.Settle();
        var cards = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("bpcard")).ToArray();
        var bp8 = cards.Single(c => ((BlueprintCard)c.DataContext!).Id == "BP-008");
        var bp9 = cards.Single(c => ((BlueprintCard)c.DataContext!).Id == "BP-009");
        var from = Center(bp8, window) + new Point(0, -bp8.Bounds.Height / 4);
        // the upper half of BP-009: before it
        var to = bp9.TranslatePoint(new Point(bp9.Bounds.Width / 2, bp9.Bounds.Height / 4), window)!.Value;

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(-20, 0));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);

        await Ui.UntilAsync(() => api.Posts.Count > 0);
        api.Posts.Should().ContainSingle().Which.Body.Should().Be("""{"order":["BP-008","BP-009"]}""");
    });

    [Theory]
    [InlineData(390)]
    [InlineData(1280)]
    public Task BlueprintCardsFillOneColumn(int width) => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await OpenAsync("blueprints", width: width, height: 1800);
        Work(model).Plan.CardChips.Single(c => c.Key == "all").On = true;
        Ui.Settle();
        var view = window.GetVisualDescendants().OfType<WorkView>().Single();
        var list = view.FindControl<ItemsControl>("Cards")!;
        var cards = list.GetVisualDescendants().OfType<Border>().Where(card => card.Classes.Contains("bpcard")).ToArray();
        cards.Should().HaveCountGreaterThan(1);
        foreach (var card in cards)
        {
            card.Bounds.Width.Should().BeApproximately(list.Bounds.Width, 1);
        }
        var positions = cards.Select(card => card.TranslatePoint(new Point(), list)!.Value).ToArray();
        positions[1].X.Should().BeApproximately(positions[0].X, 1);
        positions[1].Y.Should().BeGreaterThanOrEqualTo(positions[0].Y + cards[0].Bounds.Height);
    });

    [Theory]
    [InlineData("blueprints")]
    [InlineData("tasks")]
    [InlineData("inbox")]
    public Task WorkPassesTheUiChecks(string section) =>
        Ui.RunAsync(() =>
            UiChecks.AssertAsync(async (width, height) =>
            {
                var api = new FixtureApi();
                api.Answers[Root + "/tasks"] = Fixtures.Tasks(("TASK-0009", "blocked", "A blocked task whose title is long enough to wrap on a phone"));
                return (await OpenAsync(section, api, width, height)).Window;
            })
        );

    [Theory]
    [InlineData("task")]
    [InlineData("form")]
    [InlineData("blueprint")]
    public Task WorkDialogsPassTheUiChecks(string which) =>
        Ui.RunAsync(() =>
            UiChecks.AssertAsync(async (width, height) =>
            {
                var (window, _, model) = await OpenAsync("tasks", null, width, height);
                var plan = Work(model).Plan;
                switch (which)
                {
                    case "task":
                        _ = plan.OpenTaskCommand.ExecuteAsync("TASK-0001");
                        await (await DialogAsync<TaskDialog>(model)).Loaded;
                        break;
                    case "form":
                        _ = plan.NewTaskCommand.ExecuteAsync(null);
                        await DialogAsync<TaskFormDialog>(model);
                        break;
                    default:
                        _ = plan.NewBlueprintCommand.ExecuteAsync(null);
                        await DialogAsync<BlueprintFormDialog>(model);
                        break;
                }
                Ui.Settle();
                return window;
            })
        );

    private static async Task<(Window Window, FixtureApi Api, MainViewModel Model)> OpenAsync(
        string section,
        FixtureApi? api = null,
        int width = 1280,
        int height = 800
    )
    {
        var opened = await Ui.OpenMainAsync(width, height, api: api, address: $"#/p/prj_fixture/work/{section}");
        await Ui.UntilAsync(() =>
            opened.Model.Page is ProjectPage { Body: WorkViewModel { Plan.HasContent: true, Inbox.HasContent: true } }
        );
        Ui.Settle();
        return opened;
    }

    private static WorkViewModel Work(MainViewModel model) => (WorkViewModel)((ProjectPage)model.Page!).Body!;

    private static async Task<T> DialogAsync<T>(MainViewModel model)
        where T : DialogViewModel
    {
        await Ui.UntilAsync(() => model.Services.Dialogs.Current is T);
        Ui.Settle();
        return (T)model.Services.Dialogs.Current!;
    }

    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
}
