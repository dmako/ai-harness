using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Tasks;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Web;

[Collection("Provider processes")]
public sealed class UiBrowserTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FrontendRequiresMemoryConflictChoiceAndTaskConfirmationInARealBrowser()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var memories = new MemoryStore(database);
        memories.Put(new() { Type = "decision", Content = "Template payload enums are stored as integers" }, Token);
        var proposal = memories.Propose(new("decision", "Template payload enums are stored as names"), Token).Proposal!;
        var tasks = new TaskStore(database);
        var taskProposal = tasks.Propose(
            new TaskDefinition
            {
                Title = "Browser proposed task",
                Goal = "Author reviews work in the browser",
                Acceptance = ["Author confirms"],
            },
            "Test browser review",
            "agent:test",
            Token
        );
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/knowledge/inbox" }
                );
                await page.WaitAsync("[...document.querySelectorAll('button')].some(b => b.textContent === 'Approve')");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Approve').click()"
                );
                await page.WaitAsync(
                    "document.body.textContent.includes('Replace one of them with it, or keep both:')"
                );
                memories
                    .GetProposal(proposal.Id, Token)
                    .Proposal.Status.Should()
                    .Be("pending", "the conflict choice must precede approval");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Keep both').click()"
                );
                await ProviderFixture.EventuallyAsync(() =>
                    memories.GetProposal(proposal.Id, Token).Proposal.Status is "approved"
                );
                await page.EvaluateAsync("location.hash = '#/p/" + id + "/work/inbox'");
                await page.WaitAsync(
                    "[...document.querySelectorAll('button')].some(b => b.textContent === 'Create the task…')"
                );
                await page.EvaluateAsync(
                    """
                    window.originalReviewFetch = window.fetch;
                    window.holdTaskRefresh = true;
                    window.holdReviewRead = true;
                    window.fetch = async (...args) => {
                      const response = await window.originalReviewFetch(...args);
                      const url = String(args[0]);
                      if (window.holdTaskRefresh && url.includes('/task-proposals?')) {
                        window.holdTaskRefresh = false;
                        window.taskRefreshWaiting = true;
                        await new Promise(resolve => { window.releaseTaskRefresh = resolve; });
                      }
                      if (window.holdReviewRead && url.includes('/blueprints')) {
                        window.holdReviewRead = false;
                        window.reviewReadWaiting = true;
                        await new Promise(resolve => { window.releaseReviewRead = resolve; });
                      }
                      return response;
                    };
                    """
                );
                tasks.Propose(
                    new TaskDefinition
                    {
                        Title = "Concurrent refresh proposal",
                        Goal = "A pending refresh must preserve the review being opened",
                        Acceptance = ["Form stays connected"],
                    },
                    "Review race",
                    "agent:test",
                    Token
                );
                await page.WaitAsync("window.taskRefreshWaiting === true");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the task…').click()"
                );
                await page.WaitAsync("window.reviewReadWaiting === true");
                await page.EvaluateAsync("window.releaseTaskRefresh(); window.releaseReviewRead();");
                await page.WaitAsync("!!document.querySelector('input[aria-label=Title]')");
                tasks
                    .GetProposal(taskProposal.Id, Token)
                    .Status.Should()
                    .Be("pending", "opening the review form must not create the task");
                await page.EvaluateAsync(
                    "document.querySelector('input[aria-label=Title]').value = 'Browser reviewed task'"
                );
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the task').click()"
                );
                await ProviderFixture.EventuallyAsync(() =>
                    tasks.GetProposal(taskProposal.Id, Token).Status is "approved"
                );
                tasks
                    .List(cancellationToken: Token)
                    .Should()
                    .ContainSingle()
                    .Which.Definition.Title.Should()
                    .Be("Browser reviewed task");
                await page.WaitAsync("!!document.querySelector('.live.on')");
                tasks.Propose(
                    new TaskDefinition
                    {
                        Title = "Live update proposal",
                        Goal = "Show independent writes without reload",
                        Acceptance = ["Visible live"],
                    },
                    "SSE check",
                    "agent:test",
                    Token
                );
                await page.WaitAsync("document.body.textContent.includes('Live update proposal')");
                await page.CommandAsync("Network.enable", new { });
                await page.CommandAsync("Network.setBlockedURLs", new { urls = new[] { "*/api/events" } });
                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                await page.WaitAsync(
                    "!!document.querySelector('.live.off') && !document.getElementById('refresh').hidden"
                );
                tasks.Propose(
                    new TaskDefinition
                    {
                        Title = "Fallback polling proposal",
                        Goal = "Show independent writes while SSE is unavailable",
                        Acceptance = ["Visible through polling"],
                    },
                    "Polling check",
                    "agent:test",
                    Token
                );
                await page.WaitAsync("document.body.textContent.includes('Fallback polling proposal')");
                await page.CommandAsync("Network.setBlockedURLs", new { urls = Array.Empty<string>() });
                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                await page.WaitAsync("!!document.querySelector('.live.on')");
            }
        );
    }

    [Fact]
    public async Task ReadyTaskIsEditedWithTheProposalFormAndClaimedOneOffersNoEdit()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        var origin = new TaskOrigin("test", "test", "fixture");
        tasks.Create(
            new TaskDefinition
            {
                Id = "UI-0001",
                Title = "Ready task",
                Acceptance = ["Old"],
            },
            "",
            origin,
            Token
        );
        tasks.Create(new TaskDefinition { Id = "UI-0002", Title = "Claimed task" }, "", origin, Token);
        tasks.Claim("UI-0002", new("claude"), cancellationToken: Token);
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/work/UI-0001" }
                );
                await page.WaitAsync("[...document.querySelectorAll('button')].some(b => b.textContent === 'Edit…')");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Edit…').click()"
                );
                await page.WaitAsync("!!document.querySelector('input[aria-label=Title]')");
                (
                    await ValueAsync(
                        page,
                        "[...document.querySelectorAll('a')].some(a => a.textContent.startsWith('Blueprints'))"
                            + " && !!document.querySelector('select[aria-label=Blueprint]')"
                            + " && !/milestone/i.test(document.body.textContent)"
                    )
                )
                    .Should()
                    .Be("True");
                (await ValueAsync(page, "document.querySelector('input[aria-label=Id]').disabled")).Should().Be("True");
                (await ValueAsync(page, "document.querySelector('textarea[aria-label=\"Acceptance criteria\"]').value"))
                    .Should()
                    .Be("Old");
                await page.EvaluateAsync(
                    "document.querySelector('input[aria-label=Title]').value = 'Edited in the browser'"
                );
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Save the task').click()"
                );
                await ProviderFixture.EventuallyAsync(() =>
                    tasks.Get("UI-0001", Token).Definition.Title is "Edited in the browser"
                );
                tasks.Get("UI-0001", Token).Definition.Acceptance.Should().Equal("Old");
                await page.EvaluateAsync("location.hash = '#/p/" + id + "/work/UI-0002'");
                await page.WaitAsync("!!document.querySelector('#task-UI-0002 .tdetail')");
                (await ValueAsync(page, "!!document.querySelector('#task-UI-0002 .tdetail .review')"))
                    .Should()
                    .Be("False");
            }
        );
    }

    [Fact]
    public async Task BlueprintsAreCreatedEditedAndRemovedAskingOnlyWhenTheyHoldTasks()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        tasks.CreateBlueprint(new("EMPTY", "Nothing in it"), "test", Token);
        static string Click(string text) =>
            $"[...document.querySelectorAll('button')].find(b => b.textContent === '{text}').click()";
        static string Card(string id) => $"document.querySelector('.mcard[data-blueprint=\"{id}\"]')";
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/work/blueprints" }
                );
                await page.WaitAsync(
                    "[...document.querySelectorAll('button')].some(b => b.textContent === 'New blueprint…')"
                );
                await page.EvaluateAsync(Click("New blueprint…"));
                await page.WaitAsync("document.querySelector('input[aria-label=Id]')?.value === 'BP-001'");
                (await ValueAsync(page, "document.querySelector('input[aria-label=Name]').value"))
                    .Should()
                    .Be("New blueprint");
                await page.EvaluateAsync("document.querySelector('input[aria-label=Name]').value = 'From the browser'");
                await page.EvaluateAsync(Click("Add the blueprint"));
                await page.WaitAsync($"!!{Card("BP-001")}");
                tasks.Blueprints(Token).Select(b => b.Definition.Title).Should().Contain("From the browser");

                await page.EvaluateAsync(
                    $"[...{Card("BP-001")}.querySelectorAll('button')].find(b => b.textContent === 'Edit…').click()"
                );
                await page.WaitAsync($"!!{Card("BP-001")}.querySelector('textarea[aria-label=Goal]')");
                await page.EvaluateAsync(
                    $"{Card("BP-001")}.querySelector('textarea[aria-label=Goal]').value = 'Edited goal'"
                );
                await page.EvaluateAsync(Click("Save the blueprint"));
                await ProviderFixture.EventuallyAsync(() =>
                    tasks.Blueprints(Token).Single(b => b.Definition.Id == "BP-001").Definition.Goal is "Edited goal"
                );

                // no tasks: removed at once, nothing asked
                await page.EvaluateAsync(
                    $"[...{Card("EMPTY")}.querySelectorAll('button')].find(b => b.textContent === 'Remove').click()"
                );
                await page.WaitAsync($"!{Card("EMPTY")}");
                tasks.Blueprints(Token).Select(b => b.Definition.Id).Should().Equal("BP-001");

                tasks.Create(
                    new TaskDefinition
                    {
                        Id = "UI-0001",
                        Title = "In the blueprint",
                        Blueprint = "BP-001",
                    },
                    "",
                    new("test", "test", "fixture"),
                    Token
                );
                await page.EvaluateAsync("location.reload()");
                await page.WaitAsync(
                    $"[...({Card("BP-001")}?.querySelectorAll('button') || [])].some(b => b.textContent === 'Remove…')"
                );
                await page.EvaluateAsync(
                    $"[...{Card("BP-001")}.querySelectorAll('button')].find(b => b.textContent === 'Remove…').click()"
                );
                await page.WaitAsync("!!document.querySelector('select[aria-label=\"Move to\"]')");
                (await ValueAsync(page, "document.querySelector('.aform .hint').textContent"))
                    .Should()
                    .Contain("1 task(s)");
                await page.EvaluateAsync(Click("Remove the blueprint"));
                await page.WaitAsync($"!{Card("BP-001")}");
                tasks.Get("UI-0001", Token).Definition.Blueprint.Should().BeEmpty("moved to no blueprint");
                tasks.Blueprints(Token).Should().BeEmpty();
            }
        );
    }

    [Fact]
    public async Task ProposedBlueprintIsCreatedFromTheInbox()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        var proposal = tasks.ProposeBlueprint("", new("", "Agent idea", "Its goal"), "seen", "codex", Token);
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                var id = UiWriteTests.ProjectId(fixture);
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + id + "/work/inbox" }
                );
                await page.WaitAsync($"!!document.querySelector('[data-proposal=\"{proposal.Id}\"]')");
                (
                    await ValueAsync(
                        page,
                        "[...document.querySelectorAll('h3.inboxhead')].some(x => x.textContent === 'Blueprints')"
                    )
                )
                    .Should()
                    .Be("True");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the blueprint…').click()"
                );
                await page.WaitAsync("document.querySelector('input[aria-label=Id]')?.value === 'BP-001'");
                (await ValueAsync(page, "document.querySelector('input[aria-label=Name]').value"))
                    .Should()
                    .Be("Agent idea");
                await page.EvaluateAsync(
                    "[...document.querySelectorAll('button')].find(b => b.textContent === 'Create the blueprint').click()"
                );
                await ProviderFixture.EventuallyAsync(() =>
                    tasks.GetBlueprintProposal(proposal.Id, Token).Status is "approved"
                );
                tasks.Blueprints(Token).Single().Definition.Title.Should().Be("Agent idea");
            }
        );
    }

    [Fact]
    public async Task ComplexityGroupShowsItsWorstLevelFiltersByLevelAndDrawsTheDistribution()
    {
        var executable = Browser();
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "example.csproj",
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        );
        // One problem, one warning and eight notes: ten findings, more than the eight shown at first.
        fixture.Write(
            "Router.cs",
            "namespace Example;\npublic class Router\n{\n"
                + string.Concat(
                    new[] { ("Huge", 60), ("Large", 30) }
                        .Concat(Enumerable.Range(11, 8).Select(value => ($"Note{value}", value)))
                        .Select(method =>
                            $"    public int {method.Item1}(int a)\n    {{\n"
                            + string.Concat(Enumerable.Repeat("        if (a > 0) { a--; }\n", method.Item2 - 1))
                            + "        return a;\n    }\n"
                        )
                )
                + "}\n"
        );
        await fixture.CommitAsync("complex methods");
        await fixture.RunAsync();
        await BrowseAsync(
            executable,
            fixture,
            async (page, server) =>
            {
                const string Group =
                    "[...document.querySelectorAll('li.smell > details')].find(d => d.querySelector('summary').textContent.includes('paths through them'))";
                const string List = Group + ".querySelector('.smellsub')";
                await page.CommandAsync(
                    "Page.navigate",
                    new { url = server.Client.BaseAddress + "#/p/" + UiWriteTests.ProjectId(fixture) + "/architecture" }
                );
                await page.WaitAsync($"!!({Group})");
                (await ValueAsync(page, $"{Group}.querySelector('summary .pill').textContent"))
                    .Should()
                    .Be("✕problem", "the pill shows the worst level in the group");
                (await ValueAsync(page, $"{Group}.querySelector('summary').textContent")).Should().Contain("10 × ");
                await page.EvaluateAsync($"{Group}.querySelector('summary').click()");
                (await ValueAsync(page, $"{List}.querySelectorAll(':scope > li > details > summary').length"))
                    .Should()
                    .Be("9", "eight findings and the fold of the rest");
                (await ValueAsync(page, $"{List}.textContent")).Should().Contain("2 more, worth less");
                (await ValueAsync(page, $"{List}.querySelector('li summary').textContent"))
                    .Should()
                    .StartWith("Router.Huge has cyclomatic complexity 60");
                (
                    await ValueAsync(
                        page,
                        $"[...{Group}.querySelectorAll('.lvfilter .chip')].map(c => c.dataset.level + ':' + c.querySelector('.count').textContent).join()"
                    )
                )
                    .Should()
                    .Be("problem:1,warn:1,info:8");

                await page.EvaluateAsync($"{Group}.querySelector('.lvfilter .chip[data-level=info]').click()");
                (await ValueAsync(page, $"{List}.querySelectorAll(':scope > li').length")).Should().Be("2");
                (await ValueAsync(page, $"{Group}.open")).Should().Be("True", "filtering redraws only the list");

                await page.CommandAsync("Page.reload", new { ignoreCache = true });
                await page.WaitAsync($"!!({Group})");
                await page.EvaluateAsync($"{Group}.querySelector('summary').click()");
                (
                    await ValueAsync(
                        page,
                        $"{Group}.querySelector('.chip[data-level=info]').getAttribute('aria-pressed')"
                    )
                )
                    .Should()
                    .Be("false", "the viewer's choice is kept");
                (await ValueAsync(page, $"{List}.querySelectorAll(':scope > li').length")).Should().Be("2");

                await page.WaitAsync("!!document.querySelector('.cxchart')");
                (await ValueAsync(page, "document.querySelectorAll('.cxchart .cxth').length")).Should().Be("3");
                (await ValueAsync(page, "document.querySelector('.cxlang').textContent"))
                    .Should()
                    .Contain("10 functions; at these thresholds 10 reported");
            }
        );
    }

    private static async Task<string> ValueAsync(BrowserPage page, string expression) =>
        (await page.EvaluateAsync(expression)).GetProperty("result").GetProperty("value").ToString();

    private static string Browser()
    {
        var executable = Environment.GetEnvironmentVariable("HARNESS_UI_BROWSER");
        return string.IsNullOrEmpty(executable) || !File.Exists(executable)
            ? throw Xunit.Sdk.SkipException.ForSkip(
                "Set HARNESS_UI_BROWSER to a Chrome or Edge executable for headless browser review checks."
            )
            : executable;
    }

    /// <summary>Serves the fixture's UI, opens it in a headless browser and runs the checks on its page.</summary>
    private static async Task BrowseAsync(
        string executable,
        SyncFixture fixture,
        Func<BrowserPage, UiFixture, Task> checks
    )
    {
        await using var server = await UiFixture.StartAsync(fixture);
        var profile = Path.Combine(fixture.DirectoryRoot, "browser");
        Directory.CreateDirectory(profile);
        using var browser = new Process
        {
            StartInfo = new(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            },
        };
        foreach (
            var argument in new[]
            {
                "--headless=new",
                "--disable-gpu",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-background-networking",
                "--remote-debugging-port=0",
                "--user-data-dir=" + profile,
                "about:blank",
            }
        )
        {
            browser.StartInfo.ArgumentList.Add(argument);
        }
        browser.Start();
        var errors = browser.StandardError.ReadToEndAsync(Token);
        var output = browser.StandardOutput.ReadToEndAsync(Token);
        try
        {
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            await ProviderFixture.EventuallyAsync(() => File.Exists(portFile));
            var port = (await File.ReadAllLinesAsync(portFile, Token))[0];
            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var client = new HttpClient(handler, disposeHandler: false);
            using var targets = JsonDocument.Parse(
                await client.GetStringAsync("http://127.0.0.1:" + port + "/json/list", Token)
            );
            var target = targets.RootElement.EnumerateArray().First(t => t.GetProperty("type").GetString() is "page");
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new(target.GetProperty("webSocketDebuggerUrl").GetString()!), Token);
            await checks(new BrowserPage(socket), server);
        }
        finally
        {
            if (!browser.HasExited)
            {
                browser.Kill(entireProcessTree: true);
            }
            await browser.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(errors, output);
        }
    }

    private sealed class BrowserPage(ClientWebSocket socket)
    {
        private int _id;

        internal async Task<JsonElement> CommandAsync(string method, object parameters)
        {
            var id = ++_id;
            await socket.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        id,
                        method,
                        @params = parameters,
                    }
                ),
                WebSocketMessageType.Text,
                true,
                Token
            );
            var buffer = new byte[8192];
            while (true)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult read;
                do
                {
                    read = await socket.ReceiveAsync(buffer.AsMemory(), Token);
                    await message.WriteAsync(buffer.AsMemory(0, read.Count), Token);
                } while (!read.EndOfMessage);
                using var response = JsonDocument.Parse(message.ToArray());
                if (response.RootElement.TryGetProperty("id", out var returned) && returned.GetInt32() == id)
                {
                    response.RootElement.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
                    return response.RootElement.GetProperty("result").Clone();
                }
            }
        }

        internal async Task<JsonElement> EvaluateAsync(string expression) =>
            await CommandAsync(
                "Runtime.evaluate",
                new
                {
                    expression,
                    returnByValue = true,
                    awaitPromise = true,
                }
            );

        internal async Task WaitAsync(string expression)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            while (
                !(await EvaluateAsync(expression)).GetProperty("result").TryGetProperty("value", out var value)
                || value.ValueKind is not JsonValueKind.True
            )
            {
                await Task.Delay(100, timeout.Token);
            }
        }
    }
}
