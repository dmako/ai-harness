using System.Globalization;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Agents;
using Harness.Core.Agents;
using Harness.Core.Tasks;
using Harness.Infrastructure.Agents;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Providers;
using Xunit;

namespace Harness.Integration.Agents;

/// <summary>
/// harness-agents serve (TASK-0116) against scripted interactive clients: Claude Code with stream-json input and
/// stdio permission prompts, and codex app-server. Everything goes through the host's named pipe, as harness ui does.
/// </summary>
public sealed class AgentServerTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("harness-serve-").FullName;
    private string State => Path.Combine(_root, "agents");

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task ASessionTakesTurnsAnswersApprovalsStopsATurnAndCloses(string client)
    {
        using var database = Database();
        await using var host = await Host.StartAsync(this, client, database.Path, "first");
        await host.WaitAsync(e => Is(e, "turn"));
        host.Texts().Should().Contain("echo: first");

        await host.Pipe.SendAsync(Command("send", "please approve this"), Token);
        var approval = await host.WaitAsync(e => Is(e, "approval"));
        await host.Pipe.SendAsync(
            new JsonObject
            {
                ["op"] = "approve",
                ["id"] = Text(approval, "id"),
                ["allow"] = client is "claude",
            },
            Token
        );
        await host.WaitAsync(e => Is(e, "turn"), 2);
        host.Texts().Should().Contain(client is "claude" ? "allowed notes.txt" : "declined");
        host.Events.Should().Contain(e => Is(e, "answered") && Text(e, "id") == Text(approval, "id"));

        await host.Pipe.SendAsync(Command("send", "slow work"), Token);
        await host.WaitAsync(e => Is(e, "text") && Text(e, "text") == "working");
        await host.Pipe.SendAsync(new JsonObject { ["op"] = "stop" }, Token);
        var stopped = await host.WaitAsync(e => Is(e, "turn"), 3);
        Text(stopped, "status").Should().Be("stopped");
        Session(database).Status.Should().Be("cancelled");

        await host.Pipe.SendAsync(Command("send", "again"), Token);
        await host.WaitAsync(e => Is(e, "turn"), 4);
        Session(database).Status.Should().Be("idle");

        await host.Pipe.SendAsync(new JsonObject { ["op"] = "close" }, Token);
        (await host.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token)).Should().Be(AgentHost.Completed);
        var state = AgentHostFiles.ReadState(State, host.Key)!;
        (Text(state, "status"), Text(state, "task_id"), Text(state, "client"))
            .Should()
            .Be(("closed", "TASK-0001", client is "claude" ? AgentClients.Claude : AgentClients.Codex));
        var session = Session(database);
        (session.Origin, session.SessionId).Should().Be((AgentSession.HarnessOrigin, Text(state, "session_id")));
        AgentHostFiles
            .Events(State, host.Key)
            .Select(AgentHostFiles.Sequence)
            .Should()
            .BeInAscendingOrder()
            .And.OnlyHaveUniqueItems();
        (await File.ReadAllLinesAsync(Path.Combine(State, ".gitignore"), Token)).Should().Contain("*");
        await ProviderFixture.GoneAsync(ClientPid());
    }

    [Fact]
    public async Task ASecondConnectionGetsEveryEventFromTheStartAndTheLogKeepsThemAfterTheHostEnds()
    {
        await using var host = await Host.StartAsync(this, "claude", "", "hello");
        await host.WaitAsync(e => Is(e, "turn"));
        await using (var late = (await AgentHostClient.ConnectAsync(host.Key, TimeSpan.FromSeconds(5), Token))!)
        {
            List<JsonObject> replay = [];
            await foreach (var item in late.EventsAsync(Token))
            {
                replay.Add(item);
                if (Is(item, "turn"))
                {
                    break;
                }
            }
            AgentHostFiles.Sequence(replay[0]).Should().Be(1);
            replay.Select(e => Text(e, "event")).Should().StartWith(["session", "status", "user", "status"]);
        }
        await host.Pipe.SendAsync(new JsonObject { ["op"] = "close" }, Token);
        await host.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var logged = AgentHostFiles.Events(State, host.Key);
        logged.Select(e => Text(e, "event")).Should().Contain(["user", "text", "turn"]).And.EndWith("status");
        AgentHostFiles.Events(State, host.Key, after: logged.Count - 1).Should().ContainSingle();
    }

    [Theory]
    [InlineData("claude", "--resume")]
    [InlineData("codex", "")]
    public async Task ResumeContinuesTheGivenSession(string client, string option)
    {
        var session = client is "claude" ? Guid.NewGuid().ToString() : "thread-7";
        await using var host = await Host.StartAsync(this, client, "", "go on", ["--resume", session]);
        await host.WaitAsync(e => Is(e, "turn"));
        host.Events.First(e => Is(e, "session")).Should().Match<JsonObject>(e => Text(e, "session_id") == session);
        if (option.Length > 0)
        {
            Calls()[0]["args"]!.AsArray().Select(a => (string)a!).Should().ContainInOrder(option, session);
            host.Texts().Should().Contain("resumed " + session + ": go on");
        }
        await host.Pipe.SendAsync(new JsonObject { ["op"] = "close" }, Token);
        await host.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task AClientThatExitsEndsTheHostAsFailed(string client)
    {
        await using var host = await Host.StartAsync(this, client, "", "exit now");
        (await host.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token)).Should().Be(AgentHost.Failed);
        // the host's last events can still be on their way through the pipe when it has ended
        (
            await host.WaitAsync(e =>
                Is(e, "error") && Text(e, "text").Contains("the client exited", StringComparison.Ordinal)
            )
        )
            .Should()
            .NotBeNull();
        Text(AgentHostFiles.ReadState(State, host.Key)!, "status").Should().Be("failed");
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task StoppingTheHostEndsItAndItsClientDuringATurn(string client)
    {
        await using var host = await Host.StartAsync(this, client, "", "slow");
        await host.WaitAsync(e => Is(e, "text") && Text(e, "text") == "working");
        await host.StopAsync();
        (await host.Exit.WaitAsync(TimeSpan.FromSeconds(10), Token)).Should().Be(AgentHost.Completed);
        Text(AgentHostFiles.ReadState(State, host.Key)!, "status").Should().Be("closed");
        await ProviderFixture.GoneAsync(ClientPid());
    }

    [Fact]
    public async Task CommandsThatCannotBeCarriedOutAreReportedAndTheHostGoesOn()
    {
        await using var host = await Host.StartAsync(this, "claude", "", "");
        await host.Pipe.SendAsync(
            new JsonObject
            {
                ["op"] = "approve",
                ["id"] = "nope",
                ["allow"] = true,
            },
            Token
        );
        await host.WaitAsync(e =>
            Is(e, "error") && Text(e, "text").Contains("no approval request nope", StringComparison.Ordinal)
        );
        await host.Pipe.SendAsync(Command("send", "slow"), Token);
        await host.WaitAsync(e => Is(e, "text") && Text(e, "text") == "working");
        await host.Pipe.SendAsync(Command("send", "too soon"), Token);
        await host.WaitAsync(e =>
            Is(e, "error") && Text(e, "text").Contains("a turn is running", StringComparison.Ordinal)
        );
        await host.Pipe.SendAsync(new JsonObject { ["op"] = "close" }, Token);
        (await host.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token)).Should().Be(AgentHost.Completed);
        await ProviderFixture.GoneAsync(ClientPid());
    }

    private static JsonObject Command(string op, string text) => new() { ["op"] = op, ["text"] = text };

    private static bool Is(JsonObject item, string name) => Text(item, "event") == name;

    private static string Text(JsonObject item, string name) => AgentHostFiles.Text(item, name);

    private HarnessDatabase Database()
    {
        var database = HarnessDatabase.Open(Path.Combine(_root, "h.db"), cancellationToken: Token);
        new TaskStore(database).Import(
            [new TaskDefinition { Id = "TASK-0001", Title = "t" }],
            "fixture",
            cancellationToken: Token
        );
        return database;
    }

    private static AgentSession Session(HarnessDatabase database) =>
        new TaskStore(database).Sessions("TASK-0001", Token).Should().ContainSingle().Subject;

    private List<JsonObject> Calls() =>
        File.ReadAllLines(Path.Combine(_root, "calls.jsonl")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

    private int ClientPid() => (int)Calls()[^1]["pid"]!;

    public void Dispose() => TestDirectory.Delete(_root);

    /// <summary>A host running in this process, and a pipe connection collecting its events.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);
        private readonly List<JsonObject> _events = [];
        private Task _reading = Task.CompletedTask;

        private Host(string key) => Key = key;

        public string Key { get; }
        public Task<int> Exit { get; private set; } = Task.FromResult(0);
        public AgentHostClient Pipe { get; private set; } = null!;

        public IReadOnlyList<JsonObject> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public List<string> Texts() => Events.Where(e => Is(e, "text")).Select(e => Text(e, "text")).ToList();

        public static async Task<Host> StartAsync(
            AgentServerTests test,
            string client,
            string database,
            string prompt,
            string[]? extra = null
        )
        {
            var host = new Host(AgentHostFiles.NewKey());
            List<string> args =
            [
                "serve",
                "--client",
                client is "claude" ? AgentClients.Claude : AgentClients.Codex,
                "--task",
                "TASK-0001",
                "--cwd",
                test._root,
                "--key",
                host.Key,
                "--state",
                test.State,
                "--command",
                "dotnet",
                "--command-arg",
                ProviderFixture.Worker,
                "--env",
                "HARNESS_FAKE_AGENT=" + client,
                "--env",
                "HARNESS_FAKE_AGENT_DIR=" + test._root,
                "--env",
                "HARNESS_FAKE_AGENT_MODE=" + (prompt == "fail" ? "fail" : "normal"),
                .. extra ?? [],
            ];
            if (prompt.Length > 0)
            {
                args.AddRange(["--prompt", prompt]);
            }
            if (database.Length > 0)
            {
                args.AddRange(["--db", database]);
            }
            host.Exit = AgentHost.RunAsync(
                args,
                TextReader.Null,
                TextWriter.Null,
                TextWriter.Null,
                host._lifetime.Token
            );
            AgentHostClient? pipe = null;
            for (var attempt = 0; attempt < 60 && pipe is null && !host.Exit.IsCompleted; attempt++)
            {
                pipe = await AgentHostClient.ConnectAsync(host.Key, TimeSpan.FromMilliseconds(500), Token);
            }
            if (pipe is null)
            {
                // the host ended before anyone connected: its log says what happened
                await host.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token);
                lock (host._events)
                {
                    host._events.AddRange(AgentHostFiles.Events(test.State, host.Key));
                }
                host.Pipe = null!;
                return host;
            }
            host.Pipe = pipe;
            host._reading = host.ReadAsync();
            return host;
        }

        private async Task ReadAsync()
        {
            await foreach (var item in Pipe.EventsAsync(_lifetime.Token))
            {
                lock (_events)
                {
                    _events.Add(item);
                }
            }
        }

        /// <summary>Waits for the <paramref name="count"/>th event that matches.</summary>
        public async Task<JsonObject> WaitAsync(Func<JsonObject, bool> match, int count = 1)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                var found = Events.Where(match).ToList();
                if (found.Count >= count)
                {
                    return found[count - 1];
                }
                await Task.Delay(20, timeout.Token);
            }
        }

        public Task StopAsync() => _lifetime.CancelAsync();

        public async ValueTask DisposeAsync()
        {
            if (!Exit.IsCompleted)
            {
                await _lifetime.CancelAsync();
            }
            await ((Task)Exit)
                .WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await _reading
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (Pipe is not null)
            {
                await Pipe.DisposeAsync();
            }
            _lifetime.Dispose();
        }
    }
}
