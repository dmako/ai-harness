using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using Harness.Core.Projects;
using Harness.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Unit;

public sealed class McpTests
{
    private static readonly HarnessRuntime Runtime = new("test", "commit", ".NET test", "windows/amd64");

    [Fact]
    public async Task PingDoesNotTouchSessionAndSuccessHasMatchingStructuredAndTextContent()
    {
        await using var session = new FakeSession();
        var result = await Call("ping", "{}", session);
        Assert.Equal(0, session.Calls);
        Assert.Null(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal(result.StructuredContent!.Value.GetRawText(), text.Text);
        Assert.Equal("pong", result.StructuredContent.Value.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("state_get_test", "{}", "required: missing properties")]
    [InlineData("state_put_test", "{\"key\":\"x\"}", "required: missing properties")]
    [InlineData("ping", "{\"unexpected\":true}", "unexpected additional properties")]
    [InlineData("state_get_test", "{\"key\":1}", "want \"string\"")]
    [InlineData("state_get_test", "{\"key\":null}", "want \"string\"")]
    [InlineData("state_get_test", "{\"key\":\"\"}", "key must not be empty")]
    public async Task InvalidArgumentsAreToolErrorsBeforeStorage(string tool, string arguments, string message)
    {
        await using var session = new FakeSession();
        var result = await Call(tool, arguments, session);
        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.Contains(message, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
        Assert.Equal(0, session.Calls);
    }

    [Fact]
    public async Task SchemaErrorsRedactCredentialLikeValuesBeforeReturningDiagnostics()
    {
        await using var session = new FakeSession();
        var result = await Call("memory_put", "{\"type\":\"fact\",\"content\":\"safe statement\",\"force\":\"API_KEY=synthetic-fixture\"}", session);
        Assert.True(result.IsError);
        var error = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("[redacted]", error, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-fixture", error, StringComparison.Ordinal);
        Assert.Equal(0, session.Calls);
    }

    [Fact]
    public async Task LimitsCountKeyRunesAndValueUtf8BytesAndEmptyFoundValueIsOmitted()
    {
        await using var session = new FakeSession();
        var key = string.Concat(Enumerable.Repeat("🧪", 256));
        var value = new string('ž', 32768);
        var result = await Call("state_put_test", JsonSerializer.Serialize(new { key, value }), session);
        Assert.Null(result.IsError);
        Assert.True((await Call("state_put_test", JsonSerializer.Serialize(new { key, value = value + "x" }), session)).IsError);
        Assert.True((await Call("state_get_test", JsonSerializer.Serialize(new { key = key + "🧪" }), session)).IsError);
        Assert.Null((await Call("state_put_test", "{\"key\":\"empty\",\"value\":\"\"}", session)).IsError);
        result = await Call("state_get_test", "{\"key\":\"empty\"}", session);
        Assert.True(result.StructuredContent!.Value.GetProperty("found").GetBoolean());
        Assert.False(result.StructuredContent.Value.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task ToolCancellationReachesTheSession()
    {
        await using var session = new FakeSession { Wait = true };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = HarnessMcpServer.CallAsync(new CallToolRequestParams { Name = "state_get_test", Arguments = Arguments("{\"key\":\"wait\"}") }, session, Runtime, cancellation.Token).AsTask();
        await session.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2025:Do not pass disposable objects into unawaited tasks", Justification = "The server task is awaited in finally after cancellation, before its input and output streams leave scope.")]
    public async Task OfficialSdkCancellationNotificationReachesTheHandlerAndSessionCloses()
    {
        var toServer = new Pipe();
        var fromServer = new Pipe();
        using var input = toServer.Reader.AsStream();
        using var output = fromServer.Writer.AsStream();
        await using var session = new FakeSession { Wait = true };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = HarnessMcpServer.RunAsync(input, output, session, Runtime, NullLoggerFactory.Instance, cancellationToken: lifetime.Token);
        try
        {
            await using (var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), fromServer.Reader.AsStream()), new McpClientOptions { ProtocolVersion = "2025-06-18" }, cancellationToken: TestContext.Current.CancellationToken))
            {
                var operation = client.SendRequestAsync(new JsonRpcRequest
                {
                    Id = new RequestId(42),
                    Method = "tools/call",
                    Params = new JsonObject { ["name"] = "state_get_test", ["arguments"] = new JsonObject { ["key"] = "wait" } },
                }, TestContext.Current.CancellationToken);
                await session.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
                await client.SendMessageAsync(new JsonRpcNotification
                {
                    Method = "notifications/cancelled",
                    Params = new JsonObject { ["requestId"] = 42 },
                }, TestContext.Current.CancellationToken);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
                await session.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal("pong", (await client.CallToolAsync("ping", cancellationToken: TestContext.Current.CancellationToken)).StructuredContent!.Value.GetProperty("message").GetString());
            }

            await toServer.Writer.CompleteAsync(); // Stream transport leaves caller-owned streams open; this is stdin EOF.
            await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(session.Disposed);
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            { await running; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { /* Cleanup after an assertion failure. */ }
        }
    }

    [Fact]
    public async Task EofDrainsResponsesDisposesSessionAndLeavesCallerStreamsOpen()
    {
        var input = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"unit","version":"1"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"state_put_test","arguments":{"key":"x","value":"saved"}}}
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ping","arguments":{}}}
            """;
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var destination = new MemoryStream();
        await using var session = new FakeSession();
        await HarnessMcpServer.RunAsync(source, destination, session, Runtime, NullLoggerFactory.Instance, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(session.Disposed);
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
        var messages = Encoding.UTF8.GetString(destination.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.Contains(messages, message => message.GetProperty("id").GetInt32() == 2 && message.TryGetProperty("result", out _));
        Assert.Contains(messages, message => message.GetProperty("id").GetInt32() == 3 && message.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task FramingRejectsMalformedOversizedAndStructuralErrorsAndContinues()
    {
        var input = "{nope\n42\n{\"jsonrpc\":\"1.0\",\"id\":5,\"method\":\"ping\"}\n[]\n" + new string('x', CompatibleStdioTransport.MaximumFrameBytes + 1) + "\n";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var destination = new MemoryStream();
        await using (var transport = new CompatibleStdioTransport(source, destination))
        {
            await transport.MessageReader.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        var responses = Encoding.UTF8.GetString(destination.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.Equal([-32700, -32600, -32600, -32600, -32700], responses.Select(message => message.GetProperty("error").GetProperty("code").GetInt32()));
        Assert.Equal(5, responses[2].GetProperty("id").GetInt32());
        Assert.Null(responses[0].GetProperty("id").Deserialize<int?>());
    }

    private static ValueTask<CallToolResult> Call(string tool, string json, FakeSession session) => HarnessMcpServer.CallAsync(
        new CallToolRequestParams { Name = tool, Arguments = Arguments(json) }, session, Runtime, TestContext.Current.CancellationToken);

    private static Dictionary<string, JsonElement> Arguments(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private sealed class FakeSession : IHarnessStateSession
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        internal bool Wait { get; init; }
        internal bool Disposed { get; private set; }
        internal int Calls { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyDictionary<string, object?>> DiagnosticsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PutAsync(string key, string value, CancellationToken cancellationToken)
        {
            Calls++;
            _values[key] = value;
            return Task.CompletedTask;
        }

        public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            if (Wait)
            {
                try
                { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { if (cancellationToken.IsCancellationRequested) Cancelled.TrySetResult(); }
            }
            return _values.GetValueOrDefault(key);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
