using Avalonia.Controls;
using AwesomeAssertions;
using Harness.Ui.ViewModels;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The Avalonia Agents against the fixture API: How harness serves them (verdict, tiles, what to change, resolved),
/// Tools &amp; providers (providers used and not, a provider's check and index, architecture evidence), the SCM card
/// (connection choice, test) and hooks, with each write's request and the UI checks.
/// </summary>
public sealed class AgentsViewTests
{
    private const string Root = "/api/projects/prj_fixture";

    private const string Report = """
        {"days":30,"context":{"calls":40,"changed":20,"hits":13,"hit_rate":0.65,"after_read":{"touched":10,"hits":7,"hit_rate":0.7}},
         "metrics":{"tools":[{"tool":"get_context","calls":30,"clients":["claude-code"],"last_at":"2026-11-13T09:00:00Z"},{"tool":"search_project","calls":12,"clients":["codex"],"last_at":"2026-11-12T09:00:00Z"}],"hooks":{"events":5,"sessions":2}},
         "knowledge":{"current_memories":9},"knowledge_reads":4,"knowledge_writes":2,
         "findings":[
           {"level":"warn","audience":"user","area":"providers","message":"graphify answered empty for 3 of 5 calls","action":"harness sync","trend":{"days":[{"day":"2026-11-12","calls":3,"errors":1,"slow":0},{"day":"2026-11-13","calls":2,"errors":0,"slow":0}]}},
           {"level":"info","audience":"user","area":"hooks","message":"history shown","history":"line one"},
           {"level":"warn","audience":"user","area":"providers","message":"ripwire failed: resolved"}]}
        """;

    private const string Providers = """
        {"languages":["csharp"],
         "providers":[
           {"name":"graphify-csharp","type":"harness","command":"graphify-csharp serve","enabled":true,"pack":"graphify-csharp",
            "capabilities":[{"capability":"code.callers","declared":true,"metrics":{"calls":10,"errors":1,"error_rate":0.1}},{"capability":"code.impact","declared":true,"tool":"impact"},{"capability":"project.overview","declared":true}],
            "check":{"name":"nuget-config","level":"warn","detail":"the company feed is not configured","fix":"harness doctor --fix nuget-config","fixable":true}},
           {"name":"pyright","type":"mcp","command":"pyright","enabled":true,"inactive":"not used here: serves python","languages":["python"]},
           {"name":"old","type":"mcp","command":"old","enabled":false,"inactive":"disabled"}],
         "architecture":[{"capability":"arch.dependencies","providers":[],"without":"harness reads dependencies itself; nothing is missing"},{"capability":"arch.change_coupling","providers":[],"without":"co-change partners need it"},{"capability":"arch.duplicates","providers":["codebase-memory"]}]}
        """;

    private const string Scm = """
        {"configured":true,"repository":true,
         "scm":{"status":"matched","source":"choice","name":"work","type":"gitlab","url":"https://gitlab.example.com","project":"team/fixture","remote":"origin"},
         "chosen":"chosen on this machine","choice":"work","config_name":null,
         "candidates":[{"name":"work","type":"gitlab","url":"https://gitlab.example.com","token":"****oken"},{"name":"mirror","type":"gitlab","url":"https://mirror.example.com","token":"****abcd"}],
         "last_test":null,
         "stats":{"days":30,"tools":[{"tool":"scm_get_merge_request_diff","calls":4,"errors":1,"error_rate":0.25}],"trend":{"days":[{"day":"2026-11-13","calls":4,"errors":1,"slow":0}]}}}
        """;

    [Fact]
    public Task HowHarnessServesThemShowsTheVerdictTilesAndWhatToChange() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/report"] = Report;
        var (_, _, model) = await OpenAsync("agents", api);
        var serving = Agents(model).Serving;

