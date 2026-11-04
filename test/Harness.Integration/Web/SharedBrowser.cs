using System.Net.WebSockets;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Infrastructure.Processes;
using Harness.Integration.Providers;
using Xunit;

[assembly: AssemblyFixture(typeof(Harness.Integration.Web.SharedBrowser))]

namespace Harness.Integration.Web;

/// <summary>
/// One headless browser for the whole test run, started by the first browser test that asks for it. Each page it
/// opens lives in its own CDP browser context (Target.createBrowserContext), so no storage, cookie, cache or
/// localStorage entry reaches another page; disposing the page disposes its context. A browser that died or stopped
/// answering is replaced before the next page. It runs in a kill-on-close job, so it ends with the run however the
/// run ends; at the end it is asked to close with Browser.close and its profile is deleted best-effort.
/// </summary>
public sealed class SharedBrowser : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SocketsHttpHandler _handler = new() { UseProxy = false };
    private readonly HttpClient _client;
    private Edge? _edge;

    public SharedBrowser() => _client = new HttpClient(_handler, disposeHandler: false);

    /// <summary>A new page in a new, empty browser context of the shared browser.</summary>
    internal async Task<IsolatedPage> OpenAsync(string executable, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            try
            {
                return await (await RunningAsync(executable, token)).OpenPageAsync(this, token);
            }
            catch (Exception error) when (IsBrowserFault(error) && !token.IsCancellationRequested)
            {
                // it answered the health check but not the page: start a fresh one and try once more
                await StopAsync();
                return await (await RunningAsync(executable, token)).OpenPageAsync(this, token);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the shared browser's process tree at once, as a crash would (for the restart test).</summary>
    internal async Task CrashAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_edge is { } edge)
            {
                edge.Process.KillTree();
                await edge.Process.Completion.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The id of the shared browser's main process, or null when none is running.</summary>
    internal int? ProcessId => _edge?.Process.Id;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await StopAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _client.Dispose();
            _handler.Dispose();
        }
    }

    private async Task DisposeContextAsync(Edge edge, string context)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (ReferenceEquals(edge, _edge) && !edge.Process.Completion.IsCompleted)
            {
                await edge
                    .Control.CommandUntilAsync(
                        "Target.disposeBrowserContext",
                        new { browserContextId = context },
                        default
                    )
                    .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        }
        catch (Exception error) when (IsBrowserFault(error))
        {
            // a browser that cannot dispose a context is no longer trusted: the next page gets a new one
            await StopAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The running browser, after a health check; a dead or unanswering one is replaced.</summary>
    private async Task<Edge> RunningAsync(string executable, CancellationToken token)
    {
        if (_edge is { } current)
        {
            if (!current.Process.Completion.IsCompleted && await current.AnswersAsync(_client))
            {
                return current;
            }

            await StopAsync();
        }

        _edge = await Edge.StartAsync(executable, Edge.NewProfile(), _client, token);
        return _edge;
    }

    private async Task StopAsync()
    {
        if (_edge is not { } edge)
        {
            return;
        }

        _edge = null;
        await edge.DisposeAsync();
    }

    private static bool IsBrowserFault(Exception error) =>
        error
            is WebSocketException
                or HttpRequestException
                or TimeoutException
                or IOException
                or JsonException
                or InvalidOperationException
                or OperationCanceledException;

    /// <summary>A page of the shared browser in its own browser context; disposing it disposes the context.</summary>
    internal sealed class IsolatedPage(SharedBrowser owner, Edge edge, string context, ClientWebSocket socket)
        : IAsyncDisposable
    {
        internal BrowserPage Page { get; } = new(socket);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State is WebSocketState.Open)
                {
                    await socket
                        .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
            }
            catch (Exception error) when (IsBrowserFault(error))
            {
                // The context's disposal ends the page anyway.
            }
            finally
            {
                socket.Dispose();
                await owner.DisposeContextAsync(edge, context);
            }
        }
    }

    /// <summary>
    /// A headless Edge (or Chrome) process with its own profile folder, in a kill-on-close job (a process group
    /// elsewhere), and a CDP connection to the browser itself.
    /// </summary>
    internal sealed class Edge : IAsyncDisposable
    {
        internal const string ProfilePrefix = "harness-edge-";

        private readonly Task _drained;

        private Edge(IProviderProcess process, Task drained, string profile, string port)
        {
            Process = process;
            _drained = drained;
            Profile = profile;
            Port = port;
        }

        internal IProviderProcess Process { get; }
        internal string Profile { get; }
        internal string Port { get; private set; }
        internal BrowserPage Control { get; private set; } = null!;
        private ClientWebSocket? _control;

        /// <summary>A new profile folder apart from any fixture, so a slow helper exit cannot fail its cleanup.</summary>
        internal static string NewProfile() =>
            Path.Combine(Path.GetTempPath(), ProfilePrefix + Guid.NewGuid().ToString("N"));

        internal static async Task<Edge> StartAsync(
            string executable,
            string profile,
            HttpClient client,
            CancellationToken token
        )
        {
            SweepProfiles();
            Directory.CreateDirectory(profile);
            var process = new ProviderProcessFactory().Start(
                new(
                    executable,
                    [
                        "--headless=new",
                        "--disable-gpu",
                        "--no-first-run",
                        "--no-default-browser-check",
                        "--disable-background-networking",
                        // pages of one test may run side by side: none of them is throttled as a background tab
                        "--disable-background-timer-throttling",
                        "--disable-renderer-backgrounding",
                        "--disable-backgrounding-occluded-windows",
                        "--remote-debugging-port=0",
                        "--user-data-dir=" + profile,
                        "about:blank",
                    ]
                )
            );
            // the browser's own output is not read, only drained so it never blocks on a full pipe
            var drained = Task.WhenAll(
                process.Output.CopyToAsync(Stream.Null, CancellationToken.None),
                process.Error.CopyToAsync(Stream.Null, CancellationToken.None)
            );
            var edge = new Edge(process, drained, profile, "");
            try
            {
                // the file is written in two lines, port and path: one read too early sees it empty
                var portFile = Path.Combine(profile, "DevToolsActivePort");
                await ProviderFixture.EventuallyAsync(() => ReadPort(portFile) is not null, TimeSpan.FromSeconds(30));
                edge.Port = ReadPort(portFile)!;
                using var version = JsonDocument.Parse(
                    await client.GetStringAsync("http://127.0.0.1:" + edge.Port + "/json/version", token)
                );
                edge._control = new ClientWebSocket();
                await edge._control.ConnectAsync(
                    new(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!),
                    token
                );
                edge.Control = new BrowserPage(edge._control);
                return edge;
            }
            catch
            {
                await edge.DisposeAsync();
                throw;
            }
        }

        /// <summary>The browser's first page, for a browser started for one test.</summary>
        internal async Task<ClientWebSocket> FirstPageAsync(HttpClient client, CancellationToken token)
        {
            using var targets = JsonDocument.Parse(
                await client.GetStringAsync("http://127.0.0.1:" + Port + "/json/list", token)
            );
            var target = targets.RootElement.EnumerateArray().First(t => t.GetProperty("type").GetString() is "page");
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new(target.GetProperty("webSocketDebuggerUrl").GetString()!), token);
            return socket;
        }

        internal async Task<IsolatedPage> OpenPageAsync(SharedBrowser owner, CancellationToken token)
        {
            var created = await Control
                .CommandUntilAsync("Target.createBrowserContext", new { disposeOnDetach = false }, token)
                .WaitAsync(TimeSpan.FromSeconds(10), token);
            var context = created.GetProperty("browserContextId").GetString()!;
            var target = await Control
                .CommandUntilAsync(
                    "Target.createTarget",
                    new { url = "about:blank", browserContextId = context },
                    token
                )
                .WaitAsync(TimeSpan.FromSeconds(10), token);
            var socket = new ClientWebSocket();
            await socket
                .ConnectAsync(
                    new($"ws://127.0.0.1:{Port}/devtools/page/{target.GetProperty("targetId").GetString()}"),
                    token
                )
                .WaitAsync(TimeSpan.FromSeconds(10), token);
            var page = new IsolatedPage(owner, this, context, socket);
            // a page that is not the focused window still behaves as the one in front
            await page.Page.CommandUntilAsync("Emulation.setFocusEmulationEnabled", new { enabled = true }, token);
            return page;
        }

        /// <summary>Whether the browser still answers its DevTools endpoint within a few seconds.</summary>
        internal async Task<bool> AnswersAsync(HttpClient client)
        {
            if (_control is not { State: WebSocketState.Open })
            {
                return false;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.GetStringAsync("http://127.0.0.1:" + Port + "/json/version", timeout.Token);
                return true;
            }
            catch (Exception error) when (IsBrowserFault(error))
            {
                return false;
            }
        }

        /// <summary>
        /// Asks the browser to close as a user would, ends whatever of it is left with its job and deletes its
        /// profile best-effort (Edge helpers can outlive the job's end for a moment and hold its files).
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Port.Length > 0 && !Process.Completion.IsCompleted)
            {
                try
                {
                    if (_control is { State: WebSocketState.Open })
                    {
                        await Control.SendAsync("Browser.close");
                    }

                    await Process.Completion.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                }
                catch (Exception error) when (IsBrowserFault(error))
                {
                    // It did not close by itself: its job ends it.
                }
            }

            _control?.Dispose();
            // ending the job ends the browser and every helper still running
            await Process.DisposeAsync();
            try
            {
                await _drained;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The pipes closed with the processes.
            }

            await DeleteProfileAsync(Profile);
        }

        private static string? ReadPort(string portFile)
        {
            try
            {
                var lines = File.ReadAllLines(portFile);
                return lines.Length >= 2 && lines[0].Length > 0 ? lines[0] : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null; // not there yet, or being written
            }
        }

        /// <summary>
        /// Deletes a browser profile (some 200 MB) once its killed processes let go of its files, a moment after the
        /// job ends; one still held is left for a later sweep.
        /// </summary>
        private static async Task DeleteProfileAsync(string profile)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                try
                {
                    Directory.Delete(profile, recursive: true);
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }
                catch (Exception error)
                    when (error is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(200, CancellationToken.None);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Removes the profiles earlier runs could not delete, so they never pile up. Only ones untouched for 15
        /// minutes go, and a profile in use keeps its files locked, so a running browser's survives.
        /// </summary>
        private static void SweepProfiles()
        {
            var before = DateTime.UtcNow.AddMinutes(-15);
            foreach (
                var directory in new DirectoryInfo(Path.GetTempPath())
                    .EnumerateDirectories(ProfilePrefix + "*")
                    .Where(directory => directory.LastWriteTimeUtc < before)
            )
            {
                try
                {
                    directory.Delete(recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Still in use: the next sweep takes it.
                }
            }
        }
    }
}

