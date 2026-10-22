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
    public async Task FrozenFrontendRequiresMemoryConflictChoiceAndTaskConfirmationInARealBrowser()
    {
        var executable = Environment.GetEnvironmentVariable("HARNESS_UI_BROWSER");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Set HARNESS_UI_BROWSER to a Chrome or Edge executable for headless browser review checks."
            );
        }
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
            var page = new BrowserPage(socket);
            var id = UiWriteTests.ProjectId(fixture);
            await page.CommandAsync(
                "Page.navigate",
                new { url = server.Client.BaseAddress + "#/p/" + id + "/knowledge/inbox" }
            );
            await page.WaitAsync("[...document.querySelectorAll('button')].some(b => b.textContent === 'Approve')");
            await page.EvaluateAsync(
                "[...document.querySelectorAll('button')].find(b => b.textContent === 'Approve').click()"
            );
            await page.WaitAsync("document.body.textContent.includes('Replace one of them with it, or keep both:')");
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
                  if (window.holdReviewRead && url.includes('/milestones')) {
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
            await ProviderFixture.EventuallyAsync(() => tasks.GetProposal(taskProposal.Id, Token).Status is "approved");
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
            await page.WaitAsync("!!document.querySelector('.live.off') && !document.getElementById('refresh').hidden");
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
