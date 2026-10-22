using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
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
        var result = await Call("ping", """{}""", session);
        session.Calls.Should().Be(0);
        result.IsError.Should().BeNull();
        var text = result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which;
        text.Text.Should().Be(result.StructuredContent!.Value.GetRawText());
        result.StructuredContent.Value.GetProperty("message").GetString().Should().Be("pong");
    }

    [Theory]
    [InlineData("state_get_test", """{}""", "required: missing properties")]
    [InlineData("state_put_test", """{"key":"x"}""", "required: missing properties")]
    [InlineData("ping", """{"unexpected":true}""", "unexpected additional properties")]
    [InlineData("state_get_test", """{"key":1}""", "want \"string\"")]
    [InlineData("state_get_test", """{"key":null}""", "want \"string\"")]
    [InlineData("state_get_test", """{"key":""}""", "key must not be empty")]
    public async Task InvalidArgumentsAreToolErrorsBeforeStorage(string tool, string arguments, string message)
    {
        await using var session = new FakeSession();
        var result = await Call(tool, arguments, session);
        result.IsError.Should().BeTrue();
        result.StructuredContent.Should().BeNull();
        result
            .Content.Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<TextContentBlock>()
            .Which.Text.Should()
            .Contain(message);
        session.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SchemaErrorsRedactCredentialLikeValuesBeforeReturningDiagnostics()
    {
        await using var session = new FakeSession();
        var result = await Call(
            "memory_put",
            """{"type":"fact","content":"safe statement","force":"API_KEY=synthetic-fixture"}""",
            session
        );
        result.IsError.Should().BeTrue();
        var error = result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which.Text;
        error.Should().Contain("[redacted]");
        error.Should().NotContain("synthetic-fixture");
        session.Calls.Should().Be(0);
    }

    [Fact]
    public async Task LimitsCountKeyRunesAndValueUtf8BytesAndEmptyFoundValueIsOmitted()
    {
        await using var session = new FakeSession();
        var key = string.Concat(Enumerable.Repeat("🧪", 256));
        var value = new string('ž', 32768);
        var result = await Call("state_put_test", JsonSerializer.Serialize(new { key, value }), session);
        result.IsError.Should().BeNull();
        (await Call("state_put_test", JsonSerializer.Serialize(new { key, value = value + "x" }), session))
            .IsError.Should()
            .BeTrue();
        (await Call("state_get_test", JsonSerializer.Serialize(new { key = key + "🧪" }), session))
            .IsError.Should()
            .BeTrue();
        (await Call("state_put_test", """{"key":"empty","value":""}""", session)).IsError.Should().BeNull();
        result = await Call("state_get_test", """{"key":"empty"}""", session);
        result.StructuredContent!.Value.GetProperty("found").GetBoolean().Should().BeTrue();
        result.StructuredContent.Value.TryGetProperty("value", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ToolCancellationReachesTheSession()
    {
        await using var session = new FakeSession { Wait = true };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = HarnessMcpServer
            .CallAsync(
                new CallToolRequestParams { Name = "state_get_test", Arguments = Arguments("""{"key":"wait"}""") },
                session,
                Runtime,
                cancellation.Token
            )
            .AsTask();
        await session.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await FluentActions.Awaiting(() => operation).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [SuppressMessage(
        "Reliability",
        "CA2025:Do not pass disposable objects into unawaited tasks",
        Justification = "The server task is awaited in finally after cancellation, before its input and output streams leave scope."
    )]
    public async Task OfficialSdkCancellationNotificationReachesTheHandlerAndSessionCloses()
    {
        var toServer = new Pipe();
        var fromServer = new Pipe();
        using var input = toServer.Reader.AsStream();
        using var output = fromServer.Writer.AsStream();
        await using var session = new FakeSession { Wait = true };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = HarnessMcpServer.RunAsync(
            input,
            output,
            session,
            Runtime,
            NullLoggerFactory.Instance,
            cancellationToken: lifetime.Token
        );
        try
        {
            await using (
                var client = await McpClient.CreateAsync(
                    new StreamClientTransport(toServer.Writer.AsStream(), fromServer.Reader.AsStream()),
                    new McpClientOptions { ProtocolVersion = "2025-06-18" },
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            {
                var operation = client.SendRequestAsync(
                    new JsonRpcRequest
                    {
                        Id = new RequestId(42),
                        Method = "tools/call",
                        Params = new JsonObject
                        {
                            ["name"] = "state_get_test",
                            ["arguments"] = new JsonObject { ["key"] = "wait" },
                        },
                    },
                    TestContext.Current.CancellationToken
                );
                await session.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
                await client.SendMessageAsync(
                    new JsonRpcNotification
                    {
                        Method = "notifications/cancelled",
                        Params = new JsonObject { ["requestId"] = 42 },
                    },
                    TestContext.Current.CancellationToken
                );
                await FluentActions.Awaiting(() => operation).Should().ThrowAsync<OperationCanceledException>();
                await session.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                (await client.CallToolAsync("ping", cancellationToken: TestContext.Current.CancellationToken))
                    .StructuredContent!.Value.GetProperty("message")
                    .GetString()
                    .Should()
                    .Be("pong");
            }

            await toServer.Writer.CompleteAsync(); // Stream transport leaves caller-owned streams open; this is stdin EOF.
            await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            session.Disposed.Should().BeTrue();
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await running;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            { /* Cleanup after an assertion failure. */
            }
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
        await HarnessMcpServer
            .RunAsync(
                source,
                destination,
                session,
                Runtime,
                NullLoggerFactory.Instance,
                cancellationToken: TestContext.Current.CancellationToken
            )
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        session.Disposed.Should().BeTrue();
        source.CanRead.Should().BeTrue();
        destination.CanWrite.Should().BeTrue();
        var messages = Encoding
            .UTF8.GetString(destination.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToArray();
        messages
            .Where(message => message.GetProperty("id").GetInt32() == 2 && message.TryGetProperty("result", out _))
            .Should()
            .NotBeEmpty();
        messages
            .Where(message => message.GetProperty("id").GetInt32() == 3 && message.TryGetProperty("result", out _))
            .Should()
            .NotBeEmpty();
    }

    [Fact]
    public async Task FramingRejectsMalformedOversizedAndStructuralErrorsAndContinues()
    {
        var input =
            string.Join('\n', "{nope", "42", """{"jsonrpc":"1.0","id":5,"method":"ping"}""", """[]""", "")
            + new string('x', CompatibleStdioTransport.MaximumFrameBytes + 1)
            + "\n";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var destination = new MemoryStream();
        await using (var transport = new CompatibleStdioTransport(source, destination))
        {
            await transport.MessageReader.Completion.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken
            );
        }

        var responses = Encoding
            .UTF8.GetString(destination.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .ToArray();
        responses
            .Select(message => message.GetProperty("error").GetProperty("code").GetInt32())
            .Should()
            .Equal(-32700, -32600, -32600, -32600, -32700);
        responses[2].GetProperty("id").GetInt32().Should().Be(5);
        responses[0].GetProperty("id").Deserialize<int?>().Should().BeNull();
    }

    private static ValueTask<CallToolResult> Call(string tool, string json, FakeSession session) =>
        HarnessMcpServer.CallAsync(
            new CallToolRequestParams { Name = tool, Arguments = Arguments(json) },
            session,
            Runtime,
            TestContext.Current.CancellationToken
        );

    private static Dictionary<string, JsonElement> Arguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private sealed class FakeSession : IHarnessStateSession
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        internal bool Wait { get; init; }
        internal bool Disposed { get; private set; }
        internal int Calls { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyDictionary<string, object?>> DiagnosticsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Cancelled.TrySetResult();
                    }
                }
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
