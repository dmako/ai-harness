using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Controls;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The machine-wide pages against the fixture API: Machine (settings, Board defaults, SCM connections, telemetry,
/// graphify sessions, state and build), Search (#/search?q=, kinds, links) and Docs (sections, Markdown, read on),
/// with each write's request checked and the UI checks at every size and theme.
/// </summary>
public sealed class MachinePagesTests
{
    [Fact]
    public Task MachineShowsWhereTheStateLivesAndTheBuild() => Ui.RunAsync(async () =>
    {
        var (_, _, page) = await MachineAsync();

        page.Info.Paths.Select(p => (p.Label, p.Value))
            .Should()
            .Equal(
                ("Global folder", @"C:\Users\dev\AppData\Roaming\harness"),
                ("Project registry", "projects.json"),
                ("Workspaces", "workspaces.json"),
                ("Workspace and user knowledge", "knowledge")
            );
        page.Info.BuildLine.Should().Be("harness 0.13.0-localbuild · 0123456789ab · built 2026-11-13 · windows/amd64 · .NET 11.0.0");
        page.Telemetry.Heading.Should().Be("Not sharing");
    });

    [Fact]
    public Task TheSettingsAreKeptForThisPage() => Ui.RunAsync(async () =>
    {
        var preferences = new InMemoryPreferences();
        var (_, _, page) = await MachineAsync(preferences: preferences);

        page.Theme = MachinePage.Themes.Single(t => t.Value == "dark");
        page.ProjectView = page.ProjectViews.Single(v => v.Value == "board");

        preferences.Get("theme").Should().Be("dark");
        Avalonia.Application.Current!.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
        preferences.Get("projectView").Should().Be("board");
        Harness.Ui.Navigation.Route.Parse("#/p/prj_fixture").View.Should().Be("board");
        page.Theme = MachinePage.Themes.Single(t => t.Value == "system");
        page.ProjectView = page.ProjectViews.Single(v => v.Value == "overview");
    });

    [Fact]
    public Task ABoardDefaultIsSavedAtOnce() => Ui.RunAsync(async () =>
    {
        var (api, model, page) = await MachineAsync();
        api.Reply = (_, _) => """{"defaults":{"sort":"id","hide_done_days":7}}""";
        page.Board.Sort!.Value.Should().Be("priority");

        page.Board.Sort = page.Board.Sorts.Single(s => s.Value == "id");
        await Ui.UntilAsync(() => api.Posts.Count > 0);

        api.Posts.Should().ContainSingle().Which.Should().Be(("/api/board-defaults", """{"sort":"id"}""", api.Posts[0].Origin, "1"));
        await Ui.UntilAsync(() => model.Services.Toasts.Items.Any(t => t.Text == "Board default saved"));
    });

    [Fact]
    public Task AnScmConnectionIsTestedEditedAndRemoved() => Ui.RunAsync(async () =>
    {
        var (api, model, page) = await MachineAsync();
        var row = page.Scm.Rows.Single();
        row.TokenLine.Should().Be("token ****oken");
        row.Aliases.Should().Be("Also cloned from ssh-gitlab");
        row.TestLine.Should().Be("Not tested yet.");
        api.Reply = (path, _) => path.EndsWith("/test", StringComparison.Ordinal)
            ? """{"test":{"ok":false,"failure":"unauthorized","message":"401","hint":"Create a new token"}}"""
            : null;

        await page.Scm.TestCommand.ExecuteAsync(row);
        api.Posts[^1].Should().Be(("/api/scm/test", """{"name":"work","type":"","url":"","token":""}""", api.Posts[^1].Origin, "1"));
        row.TestLine.Should().Be("Token rejected: 401");
        row.TestHint.Should().Be("Create a new token");

        _ = page.Scm.EditCommand.ExecuteAsync(row);
        var edit = await DialogAsync<ScmFormDialog>(model);
        edit.TokenPlaceholder.Should().Be("(unchanged)");
        edit.Aliases.Single().Text.Should().Be("ssh-gitlab");
        edit.Address = "https://gitlab.example.org";
        await edit.AcceptCommand.ExecuteAsync(null);
        api.Posts[^1].Path.Should().Be("/api/scm/work/edit");
        api.Posts[^1].Body.Should().Be("""{"name":"work","type":"gitlab","url":"https://gitlab.example.org","token":"","aliases":["ssh-gitlab"]}""");

        var removing = page.Scm.RemoveCommand.ExecuteAsync(page.Scm.Rows.Single());
        var confirm = await DialogAsync<ConfirmDialog>(model);
        confirm.Danger.Should().BeTrue();
        await confirm.AcceptCommand.ExecuteAsync(null);
        await removing;
        api.Posts[^1].Should().Be(("/api/scm/work/remove", "{}", api.Posts[^1].Origin, "1"));
    });

    [Fact]
    public Task AnScmIsAddedOnceItsFormIsComplete() => Ui.RunAsync(async () =>
    {
        var (api, model, page) = await MachineAsync();
        api.Reply = (path, _) => path.EndsWith("/test", StringComparison.Ordinal) ? """{"test":{"ok":true,"version":"17.2","user":"dev"}}""" : null;

        _ = page.Scm.AddCommand.ExecuteAsync(null);
        var add = await DialogAsync<ScmFormDialog>(model);
        await add.AcceptCommand.ExecuteAsync(null);
        add.Error.Should().StartWith("Name");
        add.Name = "home";
        add.Type = ScmFormDialog.Types.Single(t => t.Value == "github");
        add.Address = "https://github.com";
        add.Token = "ghp_secret";
        await add.TestCommand.ExecuteAsync(null);
        add.TestLine.Should().Be("Connected to version 17.2 as dev.");
        add.AddAliasCommand.Execute(null);
        add.Aliases[0].Text = "git@github.com";
        await add.AcceptCommand.ExecuteAsync(null);

        api.Posts.Select(p => (p.Path, p.Body))
            .Should()
            .Equal(
                ("/api/scm/test", """{"name":"home","type":"github","url":"https://github.com","token":"ghp_secret"}"""),
                ("/api/scm", """{"name":"home","type":"github","url":"https://github.com","token":"ghp_secret","aliases":["git@github.com"]}""")
            );
        model.Services.Toasts.Items.Should().Contain(t => t.Text == "SCM home added");
    });

    [Fact]
    public Task TelemetryIsTurnedOnWithItsCollectorAndOff() => Ui.RunAsync(async () =>
    {
        var (api, _, page) = await MachineAsync();
        var telemetry = page.Telemetry;
        api.Reply = (_, body) => body.Contains("true", StringComparison.Ordinal)
            ? """{"telemetry":{"enabled":true,"endpoint":"http://collector:8766/v1/report","last_sent":null}}"""
            : """{"telemetry":{"enabled":false,"endpoint":"","last_sent":null}}""";

        await telemetry.ToggleCommand.ExecuteAsync(null);
        telemetry.Editing.Should().BeTrue("on asks for the collector first");
        telemetry.Endpoint = "http://collector:8766/v1/report";
        telemetry.Token = "secret";
        await telemetry.SaveCommand.ExecuteAsync(null);
        api.Posts[^1].Should().Be(("/api/telemetry", """{"enabled":true,"endpoint":"http://collector:8766/v1/report","token":"secret"}""", api.Posts[^1].Origin, "1"));
        telemetry.Heading.Should().Be("Sharing");
        telemetry.Note.Should().Be("To http://collector:8766/v1/report; nothing sent yet, at most daily after a sync.");

        await telemetry.ToggleCommand.ExecuteAsync(null);
        api.Posts[^1].Body.Should().Be("""{"enabled":false}""");
        telemetry.Enabled.Should().BeFalse();
    });

    [Fact]
    public Task GraphifySessionsAreRestartedAndStopped() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/graphify/sessions"] = """
            {"sessions":[{"id":"11111111-2222-3333-4444-555555555555","root":"C:\\repos\\fixture","state":"ready","ready":true,"summary":"ready, 0.1 GB, up 2h, started by harness","project_id":"prj_fixture","project_name":"fixture"},
                         {"id":"99999999-8888-7777-6666-555555555555","root":"C:\\repos\\other","state":"unknown","summary":"not reachable (process gone), started outside harness"}]}
            """;
        var (_, _, page) = await MachineAsync(api);
        var rows = page.Graphify.Rows;

        rows.Select(r => (r.Solution, r.StateWord, r.Detail)).Should().Equal(("fixture", "ready", "0.1 GB · up 2h · started by harness"), ("other", "not reachable (process gone)", "started outside harness"));
        rows[0].Href.Should().Be("#/p/prj_fixture/agents/tools");
        rows[1].HasProject.Should().BeFalse("only a project's session restarts");

        await page.Graphify.RestartCommand.ExecuteAsync(rows[0]);
        await page.Graphify.StopCommand.ExecuteAsync(page.Graphify.Rows[1]);

        api.Posts.Select(p => (p.Path, p.Body))
            .Should()
            .Equal(
                ("/api/graphify/sessions/restart", """{"project":"prj_fixture"}"""),
                ("/api/graphify/sessions/stop", """{"session":"99999999-8888-7777-6666-555555555555"}""")
            );
    });

    [Fact]
    public Task SearchWithoutAQueryShowsWhatEachKindIsSearchedBy() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await Ui.OpenMainAsync(1280, 800, address: "#/search");
        var page = model.Page.Should().BeOfType<SearchPage>().Subject;

        page.ShowsKinds.Should().BeTrue();
        page.Kinds.Select(k => k.Label).Should().Equal("Projects", "Workspaces", "Memories", "Tasks", "Blueprints");
        api.Gets.Should().NotContain("/api/search");

        page.Query = "fixture";
        page.SubmitCommand.Execute(null);
        model.Navigation.Current.Href.Should().Be("#/search?q=fixture");
        await Ui.UntilAsync(() => page.Groups.Count > 0);
        model.Page.Should().BeSameAs(page, "a new query keeps the page");
    });

    [Fact]
    public Task SearchResultsAreGroupedByKindAndLinkToTheirPages() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/search"] = """
            {"query":"live","per_kind":2,"hits":[
              {"kind":"project","title":"Fixture project","detail":"C:\\repos\\fixture","project_id":"prj_fixture","project":"Fixture project"},
              {"kind":"memory","title":"Live updates use SSE","detail":"decision","project_id":"prj_fixture","project":"Fixture project"},
              {"kind":"memory","title":"Write one-line commits","detail":"convention"},
              {"kind":"task","title":"TASK-0002 Port the Board","detail":"in_progress","project_id":"prj_fixture","project":"Fixture project","ref":"TASK-0002"},
              {"kind":"workspace","title":"Main workspace","workspace":"main"}]}
            """;
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, address: "#/search?q=live");
        var page = (SearchPage)model.Page!;
        await Ui.UntilAsync(() => page.Groups.Count > 0);

        api.GetsWithQuery.Should().Contain("/api/search?q=live");
        page.Groups.Select(g => (g.Label, g.Count)).Should().Equal(("Projects", "1"), ("Workspaces", "1"), ("Memories", "2+"), ("Tasks", "1"));
        var hrefs = page.Groups.SelectMany(g => g.Hits).Select(h => (h.Hit.Title, h.Href, h.Where)).ToArray();
        hrefs.Should().Contain(("Fixture project", "#/p/prj_fixture/overview", @"C:\repos\fixture"));
        hrefs.Should().Contain(("Main workspace", "#/w/main/overview", ""));
        hrefs.Should().Contain(("Live updates use SSE", "#/p/prj_fixture/knowledge", "Fixture project"));
        hrefs.Should().Contain(("Write one-line commits", "#/knowledge", "you · all your projects"));
        hrefs.Should().Contain(("TASK-0002 Port the Board", "#/p/prj_fixture/work/TASK-0002", "Fixture project"));

        page.Present.Single(k => k.Kind == "memory").On = false;
        page.Groups.Select(g => g.Label).Should().Equal("Projects", "Workspaces", "Tasks");
    });

    [Fact]
    public Task NothingFoundSaysSo() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/search"] = """{"query":"zz","per_kind":20,"hits":[]}""";
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, address: "#/search?q=zz");
        var page = (SearchPage)model.Page!;

        await Ui.UntilAsync(() => page.Nothing is not null);
        page.Nothing.Should().Be("Nothing matches “zz”");
    });

    [Fact]
    public Task DocsListTheSectionsAndRenderTheMarkdown() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/docs/{slug}"] = Doc;
        var (window, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, address: "#/docs/guide-what-harness-is-for");
        var page = model.Page.Should().BeOfType<DocsPage>().Subject;
        await Ui.UntilAsync(() => page.Doc is not null);
        Ui.Settle();

        page.Groups.Select(g => (g.Title, g.Intro?.Slug, g.Items.Count)).Should().Equal(("Using harness", "guide", 3), ("Reference", "reference", 2));
        page.Groups[0].Items[0].Current.Should().BeTrue();
        (page.Previous?.Slug, page.Next?.Slug).Should().Be(("guide", "guide-how-knowledge-gets-into-memory"));

        var text = window.GetVisualDescendants().OfType<MarkdownBlock>().Single();
        var blocks = text.Children;
        blocks[0].Should().BeOfType<SelectableTextBlock>().Which.Classes.Should().Contain("h2");
        blocks.OfType<Border>().Should().ContainSingle(b => b.Classes.Contains("inset"), "the fenced code");
        blocks.OfType<ScrollViewer>().Should().ContainSingle(s => s.Content is Grid, "the table");
        blocks.OfType<StackPanel>().Should().ContainSingle("the list");
        var paragraph = blocks.OfType<SelectableTextBlock>().First(b => !b.Classes.Contains("h2") && !b.Classes.Contains("h3"));
        paragraph.Inlines!.OfType<Run>().Should().Contain(r => r.Classes.Contains("code") && r.Text == "harness docs");
        paragraph.Inlines!.OfType<Run>().Should().Contain(r => r.Text == "bold" && r.FontWeight == Avalonia.Media.FontWeight.SemiBold);
        var links = text.GetVisualDescendants().OfType<AppLink>().ToArray();
        links.Should().ContainSingle(l => l.Href == "#/docs/commands");
        text.GetVisualDescendants().OfType<HyperlinkButton>().Should().ContainSingle(h => h.NavigateUri == new Uri("https://example.com/x"));
    });

    [Fact]
    public Task FollowingADocsLinkShowsThatSection() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/docs/{slug}"] = Doc;
        var (_, _, model) = await Ui.OpenMainAsync(1280, 800, api: api, address: "#/docs/guide");
        var page = (DocsPage)model.Page!;
        await Ui.UntilAsync(() => page.Doc is not null);

        model.Navigation.Go("#/docs/commands");

        await Ui.UntilAsync(() => page.Groups.Any(g => g.Items.Any(i => i.Slug == "commands" && i.Current)));
        model.Page.Should().BeSameAs(page);
        api.Gets.Should().Contain("/api/docs/commands");
    });

    [Theory]
    [InlineData("#/machine")]
    [InlineData("#/search")]
    [InlineData("#/search?q=fixture")]
    [InlineData("#/docs/guide")]
    public Task TheMachinePagesPassTheUiChecks(string address) =>
        Ui.RunAsync(() =>
            UiChecks.AssertAsync(async (width, height) =>
            {
                var api = new FixtureApi();
                api.Answers["/api/docs/{slug}"] = Doc;
                var (window, _, _) = await Ui.OpenMainAsync(width, height, api: api, address: address);
                await Task.Delay(150);
                Ui.Settle();
                return window;
            })
        );

    private const string Doc = """
        {"doc":"guide","slug":"guide-what-harness-is-for","title":"What harness is for",
         "body":"# What harness is for\r\n\r\nIt keeps **bold** memory; `harness docs` lists [the commands](#commands) and [more](https://example.com/x).\r\n\r\n## Steps\r\n\r\n- one\r\n- two\r\n  continued\r\n\r\n| Command | What |\r\n|---|---|\r\n| `harness sync` | indexes |\r\n\r\n```\r\nharness init\r\n```\r\n"}
        """;

    private static async Task<(FixtureApi Api, MainViewModel Model, MachinePage Page)> MachineAsync(FixtureApi? api = null, IPreferences? preferences = null)
    {
        var (_, fixture, model) = await Ui.OpenMainAsync(1280, 800, api: api, address: "#/machine", preferences: preferences);
        var page = model.Page.Should().BeOfType<MachinePage>().Subject;
        await Ui.UntilAsync(() => page.Info.HasContent && page.Board.HasContent && page.Scm.HasContent && page.Graphify.HasContent);
        Ui.Settle();
        return (fixture, model, page);
    }

    private static async Task<T> DialogAsync<T>(MainViewModel model)
        where T : DialogViewModel
    {
        await Ui.UntilAsync(() => model.Services.Dialogs.Current is T);
        Ui.Settle();
        return (T)model.Services.Dialogs.Current!;
    }
}
