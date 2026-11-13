using Avalonia.Controls;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The Avalonia Health view against the fixture API: the summary and what is wrong first, the passing checks by area,
/// providers checked only on request, a fix run or shown first, a sync, and the UI checks at every size and theme.
/// </summary>
public sealed class HealthViewTests
{
    private const string Address = "#/p/prj_fixture/health";
    private const string Fix = "/api/projects/prj_fixture/fix";

    [Fact]
    public Task HealthShowsWhatIsWrongFirstThenWhatPassesByArea() => Ui.RunAsync(async () =>
    {
        var (window, _, model) = await OpenAsync(1280, 800);
        var health = Health(model);

        health.Headline.Should().Be("Needs a fix");
        health.Tone.Should().Be("fail");
        health.Summary.Should().Be("1 failing check · 1 warning · index stale · providers not checked");
        health.CanSync.Should().BeTrue("the index has never been synced");
        health.Failing.Select(c => c.Name).Should().Equal("claude-code", "codex");
        var codex = health.Failing[1];
        codex.CanFix.Should().BeTrue();
        codex.Previewed.Should().BeTrue();
        codex.Lines.Should().Equal("the harness MCP server entry is out of date");
        codex.Tip.Should().Be(@"C:\Users\dev\.codex\config.toml");
        codex.FixLine.Should().Be("Fix: harness doctor --fix codex");

        health.Passing.Select(a => a.Name).Should().Equal("Installation", "Project", "Code providers");
        var installation = health.Passing[0].Checks;
        installation.Select(c => (c.Name, c.Value)).Should().Equal(("binary", "0.13.0"), ("config", "configuration"));
        var project = health.Passing[1].Checks;
        project.Select(c => (c.Name, c.Value))
            .Should()
            .Equal(("git", "main @ eeacf0a"), ("project", "1 worktree"), ("database", "schema 27/27"));
        project[0].Lines.Should().Equal(@"C:\repos\fixture");

        health.Paths.Select(f => f.Label).Should().Equal("Project id", "Root", "Database", "Config, global", "Config, project");
        health.GitFacts.Select(f => (f.Label, f.Value))
            .Should()
            .Contain(("Commit", "eeacf0ad6662")).And.Contain(("Providers", "none enabled"));
        health.HasSyncs.Should().BeFalse();

        var view = window.GetVisualDescendants().OfType<HealthView>().Single();
        view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content)
            .Should()
            .Contain("Check providers").And.Contain("Sync now").And.Contain("Fix it");
    });

    [Fact]
    public Task CheckingTheProvidersAsksForThemFromHarnessUisOwnPage() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync(1280, 800);
        api.UiGets.Should().BeEmpty("the providers are started only on request");

        await Health(model).ProbeCommand.ExecuteAsync(null);

        api.UiGets.Should().Equal("/api/projects/prj_fixture/health?probe=1");
        Health(model).Probing.Should().BeFalse();
    });

    [Fact]
    public Task AFixThatWritesAFileIsShownFirstAndWrittenOnlyWhenAccepted() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync(1280, 800);
        api.Reply = (_, body) =>
            body.Contains("preview", StringComparison.Ordinal)
                ? """{"ok":true,"message":"harness doctor --fix codex --dry-run","output":"Would update C:\\Users\\dev\\.codex\\config.toml:\n- command = \"old\"\n+ command = \"harness\"\nDry run: nothing written"}"""
                : """{"ok":true,"message":"Updated the Codex configuration"}""";
        var health = Health(model);
        var gets = api.Gets.Count(g => g.EndsWith("/health", StringComparison.Ordinal));

        var fixing = health.FixCommand.ExecuteAsync(health.Failing.Single(c => c.Name == "codex"));
        await Ui.UntilAsync(() => model.Services.Dialogs.Current is FixPreviewDialog);
        var dialog = (FixPreviewDialog)model.Services.Dialogs.Current!;
        dialog.AcceptLabel.Should().Be("Write it");
        dialog.Head.Should().Be(@"Would update C:\Users\dev\.codex\config.toml:");
        dialog.Diff.Should().Be("- command = \"old\"\n+ command = \"harness\"");
        api.Posts.Select(p => p.Body).Should().Equal("""{"check":"codex","preview":true}""");

        await dialog.AcceptCommand.ExecuteAsync(null);
        await fixing;

        api.Posts.Select(p => (p.Path, p.Body))
            .Should()
            .Equal((Fix, """{"check":"codex","preview":true}"""), (Fix, """{"check":"codex"}"""));
        model.Services.Toasts.Items.Should().Contain(t => t.Text == "Updated the Codex configuration");
        await Ui.UntilAsync(() => api.Gets.Count(g => g.EndsWith("/health", StringComparison.Ordinal)) > gets);
    });

    [Fact]
    public Task ACancelledPreviewWritesNothing() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync(1280, 800);
        api.Reply = (_, _) => """{"ok":true,"output":"Would update a file:\n+ line"}""";
        var health = Health(model);

        var fixing = health.FixCommand.ExecuteAsync(health.Failing.Single(c => c.Name == "codex"));
        await Ui.UntilAsync(() => model.Services.Dialogs.Current is FixPreviewDialog);
        model.Services.Dialogs.Current!.Cancel();
        await fixing;

        api.Posts.Should().ContainSingle().Which.Body.Should().Contain("preview");
    });

    [Fact]
    public Task AFixThatFailsSaysSoWithItsOutput() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync(1280, 800);
        api.Reply = (_, _) => """{"ok":false,"message":"harness install failed","output":"permission denied"}""";
        var health = Health(model);

        await health.FixCommand.ExecuteAsync(health.Failing.Single(c => c.Name == "claude-code"));

        api.Posts.Select(p => p.Body).Should().Equal("""{"check":"claude-code"}""");
        var toast = model.Services.Toasts.Items.Should().ContainSingle().Subject;
        toast.Text.Should().Be("harness install failed");
        toast.Detail.Should().Be("permission denied");
        toast.Bad.Should().BeTrue();
    });

    [Fact]
    public Task SyncNowSyncsAndReloads() => Ui.RunAsync(async () =>
    {
        var (_, api, model) = await OpenAsync(1280, 800);
        api.Reply = (_, _) => """{"ok":true,"message":"Synced"}""";
        api.Answers["/api/projects/prj_fixture/health"] = Fixtures.Health();

        await Health(model).SyncCommand.ExecuteAsync(null);

        api.Posts.Should().ContainSingle().Which.Body.Should().Be("""{"sync":true}""");
        await Ui.UntilAsync(() => Health(model).HasSyncs);
        Health(model).CanSync.Should().BeFalse();
    });

    [Fact]
    public Task FreshnessAndRecentSyncsFollowEachProvider() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/projects/prj_fixture/health"] = Fixtures.Health(healthy: true);
        var (_, _, model) = await OpenAsync(1280, 800, api);
        var health = Health(model);

        health.Headline.Should().Be("Healthy");
        health.Failing.Should().BeEmpty();
        health.Fresh.Select(f => (f.Provider, f.LastOutcome, f.Failed, f.AtCommit))
            .Should()
            .Equal(("graph", "failed", true, "1234567"), ("text", "refreshed", false, "eeacf0a"));
        health.Fresh[0].LastFailure.Should().EndWith(": the graph provider timed out after 120 s");
        health.Fresh[1].LastFailure.Should().Be("none");
        health.Syncs.Select(s => (s.Status, s.Commits, s.Delta))
            .Should()
            .Equal(("partial", "0000aaa → eeacf0a", "+2 ~5 -1"), ("ok", "0000aaa → 1234567", "+2 ~5 -1"));
        health.Syncs[0].Providers.Should().Be("text refreshed · graph failed");
    });

    [Fact]
    public Task HealthShowsWhileTheProjectsStateCannotBeRead() => Ui.RunAsync(async () =>
    {
        var api = new FixtureApi();
        api.Answers["/api/projects"] = Fixtures.Projects.Replace("\"state\": \"ok\"", "\"state\": \"locked\"", StringComparison.Ordinal);
        var (_, _, model) = await OpenAsync(1280, 800, api);

        Health(model).Failing.Should().NotBeEmpty();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HealthPassesTheUiChecks(bool synced) =>
        Ui.RunAsync(() =>
            UiChecks.AssertAsync(async (width, height) =>
            {
                var api = new FixtureApi();
                if (synced)
                {
                    api.Answers["/api/projects/prj_fixture/health"] = Fixtures.Health();
                }
                return (await OpenAsync(width, height, api)).Window;
            })
        );

    private static async Task<(Window Window, FixtureApi Api, MainViewModel Model)> OpenAsync(int width, int height, FixtureApi? api = null)
    {
        var opened = await Ui.OpenMainAsync(width, height, api: api, address: Address);
        await Ui.UntilAsync(() => opened.Model.Page is ProjectPage { Body: HealthViewModel { HasContent: true, Refreshing: false } });
        return opened;
    }

    private static HealthViewModel Health(MainViewModel model) => (HealthViewModel)((ProjectPage)model.Page!).Body!;
}