        Agents(model).ShowsServing.Should().BeTrue();
        api.GetsWithQuery.Should().Contain(Root + "/report?days=30");
        serving.Verdict.Should().Be("1 thing to change so harness serves your agents better: see below.");
        serving.Tiles.Select(t => (t.Label, t.Value))
            .Should()
            .Equal(("Agent calls", "42"), ("Context hits", "65%"), ("Read next", "70%"), ("Knowledge", "4 read"), ("Providers with problems", "✓ none"));
        serving.Tiles[0].Note.Should().StartWith("from Claude Code, Codex; the last");
        serving.Findings.Select(f => f.Finding.Level).Should().Equal("warn", "info");
        var warn = serving.Findings[0];
        warn.HasTrend.Should().BeTrue();
        warn.Bars.Select(b => (b.Calls, b.Failed)).Should().Equal((3, 3), (2, 0));
        warn.TrendNote.Should().Be("11-12 → 11-13, red: days with the problem");
        warn.ActionLine.Should().Be("→ harness sync");
        serving.Findings[1].HasHistory.Should().BeTrue();
        serving.ResolvedHead.Should().Be("Resolved: 1 problem that stopped");
    });

    [Fact]
    public Task WithoutCallsTheVerdictSaysSo() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await OpenAsync("agents");

        Agents(model).Serving.Verdict.Should().Be("No agent called harness in 30 days.");
        Agents(model).Serving.Tiles[2].Note.Should().Be("measured with the client hooks (Tools & providers → Hooks)");
    });

    [Fact]
    public Task ToolsAndProvidersListWhatServesTheProjectAndWhatDoesNot() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/providers"] = Providers;
        var (_, _, model) = await OpenAsync("agents/tools", api);
        var tools = Agents(model).Tools;

        Agents(model).ShowsTools.Should().BeTrue();
        api.GetsWithQuery.Should().Contain(Root + "/providers?days=30");
        var graphify = tools.Used.Single();
        (graphify.Kind, graphify.PackTag, graphify.Summary).Should().Be(("provider protocol", "pack graphify-csharp", "10 calls · 10% failed"));
        graphify.Quiet.Should().Be("Not called in 30 days: impact (impact), project.overview");
        graphify.HasCheck.Should().BeTrue();
        graphify.CanIndex.Should().BeTrue();
        graphify.IsGraphify.Should().BeTrue();
        tools.Idle.Select(i => (i.Info.Name, i.Reason)).Should().Equal(("pyright", "serves python"), ("old", "disabled"));
        tools.Missing.Select(m => m.Capability).Should().Equal("change coupling");
        tools.Complete.Should().Be("✓ Complete: dependencies, duplicates (codebase-memory)");
    });

    [Fact]
    public Task WhatAProviderHasIndexedIsAskedFromThisPage() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/providers"] = Providers;
        api.Answers[Root + "/architecture"] = """{"structure":{"graphify-csharp":{"symbols":[{"type":"class","count":12},{"type":"method","count":80}],"version":"1"}}}""";
        var (_, _, model) = await OpenAsync("agents/tools", api);
        var graphify = Agents(model).Tools.Used.Single();

        await graphify.LoadIndexCommand.ExecuteAsync(null);

        api.UiGets.Should().Contain(Root + "/architecture");
        graphify.Indexed.Should().Be("symbols: class 12, method 80");
    });

    [Fact]
    public Task TheScmCardChoosesAConnectionAndTestsIt() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers[Root + "/scm"] = Scm;
        var (_, _, model) = await OpenAsync("agents/tools", api);
        var card = Agents(model).Scm;

        (card.Matched, card.Connection, card.ProjectLine).Should().Be((true, "work · https://gitlab.example.com", "Project team/fixture from remote origin · chosen on this machine"));
        card.Use.Should().Be("4 scm_ calls · 25% failed");
        card.Picks.Select(p => p.Name).Should().Equal("", "work", "mirror");
        card.Pick!.Name.Should().Be("work");
        card.CanPick.Should().BeTrue();
        api.Reply = (path, _) => path.EndsWith("/choice", StringComparison.Ordinal)
            ? Scm.Replace("\"choice\":\"work\"", "\"choice\":null", StringComparison.Ordinal)
            : """{"test":{"test":{"ok":true,"version":"17","user":"dev"},"project":{"level":"ok","detail":"team/fixture is reachable"}}}""";

        card.Pick = card.Picks[0];
        await Ui.UntilAsync(() => api.Posts.Count > 0);
        api.Posts[0].Should().Be((Root + "/scm/choice", """{"name":""}""", api.Posts[0].Origin, "1"));
        await Ui.UntilAsync(() => model.Services.Toasts.Items.Any(t => t.Text == "SCM detected from the remotes again"));

        await card.TestCommand.ExecuteAsync(null);
        api.Posts[^1].Should().Be((Root + "/scm/test", "{}", api.Posts[^1].Origin, "1"));
        card.TestLine.Should().Be("Connected to version 17 as dev.");
        card.ProjectCheck.Should().Be("team/fixture is reachable");
    });

    [Fact]
    public Task WithoutARemoteTheCardSaysWhy() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await OpenAsync("agents/tools");
        var card = Agents(model).Scm;

        card.Matched.Should().BeFalse();
        card.Reason.Should().StartWith("The repository has no remote");
        card.HasPicker.Should().BeFalse();
        card.NoTools.Should().Be("Agents have not called the scm_ tools in 30 days.");
    });

    [Fact]
    public Task HooksSayWhatIsInstalledAndWhetherItRuns() => Ui.RunAsync(async () =>
    {
        var (_, _, model) = await OpenAsync("agents/tools");
        var hooks = Agents(model).Hooks;

        hooks.GitLine.Should().Be("○ not installed: harness hooks install");
        hooks.Clients.Select(c => c.Client).Should().Equal("Claude Code · user", "Claude Code · project", "Claude Code · local", "Codex · user", "Codex · project");
        hooks.Clients.Should().OnlyContain(c => c.Problem == "harness hooks install");
        hooks.EventsLine.Should().Be("No events recorded in 7 days: if they are installed, they may not run.");
        hooks.Files.Should().HaveCount(6);
    });

    [Theory]
    [InlineData("agents")]
    [InlineData("agents/tools")]
    public Task AgentsPassTheUiChecks(string view) =>
        Ui.RunAsync(() =>
            UiChecks.AssertAsync(async (width, height) =>
            {
                var api = new FixtureApi();
                api.Answers[Root + "/report"] = Report;
                api.Answers[Root + "/providers"] = Providers;
                api.Answers[Root + "/scm"] = Scm;
                return (await OpenAsync(view, api, width, height)).Window;
            })
        );

    private static async Task<(Window Window, FixtureApi Api, MainViewModel Model)> OpenAsync(string view, FixtureApi? api = null, int width = 1280, int height = 800)
    {
        var opened = await Ui.OpenMainAsync(width, height, api: api, address: $"#/p/prj_fixture/{view}");
        await Ui.UntilAsync(() =>
            opened.Model.Page is ProjectPage
            {
                Body: AgentsViewModel { Serving.HasContent: true, Tools.HasContent: true, Scm.HasContent: true, Hooks.HasContent: true }
            }
        );
        Ui.Settle();
        return opened;
    }

    private static AgentsViewModel Agents(MainViewModel model) => (AgentsViewModel)((ProjectPage)model.Page!).Body!;
}