/// <summary>A CDP connection to a page (or the browser itself): commands one at a time, events ignored.</summary>
internal sealed class BrowserPage(ClientWebSocket socket)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private int _id;

    /// <summary>Sends a command without waiting for its answer (a closing browser may give none).</summary>
    internal Task SendAsync(string method) =>
        socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(new { id = ++_id, method }),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );

    internal Task<JsonElement> CommandAsync(string method, object parameters) =>
        CommandUntilAsync(method, parameters, Token);

    /// <summary>A command whose wait ends with the given token instead of the test's.</summary>
    internal async Task<JsonElement> CommandUntilAsync(string method, object parameters, CancellationToken token)
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
            token
        );
        var buffer = new byte[8192];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult read;
            do
            {
                read = await socket.ReceiveAsync(buffer.AsMemory(), token);
                if (read.MessageType is WebSocketMessageType.Close)
                {
                    throw new WebSocketException("the browser closed the connection during " + method);
                }

                await message.WriteAsync(buffer.AsMemory(0, read.Count), token);
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
            try
            {
                await Task.Delay(100, timeout.Token);
            }
            catch (OperationCanceledException) when (!Token.IsCancellationRequested)
            {
                // name what the page never showed: a bare cancellation does not say which wait it was
                throw new TimeoutException("the page did not reach " + expression + " in 25 s");
            }
        }
    }
}
