using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Web;
using Harness.Web;
using Xunit;

namespace Harness.Integration.Web;

public sealed class UiLiveLifecycleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PagesShareOneWatcherAndLastDisconnectDisposesItBeforeReconnect()
    {
        List<Source> sources = [];
        var gate = new object();
        Source Create()
        {
            var source = new Source();
            lock (gate)
            { sources.Add(source); }
            return source;
        }
        await using var app = UiHost.Create("127.0.0.1:0", _ => new Reads(), changes: Create);
        await app.StartAsync(Token);
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = new(UiHost.Address(app)) };
        using var first = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, Token);
        using var second = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, Token);
        using var firstReader = new StreamReader(await first.Content.ReadAsStreamAsync(Token));
        using var secondReader = new StreamReader(await second.Content.ReadAsStreamAsync(Token));
        await UiLiveTests.FrameAsync(firstReader);
        await UiLiveTests.FrameAsync(secondReader);
        Source original;
        lock (gate)
        { sources.Should().ContainSingle(); original = sources[0]; }
        await original.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        first.Dispose();
        original.Disposed.Task.IsCompleted.Should().BeFalse("the other page still needs live updates");
        second.Dispose();
        await original.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        using var reconnected = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, Token);
        using var reader = new StreamReader(await reconnected.Content.ReadAsStreamAsync(Token));
        (await UiLiveTests.FrameAsync(reader)).Should().Contain("retry: 3000");
        Source replacement;
        lock (gate)
        { sources.Should().HaveCount(2); replacement = sources[1]; }
        await replacement.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        reconnected.Dispose();
        await replacement.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await app.StopAsync(Token);
    }

    private sealed class Reads : IUiReadApi
    {
        public Task<JsonNode?> ReadAsync(UiReadRequest request, CancellationToken token) => Task.FromResult<JsonNode?>(null);
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
