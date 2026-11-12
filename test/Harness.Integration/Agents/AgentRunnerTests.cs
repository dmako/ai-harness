using System.Globalization;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Agents;
using Harness.Core.Agents;
using Harness.Core.Tasks;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Processes;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Providers;
using Xunit;

namespace Harness.Integration.Agents;

/// <summary>
/// The agent runner (TASK-0115) against scripted Claude Code and Codex CLIs (Harness.ProviderWorker with
/// HARNESS_FAKE_AGENT): start, resume, cancel, a client that lingers after its turn and one that never ends.
/// </summary>
public sealed class AgentRunnerTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>How long a run may take before the test fails instead of hanging the job.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private readonly string _root = Directory.CreateTempSubdirectory("harness-agents-").FullName;

    [Fact]
    public async Task ClaudeStartsWithASessionIdHarnessChoseAndTheTaskInItsEnvironment()
    {
        await using var run = await Runner("claude").StartAsync(Start("claude", "normal", "build it"), Token);
        Guid.TryParse(run.SessionId, out _).Should().BeTrue();
        var events = await Collect(run).WaitAsync(Wait, Token);
        var outcome = await run.Completion.WaitAsync(Wait, Token);

        (outcome.Status, outcome.SessionId, outcome.Text, outcome.Model)
            .Should()
            .Be((AgentRunStatus.Completed, run.SessionId, "started: build it", "claude-fake-1"));
        (outcome.Usage, outcome.CostUsd, outcome.DurationMs)
            .Should()
            .Be((new TokenCounts(10, 200, 30, 40), (double?)0.0125, 1234L));
        events
            .Select(e => (e.Kind, e.Tool, e.Text))
            .Should()
            .Equal(
                (AgentEventKind.Session, "", ""),
                (AgentEventKind.ToolCall, "Bash", "dotnet build"),
                (AgentEventKind.Text, "", "started: build it"),
                (AgentEventKind.Usage, "", ""),
                (AgentEventKind.Finished, "", "")
            );
        var call = Calls().Should().ContainSingle().Subject;
        Arguments(call).Should().ContainInOrder("-p", "--output-format", "stream-json", "--session-id", run.SessionId);
        ((string?)call["task"], (string?)call["prompt"], (string?)call["cwd"])
            .Should()
            .Be(("TASK-0001", "build it", Physical(_root)));
    }

    [Fact]
    public async Task ClaudeResumesTheSessionItIsGiven()
    {
        await using var run = await Runner("claude").ResumeAsync("abc-1", Start("claude", "normal", "go on"), Token);
        await Collect(run).WaitAsync(Wait, Token);
        var outcome = await run.Completion.WaitAsync(Wait, Token);
        (outcome.Status, outcome.SessionId, outcome.Text)
            .Should()
            .Be((AgentRunStatus.Completed, "abc-1", "resumed abc-1: go on"));
        Arguments(Calls().Single()).Should().ContainInOrder("--resume", "abc-1").And.NotContain("--session-id");
    }

    [Fact]
    public async Task CodexTakesTheThreadIdFromThreadStartedAndResumesThatThread()
    {
        string thread;
        await using (var run = await Runner("codex").StartAsync(Start("codex", "normal", "first"), Token))
        {
            run.SessionId.Should().BeEmpty();
            var events = await Collect(run).WaitAsync(Wait, Token);
            var outcome = await run.Completion.WaitAsync(Wait, Token);
            thread = outcome.SessionId;
            Guid.TryParse(thread, out _).Should().BeTrue();
            events[0].Should().Match<AgentEvent>(e => e.Kind == AgentEventKind.Session && e.SessionId == thread);
            events
                .Single(e => e.Kind is AgentEventKind.ToolCall)
                .Should()
                .Match<AgentEvent>(e => e.Tool == "shell" && e.Text == "dotnet test");
            (outcome.Status, outcome.Text, outcome.Usage)
                .Should()
                .Be((AgentRunStatus.Completed, "started: first", new TokenCounts(200, 800, 0, 50, 20)));
        }
        await using (var resumed = await Runner("codex").ResumeAsync(thread, Start("codex", "normal", "next"), Token))
        {
            await Collect(resumed).WaitAsync(Wait, Token);
            var outcome = await resumed.Completion.WaitAsync(Wait, Token);
            (outcome.Status, outcome.SessionId, outcome.Text)
                .Should()
                .Be((AgentRunStatus.Completed, thread, "resumed " + thread + ": next"));
        }
        var calls = Calls();
        Arguments(calls[0]).Should().Equal("exec", "--json", "-");
        Arguments(calls[1]).Should().Equal("exec", "resume", "--json", thread, "-");
        calls.Select(c => (string?)c["task"]).Should().AllBe("TASK-0001");
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task CancellingAClientThatNeverEndsLeavesNoProcessBehind(string client)
    {
        await using var run = await Runner(client).StartAsync(Start(client, "hang", "wait"), Token);
        var (agent, descendant) = await Pids();
        List<AgentEvent> seen = [];
        var events = Collect(run, seen);
        // the fake prints its session line after it records the call: cancel only once the run has read it
        await ProviderFixture.EventuallyAsync(() => Seen(seen).Any(e => e.Kind == AgentEventKind.Session));

        await run.CancelAsync();
        var outcome = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), Token);
        outcome.Status.Should().Be(AgentRunStatus.Cancelled);
        (await events.WaitAsync(TimeSpan.FromSeconds(10), Token))
            .Should()
            .Contain(e => e.Kind == AgentEventKind.Session);
        await ProviderFixture.GoneAsync(agent);
        await ProviderFixture.GoneAsync(descendant);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task ARunEndsWhenTheTurnCompletesAlthoughTheClientLingers(string client)
    {
        await using var run = await Runner(client).StartAsync(Start(client, "linger", "quick"), Token);
        await Collect(run).WaitAsync(TimeSpan.FromSeconds(30), Token);
        var outcome = await run.Completion.WaitAsync(Wait, Token);
        (outcome.Status, outcome.Text).Should().Be((AgentRunStatus.Completed, "started: quick"));
        var (agent, descendant) = await Pids();
        await ProviderFixture.GoneAsync(agent);
        await ProviderFixture.GoneAsync(descendant);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task AClientThatExitsBeforeTheTurnEndsFailsWithWhatItSaid(string client)
    {
        await using var run = await Runner(client).StartAsync(Start(client, "fail", "x"), Token);
        await Collect(run).WaitAsync(Wait, Token);
        var outcome = await run.Completion.WaitAsync(Wait, Token);
        outcome.Status.Should().Be(AgentRunStatus.Failed);
        outcome.Error.Should().Contain("(3)").And.Contain("fake agent: boom");
    }

    [Fact]
    public async Task TheHostRecordsTheSessionAsStartedByHarnessAndWritesItsEventsAsJsonLines()
    {
        using var database = Database("TASK-0001");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await AgentHost
            .RunAsync(HostArguments("claude", "normal", database.Path), new StringReader(""), output, error, Token)
            .WaitAsync(Wait, Token);

        exit.Should().Be(AgentHost.Completed, error.ToString());
        var lines = output
            .ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!)
            .ToList();
        lines
            .Select(l => (string?)l["event"])
            .Should()
            .Equal("session", "tool", "text", "usage", "finished", "outcome");
        var outcome = lines[^1];
        ((string?)outcome["status"], (string?)outcome["text"], (double?)outcome["cost_usd"])
            .Should()
            .Be(("completed", "started: do it", 0.0125));
        var session = new TaskStore(database).Sessions("TASK-0001", Token).Should().ContainSingle().Subject;
        (session.Client, session.SessionId, session.Origin, session.Status)
            .Should()
            .Be((AgentClients.Claude, (string)outcome["session_id"]!, AgentSession.HarnessOrigin, "idle"));
    }

    [Fact]
    public async Task CancellingTheHostStopsTheClientAndMarksTheSessionCancelled()
    {
        using var database = Database("TASK-0001");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var host = AgentHost.RunAsync(
            HostArguments("codex", "hang", database.Path),
            TextReader.Null,
            TextWriter.Null,
            TextWriter.Null,
            lifetime.Token
        );
        var (agent, descendant) = await Pids();
        await ProviderFixture.EventuallyAsync(() => new TaskStore(database).Sessions("TASK-0001", Token).Count is 1);
        await lifetime.CancelAsync();

        (await host.WaitAsync(Wait, Token)).Should().Be(AgentHost.Cancelled);
        new TaskStore(database)
            .Sessions("TASK-0001", Token)
            .Single()
            .Should()
            .Match<AgentSession>(s => s.Status == "cancelled" && s.Origin == AgentSession.HarnessOrigin);
        await ProviderFixture.GoneAsync(agent);
        await ProviderFixture.GoneAsync(descendant);
    }

    [Fact]
    public async Task TheHostStopsATurnOnACancelLine()
    {
        using var output = new StringWriter();
        using var input = new GatedReader(() => File.Exists(Path.Combine(_root, "agent.pid")));
        var exit = await AgentHost
            .RunAsync(HostArguments("claude", "hang", ""), input, output, TextWriter.Null, Token)
            .WaitAsync(Wait, Token);
        exit.Should().Be(AgentHost.Cancelled);
        output.ToString().Should().Contain("\"status\":\"cancelled\"");
    }

    [Fact]
    public async Task TheHostRefusesAnIncompleteCommandLine()
    {
        using var error = new StringWriter();
        (await AgentHost.RunAsync(["run", "--client", "gemini"], TextReader.Null, TextWriter.Null, error, Token))
            .Should()
            .Be(AgentHost.Usage);
        error.ToString().Should().Contain("--client must be claude-code or codex").And.Contain("Usage:");
        (await AgentHost.RunAsync(["run", "--client"], TextReader.Null, TextWriter.Null, TextWriter.Null, Token))
            .Should()
            .Be(AgentHost.Usage);
    }

    [Fact]
    public async Task APromptFileThatCannotBeReadIsAFailedRunNotACrash()
    {
        using var database = Database("TASK-0001");
        var missing = Path.Combine(_root, "absent", "prompt.txt");
        foreach (var file in new[] { missing, _root })
        {
            using var output = new StringWriter();
            var args = WithPromptFile(HostArguments("claude", "normal", database.Path), file);

            (await AgentHost.RunAsync(args, TextReader.Null, output, TextWriter.Null, Token))
                .Should()
                .Be(AgentHost.Failed);
            var outcome = JsonNode.Parse(output.ToString())!;
            ((string?)outcome["event"], (string?)outcome["status"]).Should().Be(("outcome", "failed"));
            ((string)outcome["error"]!).Should().StartWith("cannot read --prompt-file " + file + ": ");
        }
        File.Exists(Path.Combine(_root, "agent.pid")).Should().BeFalse("no client was started");
        new TaskStore(database).Sessions("TASK-0001", Token).Should().BeEmpty();
    }

    [Fact]
    public async Task ServeWithAPromptFileThatCannotBeReadIsAFailedRun()
    {
        using var output = new StringWriter();
        string[] args =
        [
            "serve",
            "--client",
            AgentClients.Codex,
            "--task",
            "TASK-0001",
            "--cwd",
            _root,
            "--prompt-file",
            Path.Combine(_root, "absent.txt"),
            "--key",
            "0123456789abcdef0123456789abcdef",
            "--state",
            Path.Combine(_root, "state"),
        ];

        (await AgentHost.RunAsync(args, TextReader.Null, output, TextWriter.Null, Token)).Should().Be(AgentHost.Failed);
        output.ToString().Should().Contain("\"status\":\"failed\"").And.Contain("cannot read --prompt-file");
        Directory.Exists(Path.Combine(_root, "state")).Should().BeFalse("the host never started");
    }

    [Fact]
    public async Task APromptFileIsReadByTheHostAndMustNotBeEmptyOrGivenWithAPrompt()
    {
        var file = Path.Combine(_root, "prompt.txt");
        await File.WriteAllTextAsync(file, "build it", Token);
        using var output = new StringWriter();
        (
            await AgentHost.RunAsync(
                WithPromptFile(HostArguments("claude", "normal", ""), file),
                TextReader.Null,
                output,
                TextWriter.Null,
                Token
            )
        )
            .Should()
            .Be(AgentHost.Completed);
        output.ToString().Should().Contain("started: build it");

        using var both = new StringWriter();
        (
            await AgentHost.RunAsync(
                [.. HostArguments("claude", "normal", ""), "--prompt-file", file],
                TextReader.Null,
                TextWriter.Null,
                both,
                Token
            )
        )
            .Should()
            .Be(AgentHost.Usage);
        both.ToString().Should().Contain("give --prompt or --prompt-file, not both").And.Contain("Usage:");

        await File.WriteAllTextAsync(file, " \n", Token);
        using var empty = new StringWriter();
        using var emptyOutput = new StringWriter();
        (
            await AgentHost.RunAsync(
                WithPromptFile(HostArguments("claude", "normal", ""), file),
                TextReader.Null,
                emptyOutput,
                empty,
                Token
            )
        )
            .Should()
            .Be(AgentHost.Usage);
        empty.ToString().Should().Contain("--task, --cwd and a prompt are required").And.Contain("Usage:");
        emptyOutput.ToString().Should().BeEmpty();
    }

    /// <summary>The arguments with their --prompt replaced by a --prompt-file.</summary>
    private static string[] WithPromptFile(string[] args, string file)
    {
        var at = Array.IndexOf(args, "--prompt");
        return [.. args[..at], "--prompt-file", file, .. args[(at + 2)..]];
    }

    private static IAgentRunner Runner(string client)
    {
        var command = new AgentCommand("dotnet", [ProviderFixture.Worker]);
        return client is "claude"
            ? new ClaudeRunner(new ProviderProcessFactory(), command)
            : new CodexRunner(new ProviderProcessFactory(), command);
    }

    private AgentStart Start(string client, string mode, string prompt) =>
        new("TASK-0001", _root, prompt) { Environment = Fake(client, mode) };

    private Dictionary<string, string> Fake(string client, string mode) =>
        new()
        {
            ["HARNESS_FAKE_AGENT"] = client,
            ["HARNESS_FAKE_AGENT_MODE"] = mode,
            ["HARNESS_FAKE_AGENT_DIR"] = _root,
        };

    private string[] HostArguments(string client, string mode, string database)
    {
        List<string> args =
        [
            "run",
            "--client",
            client is "claude" ? AgentClients.Claude : AgentClients.Codex,
            "--task",
            "TASK-0001",
            "--cwd",
            _root,
            "--prompt",
            "do it",
            "--command",
            "dotnet",
            "--command-arg",
            ProviderFixture.Worker,
        ];
        foreach (var (key, value) in Fake(client, mode))
        {
            args.AddRange(["--env", key + "=" + value]);
        }
        if (database.Length > 0)
        {
            args.AddRange(["--db", database]);
        }
        return [.. args];
    }

    private HarnessDatabase Database(params string[] tasks)
    {
        var database = HarnessDatabase.Open(Path.Combine(_root, "h.db"), cancellationToken: Token);
        new TaskStore(database).Import(
            tasks.Select(id => new TaskDefinition { Id = id, Title = id }).ToArray(),
            "fixture",
            cancellationToken: Token
        );
        return database;
    }

    private static async Task<List<AgentEvent>> Collect(IAgentRun run, List<AgentEvent>? events = null)
    {
        events ??= [];
        await foreach (var item in run.Events.WithCancellation(Token))
        {
            lock (events)
            {
                events.Add(item);
            }
        }
        return events;
    }

    /// <summary>The directory as the client's getcwd reports it: macOS's /var is a link to /private/var.</summary>
    private static string Physical(string directory) =>
        OperatingSystem.IsWindows() ? directory : GitRepository.PhysicalDirectory(directory);

    private static List<AgentEvent> Seen(List<AgentEvent> events)
    {
        lock (events)
        {
            return [.. events];
        }
    }

    private List<JsonObject> Calls()
    {
        var path = Path.Combine(_root, "calls.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader
            .ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToList();
    }

    private static List<string> Arguments(JsonObject call) =>
        call["args"]!.AsArray().Select(a => (string)a!).Where(a => a != ProviderFixture.Worker).ToList();

    private async Task<(int Agent, int Descendant)> Pids()
    {
        var agent = Path.Combine(_root, "agent.pid");
        await ProviderFixture.EventuallyAsync(() => File.Exists(agent), TimeSpan.FromSeconds(30));
        return (Pid(agent), Pid(Path.Combine(_root, "descendant.pid")));
    }

    private static int Pid(string path)
    {
        // the fake creates the file before it writes the id: an empty read waits like a failed one
        for (var attempt = 0; attempt < 250; attempt++)
        {
            if (Read(path) is { Length: > 0 } text)
            {
                return int.Parse(text, CultureInfo.InvariantCulture);
            }
            Thread.Sleep(20);
        }
        throw new TimeoutException("no process id in " + path);

        static string Read(string path)
        {
            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch (IOException)
            {
                return "";
            }
        }
    }

    public void Dispose() => TestDirectory.Delete(_root);

    /// <summary>Standard input that says "cancel" once a condition holds.</summary>
    private sealed class GatedReader(Func<bool> ready) : TextReader
    {
        private bool _said;

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_said)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            while (!ready())
            {
                await Task.Delay(20, cancellationToken);
            }
            _said = true;
            return "cancel";
        }
    }
}
