using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Infrastructure.Processes;
using Harness.Providers.Mcp;
using Xunit;

namespace Harness.Integration.Providers;

[Collection("Provider processes")]
public sealed class McpProcessTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HandshakeDiscoveryArgumentsEnvironmentDirectoryAndStderrAreIndependent()
    {
        using var fixture = new ProviderFixture();
        var literal = "space \"quote\" trailing\\ ; & $(not-a-command) 🧪";
        var options = fixture.Options("stderr") with
        {
            StderrLimit = 512,
            Process = fixture.Options("stderr").Process with
            {
                Arguments = [ProviderFixture.Worker, "stderr", fixture.Root, literal],
                WorkingDirectory = fixture.Root,
                Environment = new Dictionary<string, string?>
                {
                    ["HARNESS_PROVIDER_TEST"] = literal,
                    ["HARNESS_PROVIDER_STDERR_FIXTURE"] = "password='synthetic-fixture'",
                },
            },
        };
        await using var client = await McpProcessClient.StartAsync(new ProviderProcessFactory(), options, Token);
        client.Alive.Should().BeTrue();
        client.ServerVersion.Should().Be("provider-fixture 1.0");
        var tools = await client.ListToolsAsync(Token);
        tools.Should().Contain(tool => tool.Name == "echo");
        var result = await client.CallToolAsync(
            "echo",
            new Dictionary<string, object?> { ["literal"] = literal, ["count"] = 7 },
            Token
        );
        var body = Body(result);
        body.GetProperty("arguments").GetProperty("literal").GetString().Should().Be(literal);
        body.GetProperty("arguments").GetProperty("count").GetInt32().Should().Be(7);
        body.GetProperty("cwd")
            .GetString()
            .Should()
            .Be(Harness.Infrastructure.Graphify.GraphifyDiscovery.Canonical(fixture.Root));
        body.GetProperty("environment").GetString().Should().Be(literal);
        body.GetProperty("process_arguments")[2].GetString().Should().Be(literal);
        var tail = client.StderrTail();
        tail.Should().Contain("last-line").And.Contain("[redacted]").And.NotContain("synthetic-fixture");
        Encoding.UTF8.GetByteCount(tail).Should().BeLessThanOrEqualTo(512);
    }

    [Fact]
    public async Task DiscoveryTraversesPagesAndRejectsRepeatedCursors()
    {
        using var fixture = new ProviderFixture();
        fixture.Write(
            "tools-list.page",
            """{"tools":[{"name":"first","inputSchema":{"type":"object"}}],"nextCursor":"two"}"""
        );
        fixture.Write("tools-list-next.page", """{"tools":[{"name":"second","inputSchema":{"type":"object"}}]}""");
        await using var client = await McpProcessClient.StartAsync(
            new ProviderProcessFactory(),
            fixture.Options(),
            Token
        );
        (await client.ListToolsAsync(Token)).Select(tool => tool.Name).Should().Equal("first", "second");
        fixture.Write("tools-list-next.page", """{"tools":[],"nextCursor":"two"}""");
        var error = (
            await FluentActions.Awaiting(() => client.ListToolsAsync(Token)).Should().ThrowAsync<IOException>()
        ).Which;
        error.Message.Should().Contain("pagination limits");
    }

    [Theory]
    [InlineData(150)]
    [InlineData(800)]
    public async Task ConfiguredDeadlineWinsOverLongParentAndTerminatesStalledProcess(int milliseconds)
    {
        using var fixture = new ProviderFixture();
        await using var client = await McpProcessClient.StartAsync(
            new ProviderProcessFactory(),
            fixture.Options() with
            {
                CallTimeout = TimeSpan.FromMilliseconds(milliseconds),
            },
            Token
        );
        using var parent = CancellationTokenSource.CreateLinkedTokenSource(Token);
        parent.CancelAfter(TimeSpan.FromSeconds(20));
        var elapsed = Stopwatch.StartNew();
        await FluentActions
            .Awaiting(() => client.CallToolAsync("hang", new Dictionary<string, object?>(), parent.Token))
            .Should()
            .ThrowAsync<TimeoutException>();
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        client.Alive.Should().BeFalse();
        await ProviderFixture.GoneAsync(client.Id);
    }

    [Fact]
    public async Task CallerCancellationKeepsSessionUsableAndDoesNotBecomeTimeout()
    {
        using var fixture = new ProviderFixture();
        await using var client = await McpProcessClient.StartAsync(
            new ProviderProcessFactory(),
            fixture.Options(),
            Token
        );
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var operation = client.CallToolAsync("hang", new Dictionary<string, object?>(), cancelled.Token);
        await fixture.WaitForFileAsync("hang.args");
        await cancelled.CancelAsync();
        await FluentActions.Awaiting(() => operation).Should().ThrowAsync<OperationCanceledException>();
        client.Alive.Should().BeTrue();
        (await client.CallToolAsync("echo", new Dictionary<string, object?>(), Token)).IsError.Should().NotBe(true);
    }

    [Theory]
    [InlineData("crash")]
    [InlineData("oversized")]
    public async Task BrokenOrOversizedOutputClosesSessionAndCleansUp(string tool)
    {
        using var fixture = new ProviderFixture();
        await using var client = await McpProcessClient.StartAsync(
            new ProviderProcessFactory(),
            fixture.Options() with
            {
                MaximumFrameBytes = 4096,
            },
            Token
        );
        await FluentActions
            .Awaiting(() => client.CallToolAsync(tool, new Dictionary<string, object?>(), Token))
            .Should()
            .ThrowAsync<IOException>();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await client.DisposeAsync();
        await ProviderFixture.GoneAsync(client.Id);
    }

    [Theory]
    [InlineData("fail-start")]
    [InlineData("stall-start")]
    public async Task HandshakeFailuresAreBoundedAndReapTheChild(string mode)
    {
        using var fixture = new ProviderFixture();
        var factory = new CountingFactory();
        var options = fixture.Options(mode) with { StartTimeout = TimeSpan.FromSeconds(1) };
        await FluentActions
            .Awaiting(() => McpProcessClient.StartAsync(factory, options, Token))
            .Should()
            .ThrowAsync<Exception>();
        factory.Ids.Should().ContainSingle();
        await ProviderFixture.GoneAsync(factory.Ids.Single());
    }

    [Theory]
    [InlineData("spawn")]
    [InlineData("exit-after-spawn")]
    public async Task ShutdownReapsGrandchildrenEvenAfterTheirParentExits(string tool)
    {
        using var fixture = new ProviderFixture();
        await using var client = await McpProcessClient.StartAsync(
            new ProviderProcessFactory(),
            fixture.Options(),
            Token
        );
        var pidFile = Path.Combine(fixture.Root, "child.pid");
        await client.CallToolAsync(tool, new Dictionary<string, object?> { ["pid_file"] = pidFile }, Token);
        var child = int.Parse(await File.ReadAllTextAsync(pidFile, Token), CultureInfo.InvariantCulture);
        await client.DisposeAsync();
        await client.DisposeAsync();
        await ProviderFixture.GoneAsync(client.Id);
        await ProviderFixture.GoneAsync(child);
    }

    [Fact]
    public async Task ImmediateExitDuringHandshakeCannotEscapeContainment()
    {
        using var fixture = new ProviderFixture();
        var pidFile = Path.Combine(fixture.Root, "early.pid");
        var options = fixture.Options("startup-spawn-exit") with
        {
            Process = fixture.Options().Process with
            {
                Arguments = [ProviderFixture.Worker, "startup-spawn-exit", pidFile],
            },
        };
        await FluentActions
            .Awaiting(() => McpProcessClient.StartAsync(new ProviderProcessFactory(), options, Token))
            .Should()
            .ThrowAsync<IOException>();
        var child = int.Parse(await File.ReadAllTextAsync(pidFile, Token), CultureInfo.InvariantCulture);
        await ProviderFixture.GoneAsync(child);
    }

    [Fact]
    public async Task IgnoredEofIsKilledWithinShutdownBudget()
    {
        using var fixture = new ProviderFixture();
        await using var client = await McpProcessClient.StartAsync(
            new ProviderProcessFactory(),
            fixture.Options("ignore-eof"),
            Token
        );
        var elapsed = Stopwatch.StartNew();
        await client.DisposeAsync();
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
        await ProviderFixture.GoneAsync(client.Id);
    }

    [Fact]
    public async Task ToolErrorsRemainResultsAndRpcErrorsRemainExceptions()
    {
        using var fixture = new ProviderFixture();
        fixture.Write("failure.json", """{"error":"index missing","hint":"index first"}""");
        fixture.Write("failure.error", "");
        fixture.Write("rpc.protocol-error", "unknown symbol");
        await using var client = await McpProcessClient.StartAsync(
            new ProviderProcessFactory(),
            fixture.Options(),
            Token
        );
        var result = await client.CallToolAsync("failure", new Dictionary<string, object?>(), Token);
        result.IsError.Should().BeTrue();
        Body(result).GetProperty("error").GetString().Should().Be("index missing");
        await FluentActions
            .Awaiting(() => client.CallToolAsync("rpc", new Dictionary<string, object?>(), Token))
            .Should()
            .ThrowAsync<IOException>()
            .WithMessage("*unknown symbol*");
        client.Alive.Should().BeTrue();
    }

    [Fact]
    public async Task SlowNativeLaunchIsBoundedAndItsLateProcessIsReaped()
    {
        using var fixture = new ProviderFixture();
        using var release = new ManualResetEventSlim();
        var actual = new CountingFactory();
        var factory = new DelayedFactory(release, actual);
        try
        {
            await FluentActions
                .Awaiting(() =>
                    McpProcessClient.StartAsync(
                        factory,
                        fixture.Options() with
                        {
                            StartTimeout = TimeSpan.FromMilliseconds(100),
                        },
                        Token
                    )
                )
                .Should()
                .ThrowAsync<TimeoutException>();
        }
        finally
        {
            release.Set();
        }
        await ProviderFixture.EventuallyAsync(() => actual.Ids.Count is 1);
        await ProviderFixture.GoneAsync(actual.Ids.Single());
    }

    private sealed class DelayedFactory(ManualResetEventSlim release, IProviderProcessFactory actual)
        : IProviderProcessFactory
    {
        public IProviderProcess Start(ProviderProcessOptions options)
        {
            release.Wait();
            return actual.Start(options);
        }
    }

    internal static JsonElement Body(ModelContextProtocol.Protocol.CallToolResult result) =>
        JsonSerializer.Deserialize<JsonElement>(
            string.Concat(
                result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(block => block.Text)
            )
        );
}

