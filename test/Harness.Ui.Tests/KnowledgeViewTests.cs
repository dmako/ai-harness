using Avalonia.Controls;
using AwesomeAssertions;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The Avalonia Knowledge against the fixture API: a project's memories (types, scopes, search, what applies, add,
/// correct, retire, promote, share, history), its Inbox (removed memories, proposals approved, edited, decided or
/// rejected), the level view for the user and a workspace, and the UI checks. Each action's request is checked.
/// </summary>
public sealed class KnowledgeViewTests
{
    private const string Root = "/api/projects/prj_fixture";
    private const string Decision = "mem_d6ff7923add57da5";
    private const string Convention = "mem_e901a7447d83a99c";
    private const string Proposal = "prop_489c48357aaf7443";

    [Fact]
    public Task MemoriesAreGroupedByTypeAndFilteredByTypeAndScope() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("knowledge");
        var memories = Knowledge(model).Memories;

        memories.Groups.Select(g => (g.Type, g.Count)).Should().Equal(("decision", 2), ("convention", 2), ("warning", 2));
        memories.Summary.Should().Be("6 memories");
        memories.Scopes.Select(s => s.Value).Should().Equal("", "project", "path:src/Harness.Ui", "user", "workspace:main");
        api.GetsWithQuery.Should().Contain(g => g.StartsWith(Root + "/memories?type=goal,constraint,decision", StringComparison.Ordinal) && g.Contains("levels=1", StringComparison.Ordinal));
        var decision = memories.Groups[0].Cards[0];
        decision.Lead.Should().Be("Live updates use Server-Sent Events:");
        decision.Rest.Should().Be("the page only needs to know what changed.");
        decision.IsShared.Should().BeTrue();
        decision.Provenance.Should().StartWith("stated by you · design review");
        decision.CanPromote.Should().BeTrue();
        memories.Groups[0].Cards[1].IsLevel.Should().BeTrue("the workspace's decision is read and changed at its level");
        memories.Groups[0].Cards[1].CanPromote.Should().BeFalse();
        memories.Groups[2].Cards.Single(c => c.Memory.Content == "The old warning").Inactive.Should().BeTrue();

        memories.Types.Single(t => t.Type == "warning").On = false;
        memories.Groups.Select(g => g.Type).Should().Equal("decision", "convention");
        memories.Summary.Should().Be("4 memories");

