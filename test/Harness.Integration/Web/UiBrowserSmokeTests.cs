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
/// It reuses the published bundle (HARNESS_UI_TEST_BUNDLE, else artifacts/ui/wwwroot) and the browser of
/// HARNESS_UI_BROWSER, and is skipped, saying why, when either is missing.
/// </summary>
[Collection("Provider processes")]
public sealed class UiBrowserSmokeTests(SharedBrowser browser)
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
        var native = Environment.GetEnvironmentVariable("HARNESS_UI_NATIVE_EXECUTABLE") is { Length: > 0 } path
            ? path : null;
        var embedded = native is not null || Environment.GetEnvironmentVariable("HARNESS_UI_TEST_EMBEDDED") is "1";
        await using var server = await UiFixture.StartAsync(
            fixture, uiBundle: embedded ? null : bundle, nativeExecutable: native
        );
        var id = UiWriteTests.ProjectId(fixture);
        await using var isolated = await browser.OpenAsync(executable, Token);
        var page = isolated.Page;
        await page.CommandAsync("Page.enable", new { });
        await page.CommandAsync("Page.addScriptToEvaluateOnNewDocument", new { source = Watch });

        // a deep link, drawn in time
        await page.CommandAsync("Page.navigate", new { url = $"{server.Client.BaseAddress}#/p/{id}/board" });
        await page.WaitAsync("globalThis.harnessTimings?.board > 0");
        (await ValueAsync(page, "harnessTimings.board")).GetDouble().Should().BeLessThan(20_000, "the Board is drawn within 20 s");

        // The browser shortcut is intercepted for the Avalonia find bar, and the canvas remains in the AX tree.
        (await ValueAsync(page, "!window.dispatchEvent(new KeyboardEvent('keydown', {key:'f', ctrlKey:true, bubbles:true, cancelable:true}))"))
            .GetBoolean().Should().BeTrue();
        var accessibility = await page.CommandAsync("Accessibility.getFullAXTree", new { });
        var roles = accessibility.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("role").GetProperty("value").GetString()).ToArray();
        roles.Should().Contain("Canvas");
        roles.Should().NotContain("button", "Avalonia 12.1.6 does not expose the Board's buttons to the browser tree");

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

        // A single browser context keeps its cache between navigations, as in a returning visit.
        if (Environment.GetEnvironmentVariable("HARNESS_UI_TIMING_FILE") is { Length: > 0 } timingFile)
        {
            var samples = new List<double>();
            await page.EvaluateAsync($"location.hash = '#/p/{id}/board'");
            await page.WaitAsync($"globalThis.harnessTimings?.route === '#/p/{id}/board'");
            for (var i = 0; i < 5; i++)
            {
                var previous = (await ValueAsync(page, "performance.timeOrigin")).GetDouble();
                await page.CommandAsync("Page.reload", new { });
                var origin = previous.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await page.WaitAsync($"performance.timeOrigin !== {origin} && globalThis.harnessTimings?.board > 0");
                samples.Add((await ValueAsync(page, "harnessTimings.board")).GetDouble());
            }
            await File.WriteAllTextAsync(timingFile, JsonSerializer.Serialize(samples), Token);
        }
    }

    private static async Task<JsonElement> ValueAsync(BrowserPage page, string expression) =>
        (await page.EvaluateAsync(expression)).GetProperty("result").GetProperty("value");

    /// <summary>The published bundle's wwwroot, or a skip saying how to make one.</summary>
    private static string Bundle()
    {
        var folder = Environment.GetEnvironmentVariable("HARNESS_UI_TEST_BUNDLE") is { Length: > 0 } set
            ? set
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/wwwroot"));
        return File.Exists(Path.Combine(folder, "index.html")) && Directory.Exists(Path.Combine(folder, "_framework"))
            ? folder
            : throw Xunit.Sdk.SkipException.ForSkip(
                $"No Avalonia UI bundle at {folder}: publish it with 'dotnet publish src/Harness.Ui.Browser -c Release "
                    + "-o artifacts/ui' (it needs the wasm-tools workload) or set HARNESS_UI_TEST_BUNDLE."
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