internal sealed class CountingFactory : IProviderProcessFactory
{
    private readonly System.Collections.Concurrent.ConcurrentBag<int> _ids = [];
    public IReadOnlyList<int> Ids => _ids.ToArray();

    public IProviderProcess Start(ProviderProcessOptions options)
    {
        var process = new ProviderProcessFactory().Start(options);
        _ids.Add(process.Id);
        return process;
    }
}

internal sealed class ProviderFixture : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("harness-provider-").FullName;
    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
            {
                directory = directory.Parent;
            }

            directory.Should().NotBeNull();
            return directory.FullName;
        }
    }
    public static string Worker =>
        Path.Combine(
            RepositoryRoot,
            "test",
            "Harness.ProviderWorker",
            "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0",
            "Harness.ProviderWorker.dll"
        );

    public McpProcessOptions Options(string mode = "normal") =>
        new("fixture", new("dotnet", [Worker, mode, Root])) { CallTimeout = TimeSpan.FromSeconds(5) };

    public void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(Root, name), content, new UTF8Encoding(false));

    public async Task WaitForFileAsync(string name)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(Root, name)))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public static async Task GoneAsync(int pid)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(timeout.Token);
            process.HasExited.Should().BeTrue();
        }
        catch (ArgumentException)
        { /* The OS has already reaped it. */
        }
    }

    public static async Task EventuallyAsync(Func<bool> condition, TimeSpan? wait = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(wait ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public void Dispose() => TestDirectory.Delete(Root);
}