        memories.Scope = memories.Scopes.Single(s => s.Value == "project");
        memories.Summary.Should().Be("1 memory in project");
    });

    [Fact]
    public Task SearchingAndSupersededOnesAskTheServerAgain() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("knowledge");
        var memories = Knowledge(model).Memories;

        memories.Query = "events";
        await Ui.UntilAsync(() => api.GetsWithQuery.Any(g => g.Contains("q=events", StringComparison.Ordinal)));
        memories.IncludeInactive = true;
        await Ui.UntilAsync(() => api.GetsWithQuery.Any(g => g.Contains("inactive=1", StringComparison.Ordinal)));
        api.GetsWithQuery.Should().Contain(g => g.Contains("q=events", StringComparison.Ordinal) && g.Contains("inactive=1", StringComparison.Ordinal));
    });

    [Fact]
    public Task WhatAppliesHereShowsWhatAnAgentIsTold() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("knowledge/src/ui");
        var applies = Knowledge(model).Memories.Applies;

        await Ui.UntilAsync(() => applies.HasResult);
        api.GetsWithQuery.Should().Contain(Root + "/applies?scope=path%3Asrc%2Fui");
        applies.Heading.Should().Be("What an agent is told about project on branch main:");
        applies.Groups.Select(g => (g.Type, g.Count)).Should().Equal(("goal", 0), ("constraint", 0), ("decision", 2));

        applies.ClearCommand.Execute(null);
        applies.HasResult.Should().BeFalse();
    });

    [Fact]
    public Task AddingKnowledgeSendsItsTypeScopeAndSource() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("knowledge");
        api.Reply = (_, body) => body.Contains("similar", StringComparison.Ordinal)
            ? null
            : """{"status":"needs_decision","similar":[{"id":"mem_x","type":"decision","content":"Something close","scope":"project","source_type":"user","created_at":"2026-11-13T09:00:00Z"}]}""";

        _ = Knowledge(model).Memories.AddCommand.ExecuteAsync(null);
        var add = await DialogAsync<MemoryAddDialog>(model);
        add.Type.Type.Should().Be("decision");
        await add.AcceptCommand.ExecuteAsync(null);
        add.Error.Should().Be("Statement is required.");
        add.Content = "The UI is Avalonia";
        add.Scope = "path:src/Harness.Ui";
        add.SourceRef = "TASK-0144";
        await add.AcceptCommand.ExecuteAsync(null);

        api.Posts.Should().ContainSingle().Which.Should().Be(
            (Root + "/memories", """{"type":"decision","content":"The UI is Avalonia","scope":"path:src/Harness.Ui","source_ref":"TASK-0144"}""", api.Posts[0].Origin, "1")
        );
        add.Error.Should().Be("Something similar is already recorded: Something close (correct that one instead)");
        model.Services.Dialogs.Current.Should().BeSameAs(add, "a refused add keeps the dialog open");
    });

    [Fact]
    public Task CorrectingAndRetiringAMemoryAskWhy() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("knowledge");
        var card = Card(model, Decision);

        _ = card.CorrectCommand.ExecuteAsync(null);
        var correct = await DialogAsync<MemoryChangeDialog>(model);
        correct.Content.Should().StartWith("Live updates use");
        await correct.AcceptCommand.ExecuteAsync(null);
        correct.Error.Should().Be("Why is required.");
        correct.Content = "Live updates use Server-Sent Events.";
        correct.Reason = "shorter";
        await correct.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Path.Should().Be($"{Root}/memories/{Decision}/supersede");
        api.Posts[^1].Body.Should().Be("""{"content":"Live updates use Server-Sent Events.","reason":"shorter"}""");

        _ = Card(model, Convention).RetireCommand.ExecuteAsync(null);
        var retire = await DialogAsync<MemoryChangeDialog>(model);
        retire.Danger.Should().BeTrue();
        retire.Reason = "no longer applies";
        await retire.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Should().Be(($"{Root}/memories/{Convention}/retire", """{"reason":"no longer applies"}""", api.Posts[^1].Origin, "1"));
    });

    [Fact]
    public Task ALevelsMemoryIsChangedAtItsLevel() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("knowledge");
        var workspace = Knowledge(model).Memories.Groups[0].Cards.Single(c => c.IsLevel);

        _ = workspace.RetireCommand.ExecuteAsync(null);
        var retire = await DialogAsync<MemoryChangeDialog>(model);
        retire.Reason = "done";
        await retire.AcceptCommand.ExecuteAsync(null);

        api.Posts[^1].Should().Be(
            ("/api/knowledge/memories/mem_08a5c41e0b5dff0b/retire", """{"reason":"done","level":"workspace:main"}""", api.Posts[^1].Origin, "1")
        );
    });

    [Fact]
    public Task PromotingShowsWhereItHoldsThenRecordsIt() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("knowledge");
        api.Reply = (_, body) => body.Contains("dry_run", StringComparison.Ordinal)
            ? """{"status":"would_create","level":"user","holds_in":[{"project":"other","memory":{"id":"mem_o","type":"decision","content":"same","scope":"project","source_type":"user","created_at":"2026-11-13T09:00:00Z"}}],"retired":false}"""
            : """{"status":"recorded","level":"user","holds_in":[],"retired":true}""";

        _ = Card(model, Decision).PromoteCommand.ExecuteAsync(null);
        var promote = await DialogAsync<PromoteDialog>(model);
        promote.Targets.Select(t => t.Value).Should().Equal("workspace:main", "user");
        promote.To = promote.Targets[1];
        await promote.AcceptCommand.ExecuteAsync(null);
        promote.Reviewing.Should().BeTrue();
        promote.ReviewText.Should().Contain("Promote to all your projects?").And.Contain("Already holds in: other");
        await promote.AcceptCommand.ExecuteAsync(null);

        api.Posts.Select(p => (p.Path, p.Body))
            .Should()
            .Equal(($"{Root}/memories/{Decision}/promote", """{"to":"user","dry_run":true}"""), ($"{Root}/memories/{Decision}/promote", """{"to":"user"}"""));
        model.Services.Toasts.Items.Should().Contain(t => t.Text == "Promoted to all your projects");
    });

    [Fact]
    public Task SharingIsConfirmedAndAWarningIsShown() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("knowledge");
        api.Reply = (_, _) => """{"memory":{"id":"mem_e901a7447d83a99c","type":"convention","content":"x","scope":"path:src/Harness.Ui","source_type":"agent","created_at":"2026-11-13T09:00:00Z"},"shared":"project","warning":"Git ignores .harness/knowledge.toml","fix":"git add -f .harness/knowledge.toml"}""";
        var shared = Card(model, Decision);
        shared.ShareLabel.Should().Be("Withdraw from project");

        var sharing = Card(model, Convention).ShareCommand.ExecuteAsync(null);
        var confirm = await DialogAsync<ConfirmDialog>(model);
        confirm.AcceptLabel.Should().Be("Share");
        await confirm.AcceptCommand.ExecuteAsync(null);
        await sharing;

        api.Posts.Should().ContainSingle().Which.Should().Be(($"{Root}/memories/{Convention}/share", "{}", api.Posts[0].Origin, "1"));
        model.Services.Toasts.Items.Should().Contain(t => t.Bad && t.Text == "Shared here, but Git ignores the file");
    });

    [Fact]
    public Task HistoryShowsTheVersionsAndTheTrail() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/memories/{mid}"] = """
            {"memory":{"id":"mem_new","type":"warning","content":"New","scope":"project","source_type":"user","created_at":"2026-11-13T09:00:00Z"},"current":true,
             "history":[{"id":"mem_old","type":"warning","content":"Old","scope":"project","source_type":"agent","created_at":"2026-11-12T09:00:00Z"},{"id":"mem_new","type":"warning","content":"New","scope":"project","source_type":"user","created_at":"2026-11-13T09:00:00Z"}],
             "events":[{"event":"superseded","actor":"human:dev","at":"2026-11-13T09:00:00Z","related_id":"mem_old","detail":"clearer"}]}
            """;
        var (_, _, model) = await OpenAsync("knowledge", api);
        var card = Card(model, "mem_768c3fe4929680e9");

        await card.HistoryCommand.ExecuteAsync(null);

        card.HistoryShown.Should().BeTrue();
        card.Versions.Select(v => (v.Text, v.This)).Should().Equal(("Old", false), ("New", true));
        card.Trail.Single().Code.Should().Be("mem_old");
        card.Trail.Single().Detail.Should().Be("clearer");
    });

    [Fact]
    public Task TheInboxApprovesAndAsksWhenAProposalResemblesAMemory() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("knowledge/inbox");
        var inbox = Knowledge(model).Inbox;
        Knowledge(model).ShowsInbox.Should().BeTrue();
        var calls = 0;
        api.Reply = (path, body) => path.EndsWith("/approve", StringComparison.Ordinal) && calls++ == 0
            ? $$$"""{"status":"needs_decision","proposal":{},"similar":[{"memory":{"id":"{{{Decision}}}","type":"decision","content":"Live updates use Server-Sent Events","scope":"project","source_type":"user","created_at":"2026-11-13T09:00:00Z"},"similarity":0.81}]}"""
            : """{"status":"approved"}""";

        var item = inbox.Proposals.Single();
        item.Provenance.Should().Be("project · source src/LiveUpdates.cs · proposed by agent:test · " + Shell.Ago("2026-11-13T09:00:00Z"));
        var approving = inbox.ApproveCommand.ExecuteAsync(item);
        var decide = await DialogAsync<SimilarDecisionDialog>(model);
        decide.Similar.Single().Similarity.Should().Be(0.81);
        decide.ReplaceCommand.Execute(decide.Similar[0]);
        await approving;

        api.Posts.Select(p => (p.Path, p.Body))
            .Should()
            .Equal(($"{Root}/proposals/{Proposal}/approve", "{}"), ($"{Root}/proposals/{Proposal}/approve", $$$"""{"supersedes":["{{{Decision}}}"]}"""));
        model.Services.Toasts.Items.Should().Contain(t => t.Text == "Approved");
    });

    [Fact]
    public Task AProposalIsEditedBeforeApprovalOrRejectedWithANote() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync("knowledge/inbox");
        var inbox = Knowledge(model).Inbox;
        api.Reply = (_, _) => """{"status":"approved"}""";

        _ = inbox.EditAndApproveCommand.ExecuteAsync(inbox.Proposals[0]);
        var edit = await DialogAsync<ProposalEditDialog>(model);
        edit.Content = "Live updates use Server-Sent Events.";
        await edit.AcceptCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => api.Posts.Count > 0);
        api.Posts[^1].Body.Should().Be("""{"content":"Live updates use Server-Sent Events."}""");

        _ = inbox.RejectCommand.ExecuteAsync(inbox.Proposals[0]);
        var reject = await DialogAsync<MemoryRejectDialog>(model);
        reject.Note = "not a decision";
        await reject.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Should().Be(($"{Root}/proposals/{Proposal}/reject", """{"note":"not a decision"}""", api.Posts[^1].Origin, "1"));
    });

    [Fact]
    public Task AMemoryAGitChangeRemovedIsKeptOrRetired() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/memories?shared=removed"] =
            $$$"""{"memories":[{"id":"{{{Convention}}}","type":"convention","content":"Views use compiled bindings.","scope":"project","source_type":"agent","created_at":"2026-11-13T09:00:00Z","shared":"removed"}]}""";
        var (_, _, model) = await OpenAsync("knowledge/inbox", api);
        var inbox = Knowledge(model).Inbox;

        inbox.HasRemoved.Should().BeTrue();
        await inbox.KeepCommand.ExecuteAsync(inbox.Removed[0]);
        await inbox.RetireRemovedCommand.ExecuteAsync(inbox.Removed[0]);

        api.Posts.Select(p => (p.Path, p.Body))
            .Should()
            .Equal(($"{Root}/memories/{Convention}/keep", "{}"), ($"{Root}/memories/{Convention}/retire", """{"reason":"removed from project knowledge"}"""));
    });

    [Fact]
    public Task YourKnowledgeIsTheUsersLevelWithAdd() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await Ui.OpenMainAsync(1280, 800, address: "#/knowledge");
        var page = model.Page.Should().BeOfType<UserKnowledgePage>().Subject;
        await Ui.UntilAsync(() => page.Knowledge.HasContent);

        api.GetsWithQuery.Should().Contain("/api/knowledge?level=user");
        var card = page.Knowledge.Groups.Single().Cards.Single();
        card.ShowsScope.Should().BeFalse("its page says the level");
        card.CanPromote.Should().BeFalse();

        api.Reply = (_, _) => """{"status":"created"}""";
        _ = page.Knowledge.AddCommand.ExecuteAsync(null);
        var add = await DialogAsync<MemoryAddDialog>(model);
        add.AsksScope.Should().BeFalse();
        add.Type.Type.Should().Be("convention");
        add.Content = "Answer in English";
        await add.AcceptCommand.ExecuteAsync(null);
        api.Posts.Should().ContainSingle().Which.Should().Be(
            ("/api/knowledge/memories", """{"type":"convention","content":"Answer in English","level":"user"}""", api.Posts[0].Origin, "1")
        );
    });

    [Fact]
    public Task AWorkspacesKnowledgeIsTheSameLevelView() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/knowledge"] = """{"level":"workspace:main","memories":[]}""";
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, address: "#/w/main/knowledge");
        var page = model.Page.Should().BeOfType<WorkspacePage>().Subject;
        var level = page.Body.Should().BeOfType<LevelKnowledgeViewModel>().Subject;
        await Ui.UntilAsync(() => level.HasContent);

        api.GetsWithQuery.Should().Contain("/api/knowledge?level=workspace%3Amain");
        level.IsEmpty.Should().BeTrue();
        level.TypeHelp.Should().HaveCount(7);
    });

    [Theory]
    [InlineData("#/p/prj_fixture/knowledge")]
    [InlineData("#/p/prj_fixture/knowledge/inbox")]
    [InlineData("#/knowledge")]
    [InlineData("#/w/main/knowledge")]
    public Task KnowledgePassesTheUiChecks(string address) =>
        Ui.RunAsync(() =>
            UiChecks.AssertAsync(async (width, height) =>
            {
                var api = Api();
                var (window, _, _) = await Ui.OpenMainAsync(width, height, api: api, address: address);
                await Ui.UntilAsync(() => api.Gets.Any(g => g.Contains("knowledge", StringComparison.Ordinal) || g.EndsWith("/memories", StringComparison.Ordinal)));
                await Task.Delay(100);
                Ui.Settle();
                return window;
            })
        );

    /// <summary>The fixture API with no memory removed from project knowledge (the removed list's own query).</summary>
    private static FixtureApi Api(FixtureApi? api = null)
    {
        api ??= new FixtureApi();
        api.Answers.TryAdd(Root + "/memories?shared=removed", """{"memories":[]}""");
        return api;
    }

    private static async Task<(Window Window, FixtureApi Api, MainViewModel Model)> OpenAsync(string view, FixtureApi? api = null)
    {
        api = Api(api);
        var opened = await Ui.OpenMainAsync(1280, 800, api: api, address: $"#/p/prj_fixture/{view}");
        await Ui.UntilAsync(() =>
            opened.Model.Page is ProjectPage { Body: KnowledgeViewModel { Memories.HasContent: true, Inbox.HasContent: true } }
        );
        Ui.Settle();
        return opened;
    }

    private static KnowledgeViewModel Knowledge(MainViewModel model) => (KnowledgeViewModel)((ProjectPage)model.Page!).Body!;

    private static MemoryCard Card(MainViewModel model, string id) =>
        Knowledge(model).Memories.Groups.SelectMany(g => g.Cards).Single(c => c.Memory.Id == id);

    private static async Task<T> DialogAsync<T>(MainViewModel model)
        where T : DialogViewModel
    {
        await Ui.UntilAsync(() => model.Services.Dialogs.Current is T);
        Ui.Settle();
        return (T)model.Services.Dialogs.Current!;
    }
}
