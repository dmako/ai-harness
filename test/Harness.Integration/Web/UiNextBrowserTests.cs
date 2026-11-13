using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Tasks;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Sync;
using Harness.Web;
using Xunit;

namespace Harness.Integration.Web;

/// <summary>
/// The Avalonia UI in a real browser (TASK-0161): what headless tests cannot see. The WebAssembly bundle loads under
/// harness ui's content security policy at a deep link, logs no error, draws the Board in time, follows a change to
/// the project through the streamed /api/events without reloading, and moves between views with Back and Forward.
/// It reuses the published bundle (HARNESS_UI_NEXT_DIR, else artifacts/next/wwwroot) and the browser of
/// HARNESS_UI_BROWSER, and is skipped, saying why, when either is missing.
/// </summary>
[Collection("Provider processes")]
public sealed class UiNextBrowserTests(SharedBrowser browser)
{
    /// <summary>Collects what the page reports as wrong: console errors, uncaught errors, refused content.</summary>
    private const string Watch = """
        window.__problems = [];
        const report = (what) => window.__problems.push(String(what));
        const error = console.error.bind(console);
        console.error = (...args) => { report("console.error: " + args.join(" ")); error(...args); };
        window.addEventListener("error", (e) => report("error: " + (e.message || e.type)));
        window.addEventListener("unhandledrejection", (e) => report("unhandled rejection: " + (e.reason?.message || e.reason)));
        document.addEventListener("securitypolicyviolation", (e) => report("csp: " + e.violatedDirective + " " + e.blockedURI));
        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheBundleLoadsADeepLinkFollowsAChangeAndMovesBackAndForward()
    {
        var bundle = Bundle();
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        UiFixtureShapeTests.Seed(fixture);
        await using var server = await UiFixture.StartAsync(
            fixture,
            environment: new Dictionary<string, string> { [UiNextAssets.FolderVariable] = bundle }
        );
        var id = UiWriteTests.ProjectId(fixture);
        await using var isolated = await browser.OpenAsync(executable, Token);
        var page = isolated.Page;
        await page.CommandAsync("Page.enable", new { });
        await page.CommandAsync("Page.addScriptToEvaluateOnNewDocument", new { source = Watch });

        // a deep link, drawn in time
        await page.CommandAsync("Page.navigate", new { url = $"{server.Client.BaseAddress}next/#/p/{id}/board" });
        await page.WaitAsync("globalThis.harnessTimings?.board > 0");
        (await ValueAsync(page, "harnessTimings.board")).GetDouble().Should().BeLessThan(20_000, "the Board is drawn within 20 s");

        // a change to the project redraws the Board without reloading the page
        await page.EvaluateAsync("window.__stayed = true");
        using (var database = fixture.Database())
        {
            new TaskStore(database).Create(
                new TaskDefinition { Id = "TASK-0099", Title = "Written while the page is open", Blueprint = "BP-009" },
                "",
                new TaskOrigin("test", "test", "fixture"),
                Token
            );
        }
        await page.WaitAsync("(globalThis.harnessTimings?.liveReloads || 0) >= 1");
        (await ValueAsync(page, "window.__stayed === true")).GetBoolean().Should().BeTrue("the page was not reloaded");

        // Back and Forward move between two views
        await page.EvaluateAsync($"location.hash = '#/p/{id}/overview'");
        await page.WaitAsync($"globalThis.harnessTimings?.route === '#/p/{id}/overview'");
        await page.EvaluateAsync("history.back()");
        await page.WaitAsync($"globalThis.harnessTimings?.route === '#/p/{id}/board'");
        await page.EvaluateAsync("history.forward()");
        await page.WaitAsync($"globalThis.harnessTimings?.route === '#/p/{id}/overview'");

        // Avalonia tries WebGL first and reports each mode the browser cannot give it before falling back to software
        // drawing (a headless browser without a GPU); the page then draws, so those reports are not problems
        var problems = (await ValueAsync(
            page,
            "JSON.stringify(window.__problems.filter(p => !p.startsWith('console.error: Failed to create render target for mode')))"
        )).GetString();
        problems.Should().Be("[]", problems);
    }

    private static async Task<JsonElement> ValueAsync(BrowserPage page, string expression) =>
        (await page.EvaluateAsync(expression)).GetProperty("result").GetProperty("value");

    /// <summary>The published bundle's wwwroot, or a skip saying how to make one.</summary>
    private static string Bundle()
    {
        var folder = Environment.GetEnvironmentVariable(UiNextAssets.FolderVariable) is { Length: > 0 } set
            ? set
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/next/wwwroot"));
        return File.Exists(Path.Combine(folder, "index.html")) && Directory.Exists(Path.Combine(folder, "_framework"))
            ? folder
            : throw Xunit.Sdk.SkipException.ForSkip(
                $"No Avalonia UI bundle at {folder}: publish it with 'dotnet publish src/Harness.Ui.Browser -c Release "
                    + $"-o artifacts/next' (it needs the wasm-tools workload) or set {UiNextAssets.FolderVariable}."
            );
    }

    /// <summary>The browser of HARNESS_UI_BROWSER, or a skip saying so (as UiBrowserTests).</summary>
    private static string Browser() =>
        Environment.GetEnvironmentVariable("HARNESS_UI_BROWSER") is { Length: > 0 } executable && File.Exists(executable)
            ? executable
            : throw Xunit.Sdk.SkipException.ForSkip(
                "Set HARNESS_UI_BROWSER to a Chrome or Edge executable for the Avalonia UI's browser smoke test."
            );
}
