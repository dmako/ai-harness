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
        // the pipe can close before it delivers the host's last events; its log has them all once it has ended
        AgentHostFiles
            .Events(State, host.Key)
            .Should()
            .Contain(e => Is(e, "error") && Text(e, "text").Contains("the client exited", StringComparison.Ordinal));
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

    [Theory]
    [InlineData("", "--permission-mode auto")]
    [InlineData("auto", "--permission-mode auto")]
    [InlineData("manual", "")]
    [InlineData("acceptEdits", "--permission-mode acceptEdits")]
    [InlineData("plan", "--permission-mode plan")]
    [InlineData("bypassPermissions", "--permission-mode bypassPermissions")]
    public async Task ClaudeIsStartedWithThePermissionModesFlags(string mode, string flags)
    {
        await using var host = await Host.StartAsync(this, "claude", "", "hello", Mode(mode));
        await host.WaitAsync(e => Is(e, "turn"));
        var args = Calls()[0]["args"]!.AsArray().Select(a => (string)a!).ToList();
        args.Should().ContainInOrder("--permission-prompt-tool", "stdio");
        if (flags.Length > 0)
        {
            args.Should().ContainInOrder(flags.Split(' ')).And.ContainSingle(a => a == "--permission-mode");
        }
        else
        {
            args.Should().NotContain("--permission-mode");
        }
        Text(AgentHostFiles.ReadState(State, host.Key)!, "permission_mode").Should().Be(mode is "" ? "auto" : mode);
        await host.Pipe.SendAsync(new JsonObject { ["op"] = "close" }, Token);
        await host.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    [Theory]
    [InlineData("", "on-request", "workspace-write")]
    [InlineData("auto", "on-request", "workspace-write")]
    [InlineData("manual", "untrusted", "workspace-write")]
    [InlineData("read-only", "on-request", "read-only")]
    [InlineData("full-access", "never", "danger-full-access")]
    public async Task CodexOpensItsThreadWithThePermissionModesSettings(string mode, string approval, string sandbox)
    {
        await using var host = await Host.StartAsync(this, "codex", "", "hello", Mode(mode));
        await host.WaitAsync(e => Is(e, "turn"));
        var thread = Threads().Should().ContainSingle().Subject;
        (Text(thread, "method"), thread["params"]!["approvalPolicy"]!.GetValue<string>())
            .Should()
            .Be(("thread/start", approval));
        thread["params"]!["sandbox"]!.GetValue<string>().Should().Be(sandbox);
        Text(AgentHostFiles.ReadState(State, host.Key)!, "permission_mode").Should().Be(mode is "" ? "auto" : mode);
        await host.Pipe.SendAsync(new JsonObject { ["op"] = "close" }, Token);
        await host.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    [Theory]
    [InlineData("claude", "read-only")]
    [InlineData("codex", "plan")]
    [InlineData("codex", "Auto")]
    public async Task AnUnknownPermissionModeIsAUsageErrorAndStartsNoSession(string client, string mode)
    {
        using var error = new StringWriter();
        var exit = await AgentHost.RunAsync(
            [
                "serve",
                "--client",
                client is "claude" ? AgentClients.Claude : AgentClients.Codex,
                "--task",
                "TASK-0001",
                "--cwd",
                _root,
                "--key",
                AgentHostFiles.NewKey(),
                "--state",
                State,
                "--permission-mode",
                mode,
            ],
            TextReader.Null,
            TextWriter.Null,
            error,
            Token
        );
        exit.Should().Be(AgentHost.Usage);
        error.ToString().Should().Contain("unknown permission mode '" + mode + "'").And.Contain("choose one of auto");
        File.Exists(Path.Combine(_root, "calls.jsonl")).Should().BeFalse();
        Directory.Exists(State).Should().BeFalse();
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task AResumedSessionKeepsItsPermissionModeUnlessANewOneIsGiven(string client)
    {
        var (first, later) = client is "claude" ? ("plan", "acceptEdits") : ("manual", "full-access");
        string session;
        await using (var host = await Host.StartAsync(this, client, "", "hello", Mode(first)))
        {
            await host.WaitAsync(e => Is(e, "turn"));
            session = Text(host.Events.First(e => Is(e, "session") && Text(e, "session_id").Length > 0), "session_id");
            await host.Pipe.SendAsync(new JsonObject { ["op"] = "close" }, Token);
            await host.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token);
        }

        string Resumed(string key) => Text(AgentHostFiles.ReadState(State, key)!, "permission_mode");
        foreach (var (given, expected) in new[] { ("", first), (later, later) })
        {
            await using var host = await Host.StartAsync(
                this,
                client,
                "",
                "go on",
                ["--resume", session, .. Mode(given)]
            );
            await host.WaitAsync(e => Is(e, "turn"));
            Resumed(host.Key).Should().Be(expected);
            await host.Pipe.SendAsync(new JsonObject { ["op"] = "close" }, Token);
            await host.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token);
        }
        if (client is "claude")
        {
            Calls()
                .Select(c => string.Join(' ', c["args"]!.AsArray().Select(a => (string)a!)))
                .Should()
                .SatisfyRespectively(
                    c => c.Should().Contain("--permission-mode plan"),
                    c => c.Should().Contain("--permission-mode plan --resume " + session),
                    c => c.Should().Contain("--permission-mode acceptEdits --resume " + session)
                );
        }
        else
        {
            Threads()
                .Select(t => (Text(t, "method"), t["params"]!["approvalPolicy"]!.GetValue<string>()))
                .Should()
                .Equal(("thread/start", "untrusted"), ("thread/resume", "untrusted"), ("thread/resume", "never"));
        }
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task ASessionsHandshakeRefreshesItsClientsModelCatalogWithoutWaitingForIt(string client)
    {
        var path = Path.Combine(_root, "global", AgentModelStore.FileName);
        var name = client is "claude" ? AgentClients.Claude : AgentClients.Codex;
        var store = new AgentModelStore(path);
        await store.SeenAsync(name, [("model-from-a-transcript", DateTimeOffset.UtcNow)], Token);
        await using (var host = await Host.StartAsync(this, client, "", "hello", ["--models", path]))
        {
            await host.WaitAsync(e => Is(e, "turn"));
            var catalog = await CatalogAsync(store, name, c => c.RefreshedAt is not null);
            catalog.Source.Should().Be(AgentModelSources.Session);
            if (client is "claude")
            {
                catalog
                    .Models.Select(m => (m.Id, m.Label, m.IsDefault))
                    .Should()
                    .StartWith([("fake", "Fake 1", true), ("claude-fake-2", "Fake 2", false)]);
                catalog.Version.Should().Be("0.0.1-fake");
            }
            else
            {
                // both pages of model/list, the hidden model kept but marked
                catalog
                    .Models.Select(m => (m.Id, m.IsDefault, m.Hidden))
                    .Should()
                    .StartWith([
                        ("gpt-fake", true, false),
                        ("gpt-fake-old", false, true),
                        ("gpt-fake-mini", false, false),
                    ]);
                catalog.Version.Should().Be("0.0.2-fake");
            }
            catalog.Models[^1].Should().Match<AgentModel>(m => m.Id == "model-from-a-transcript" && m.Source == "seen");
            await host.Pipe.SendAsync(new JsonObject { ["op"] = "close" }, Token);
            await host.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token);
        }
        // the model asked for is kept beside the one the client names, so harness ui can tell an unlisted one
        var state = AgentHostFiles.States(State).Should().ContainSingle().Subject;
        Text(state, "requested_model").Should().BeEmpty();

        var (refreshed, count) = (store.Read(name).RefreshedAt, store.Read(name).Models.Count);
        await using (
            var failing = await Host.StartAsync(
                this,
                client,
                "",
                "hello",
                ["--models", path, "--model", "my-own-model", "--env", "HARNESS_FAKE_AGENT_MODELS=error"]
            )
        )
        {
            await failing.WaitAsync(e => Is(e, "turn"));
            var kept = await CatalogAsync(store, name, c => c.Error.Length > 0);
            kept.Error.Should().Contain("fake agent: no models");
            (kept.RefreshedAt, kept.Models.Count).Should().Be((refreshed, count), "a failed refresh keeps the catalog");
            Text(AgentHostFiles.ReadState(State, failing.Key)!, "requested_model").Should().Be("my-own-model");
            await failing.Pipe.SendAsync(new JsonObject { ["op"] = "close" }, Token);
            await failing.Exit.WaitAsync(TimeSpan.FromSeconds(30), Token);
        }
    }

    private static async Task<AgentModelCatalog> CatalogAsync(
        AgentModelStore store,
        string client,
        Func<AgentModelCatalog, bool> ready
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var catalog = store.Read(client);
            if (ready(catalog))
            {
                return catalog;
            }
            await Task.Delay(50, timeout.Token);
        }
    }

    private static string[] Mode(string mode) => mode.Length > 0 ? ["--permission-mode", mode] : [];

    private List<JsonObject> Threads() =>
        File.ReadAllLines(Path.Combine(_root, "threads.jsonl")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

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
