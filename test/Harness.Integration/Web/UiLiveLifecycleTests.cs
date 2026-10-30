using System.Text.Json.Nodes;
using System.Threading.Channels;
using AwesomeAssertions;
using Harness.Core.Web;
using Harness.Web;
using Xunit;

namespace Harness.Integration.Web;

public sealed class UiLiveLifecycleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PagesShareOneWatcherAndLastDisconnectDisposesItBeforeReconnect(bool delaySourceCreation)
    {
        using var creationAllowed = new ManualResetEventSlim(!delaySourceCreation);
        var created = Channel.CreateUnbounded<Source>();
        List<Source> sources = [];
        var gate = new object();
        Source Create()
        {
            creationAllowed.Wait(Token);
            var source = new Source();
            lock (gate)
            {
                sources.Add(source);
            }
            created.Writer.TryWrite(source);
            return source;
        }
        await using var app = UiHost.Create("127.0.0.1:0", _ => new Reads(), changes: Create);
        await app.StartAsync(Token);
        try
        {
            // A disposed stream closes its connection at once, as a browser closing the page does: by default the
            // handler would first drain the never-ending stream for 2 s, spending most of the waits below.
            using var handler = new SocketsHttpHandler { UseProxy = false, MaxResponseDrainSize = 0 };
            using var client = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new(UiHost.Address(app)),
            };
            using var first = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, Token);
            using var second = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, Token);
            using var firstReader = new StreamReader(await first.Content.ReadAsStreamAsync(Token));
            using var secondReader = new StreamReader(await second.Content.ReadAsStreamAsync(Token));
            await UiLiveTests.FrameAsync(firstReader);
            await UiLiveTests.FrameAsync(secondReader);
            if (delaySourceCreation)
            {
                lock (gate)
                {
                    sources.Should().BeEmpty("the initial SSE frames must not wait for the watcher factory");
                }
            }
            creationAllowed.Set();
            var original = await NextSourceAsync(created.Reader);
            lock (gate)
            {
                sources.Should().ContainSingle().Which.Should().BeSameAs(original);
            }
            first.Dispose();
            original.Disposed.Task.IsCompleted.Should().BeFalse("the other page still needs live updates");
            second.Dispose();
            await original.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            using var reconnected = await client.GetAsync(
                "/api/events",
                HttpCompletionOption.ResponseHeadersRead,
                Token
            );
            using var reader = new StreamReader(await reconnected.Content.ReadAsStreamAsync(Token));
            (await UiLiveTests.FrameAsync(reader)).Should().Contain("retry: 3000");
            var replacement = await NextSourceAsync(created.Reader);
            lock (gate)
            {
                sources.Should().HaveCount(2);
                sources[1].Should().BeSameAs(replacement);
            }
            reconnected.Dispose();
            await replacement.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        finally
        {
            // Unblock a scheduled factory even if HTTP setup or an assertion fails.
            creationAllowed.Set();
        }
        await app.StopAsync(Token);
    }

    private static async Task<Source> NextSourceAsync(ChannelReader<Source> created)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var source = await created.ReadAsync(timeout.Token);
        await source.Started.Task.WaitAsync(timeout.Token);
        return source;
    }

    private sealed class Reads : IUiReadApi
    {
        public Task<JsonNode?> ReadAsync(UiReadRequest request, CancellationToken token) =>
            Task.FromResult<JsonNode?>(null);
    }

    private sealed class Source : IUiChangeSource
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JsonObject> PollAsync(CancellationToken token)
        {
            Started.TrySetResult();
            return Task.FromResult(new JsonObject());
        }

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
