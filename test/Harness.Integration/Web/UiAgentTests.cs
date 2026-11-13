using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Hooks;
using Harness.Core.Tasks;
using Harness.Infrastructure.Agents;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Web;

/// <summary>
/// Agent sessions in harness ui (TASK-0116): started from the task, followed over /api/events and the log, steered
/// with send, approve and close through the agent host's pipe; and a session started elsewhere read from its
/// transcript. The real harness ui and harness-agents run; the client is the scripted fake.
/// </summary>
public sealed class UiAgentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAgentStartedFromTheTaskIsFollowedLiveSteeredAndClosed()
    {
        using var fixture = await SyncFixture.CreateAsync();
        Tasks(fixture);
        var calls = Directory.CreateTempSubdirectory("harness-ui-agent-").FullName;
        try
        {
            await using var server = await UiFixture.StartAsync(fixture, environment: Environment(calls));
            var id = (await Read(server.Client, "/api/projects"))["projects"]![0]!["id"]!.GetValue<string>();
            var project = "/api/projects/" + id;
            using var events = new LiveEvents(server.Client);

            var key = (
                await Write(
                    server.Client,
                    project + "/tasks/TASK-0001/agents",
                    new()
                    {
                        ["client"] = "claude-code",
                        ["prompt"] = "first",
                        ["mode"] = "acceptEdits",
                    }
                )
            )["key"]!.GetValue<string>();
            AgentHostFiles.IsKey(key).Should().BeTrue();
            var first = await LogAsync(server.Client, project, key, e => Is(e, "turn"));
            first.Should().Contain(e => Is(e, "text") && Text(e, "text") == "echo: first");

            await Write(server.Client, project + "/agents/" + key + "/send", new() { ["text"] = "please approve" });
            var approval = await events.WaitAsync(key, e => Is(e, "approval"));
            await Write(
                server.Client,
                project + "/agents/" + key + "/approve",
                new() { ["id"] = Text(approval, "id"), ["allow"] = true }
            );
            var answered = await LogAsync(
                server.Client,
                project,
                key,
                e => Is(e, "text") && Text(e, "text") == "allowed notes.txt"
            );
            answered.Should().Contain(e => Is(e, "answered") && Text(e, "id") == Text(approval, "id"));
            (await events.WaitAsync(key, e => Is(e, "answered"))).Should().NotBeNull();

            var sessions = await Read(server.Client, project + "/tasks/TASK-0001/sessions");
            var host = sessions["hosts"]!.AsArray().Single()!;
            (host["key"]!.GetValue<string>(), host["alive"]!.GetValue<bool>(), host["client"]!.GetValue<string>())
                .Should()
                .Be((key, true, "claude-code"));
            // the mode chosen travelled to the host and its client (TASK-0135)
            host["permission_mode"]!.GetValue<string>().Should().Be("acceptEdits");
            (await File.ReadAllTextAsync(Path.Combine(calls, "calls.jsonl"), Token))
                .Should()
                .Contain("\"--permission-mode\",\"acceptEdits\"");
            sessions["permission_modes"]!["codex"]!.AsArray().Should().HaveCount(4);
            var session = sessions["sessions"]!.AsArray().Single()!;
            (session["origin"]!.GetValue<string>(), session["session_id"]!.GetValue<string>())
                .Should()
                .Be(("harness", host["session_id"]!.GetValue<string>()));

            await Write(server.Client, project + "/agents/" + key + "/close", []);
            await LogAsync(server.Client, project, key, e => Is(e, "status") && Text(e, "status") == "closed");
            await ProviderFixture.EventuallyAsync(
                () =>
                    !AgentHostFiles.Alive(
                        AgentHostFiles.ReadState(AgentHostFiles.Directory(fixture.DatabasePath), key)!
                    ),
                TimeSpan.FromSeconds(15)
            );
            var after = await Read(server.Client, project + "/agents/" + key + "/log");
            (after["alive"]!.GetValue<bool>(), after["status"]!.GetValue<string>()).Should().Be((false, "closed"));
            using var refused = await Post(
                server.Client,
                project + "/agents/" + key + "/send",
                new() { ["text"] = "hello?" }
            );
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            TestDirectory.Delete(calls);
        }
    }

    [Fact]
    public async Task AnUnknownPermissionModeIsRefusedAndStartsNoAgent()
    {
        using var fixture = await SyncFixture.CreateAsync();
        Tasks(fixture);
        var calls = Directory.CreateTempSubdirectory("harness-ui-agent-").FullName;
        try
        {
            await using var server = await UiFixture.StartAsync(fixture, environment: Environment(calls));
            var id = (await Read(server.Client, "/api/projects"))["projects"]![0]!["id"]!.GetValue<string>();
            using var refused = await Post(
                server.Client,
                "/api/projects/" + id + "/tasks/TASK-0001/agents",
                new()
                {
                    ["client"] = "codex",
                    ["prompt"] = "first",
                    ["mode"] = "bypassPermissions",
                }
            );
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.Parse(await refused.Content.ReadAsStringAsync(Token))!["error"]!
                .GetValue<string>()
                .Should()
                .Be(
                    "unknown permission mode 'bypassPermissions' for codex; choose one of auto, manual, read-only, full-access"
                );
            AgentHostFiles.States(AgentHostFiles.Directory(fixture.DatabasePath)).Should().BeEmpty();
            File.Exists(Path.Combine(calls, "calls.jsonl")).Should().BeFalse();
        }
        finally
        {
            TestDirectory.Delete(calls);
        }
    }

    [Fact]
    public async Task ASessionStartedElsewhereShowsTheEndOfItsTranscriptAndHowToResumeIt()
    {
        using var fixture = await SyncFixture.CreateAsync();
        Tasks(fixture);
        const string Session = "6f1e2d3c-0000-4000-8000-000000000001";
        var transcript = Path.Combine(fixture.DirectoryRoot, "claude", "projects", "repo", Session + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        await File.WriteAllTextAsync(
            transcript,
            string.Join(
                "\n",
                $$$"""{"type":"user","sessionId":"{{{Session}}}","timestamp":"2026-11-08T10:00:00Z","message":{"role":"user","content":"fix the build"}}""",
                $$$"""{"type":"assistant","sessionId":"{{{Session}}}","timestamp":"2026-11-08T10:00:05Z","message":{"id":"m1","model":"claude-sonnet-5-5","content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet build"}}],"usage":{"input_tokens":3,"output_tokens":4} } }""",
                $$$"""{"type":"assistant","sessionId":"{{{Session}}}","timestamp":"2026-11-08T10:00:09Z","message":{"id":"m2","model":"claude-sonnet-5-5","content":[{"type":"text","text":"The build passes."}],"usage":{"input_tokens":3,"output_tokens":4} } }"""
            ) + "\n",
            new UTF8Encoding(false),
            Token
        );
        using (var database = HarnessDatabase.Open(fixture.DatabasePath, cancellationToken: Token))
        {
            new TaskStore(database).RecordSession(
                "claude-code",
                Session,
                "TASK-0001",
                HookTaskActions.Claim,
                DateTimeOffset.Parse("2026-11-08T10:00:01Z", System.Globalization.CultureInfo.InvariantCulture),
                Token
            );
        }
        await using var server = await UiFixture.StartAsync(fixture);
        var id = (await Read(server.Client, "/api/projects"))["projects"]![0]!["id"]!.GetValue<string>();
        var project = "/api/projects/" + id;
        (await Read(server.Client, project + "/tasks/TASK-0001/sessions"))["sessions"]![0]!["transcript_path"]!
            .GetValue<string>()
            .Should()
            .Be(transcript);

        var tail = await Read(server.Client, project + "/tasks/TASK-0001/sessions/claude-code/" + Session + "/tail");
        tail["resume"]!.GetValue<string>().Should().Be("claude --resume " + Session);
        tail["entries"]!
            .AsArray()
            .Select(e =>
                (e!["kind"]!.GetValue<string>(), e["tool"]?.GetValue<string>() ?? "", e["text"]!.GetValue<string>())
            )
            .Should()
            .Equal(("user", "", "fix the build"), ("tool", "Bash", "dotnet build"), ("text", "", "The build passes."));
        using var missing = await server.Client.GetAsync(project + "/tasks/TASK-0001/sessions/codex/nope/tail", Token);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var unknown = await server.Client.GetAsync(project + "/agents/" + new string('a', 32) + "/log", Token);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheModelCatalogIsProbedInTheBackgroundAddsTranscriptModelsAndMarksAnUnlistedModel()
    {
        using var fixture = await SyncFixture.CreateAsync();
        Tasks(fixture);
        const string Session = "6f1e2d3c-0000-4000-8000-000000000002";
        var transcript = Path.Combine(fixture.DirectoryRoot, "claude", "projects", "repo", Session + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        await File.WriteAllTextAsync(
            transcript,
            $$$"""{"type":"assistant","sessionId":"{{{Session}}}","timestamp":"2026-11-08T10:00:05Z","message":{"id":"m1","model":"claude-sonnet-4-6","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":3,"output_tokens":4} } }"""
                + "\n",
            new UTF8Encoding(false),
            Token
        );
        using (var database = HarnessDatabase.Open(fixture.DatabasePath, cancellationToken: Token))
        {
            new TaskStore(database).RecordSession(
                "claude-code",
                Session,
                "TASK-0001",
                HookTaskActions.Claim,
                DateTimeOffset.Parse("2026-11-08T10:00:01Z", System.Globalization.CultureInfo.InvariantCulture),
                Token
            );
        }
        // Codex's catalog is current, so only Claude's (empty) one is probed
        var store = new AgentModelStore(Path.Combine(fixture.Global, AgentModelStore.FileName));
        await store.RefreshedAsync(
            "codex",
            new([new("gpt-fake", "GPT Fake") { IsDefault = true }]),
            "probe",
            DateTimeOffset.UtcNow,
            Token
        );
        var calls = Directory.CreateTempSubdirectory("harness-ui-agent-").FullName;
        try
        {
            var environment = Environment(calls);
            environment["HARNESS_AGENT_MODELS_OFFLINE"] = "";
            await using var server = await UiFixture.StartAsync(fixture, environment: environment);
            var id = (await Read(server.Client, "/api/projects"))["projects"]![0]!["id"]!.GetValue<string>();
            var project = "/api/projects/" + id;
            // reading the task's sessions brings the usage, and so the transcript's model, up to date
            await Read(server.Client, project + "/tasks/TASK-0001/sessions");

            // a read without the harness ui header starts no probe
            var plain = await Read(server.Client, project + "/agent-models");
            var claude = plain["clients"]!["claude-code"]!;
            (claude["refreshing"]!.GetValue<bool>(), claude["stale"]!.GetValue<bool>()).Should().Be((false, true));
            claude["models"]!.AsArray().Select(m => (string)m!["id"]!).Should().Equal("claude-sonnet-4-6");
            claude["models"]![0]!["source"]!.GetValue<string>().Should().Be("seen");
            plain["clients"]!["codex"]!["default"]!["label"]!.GetValue<string>().Should().Be("GPT Fake");
            plain["clients"]!["codex"]!["stale"]!.GetValue<bool>().Should().BeFalse();
            File.Exists(Path.Combine(calls, "calls.jsonl")).Should().BeFalse();

            // the dialog's read starts it, answers at once and is asked again until it is done
            var first = await Trusted(server.Client, project + "/agent-models");
            first["clients"]!["codex"]!["refreshing"]!.GetValue<bool>().Should().BeFalse();
            JsonNode probed;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                while (true)
                {
                    probed = (await Trusted(server.Client, project + "/agent-models"))["clients"]!["claude-code"]!;
                    if (
                        !probed["refreshing"]!.GetValue<bool>()
                        && probed["refreshed_at"]!.GetValue<string>().Length > 0
                    )
                    {
                        break;
                    }
                    await Task.Delay(200, timeout.Token);
                }
            }
            probed["error"]!.GetValue<string>().Should().BeEmpty();
            probed["source"]!.GetValue<string>().Should().Be("probe");
            probed["default"]!["label"]!.GetValue<string>().Should().Be("Fake 1");
            probed["models"]!
                .AsArray()
                .Select(m => ((string)m!["id"]!, (string)m["source"]!))
                .Should()
                .Equal(("fake", "probe"), ("claude-fake-2", "probe"), ("claude-sonnet-4-6", "seen"));
            probed["age_seconds"]!.GetValue<long>().Should().BeLessThan(60);
            // the probe saved no session and ran no turn
            var call = JsonNode.Parse((await File.ReadAllLinesAsync(Path.Combine(calls, "calls.jsonl"), Token))[0])!;
            call["args"]!.AsArray().Select(a => (string)a!).Should().Contain("--no-session-persistence");

            // any model id starts; one the catalog does not name is marked in the session view
            var key = (
                await Write(
                    server.Client,
                    project + "/tasks/TASK-0001/agents",
                    new()
                    {
                        ["client"] = "claude-code",
                        ["prompt"] = "first",
                        ["model"] = "my-own-model",
                    }
                )
            )["key"]!.GetValue<string>();
            await LogAsync(server.Client, project, key, e => Is(e, "turn"));
            var host = (await Read(server.Client, project + "/tasks/TASK-0001/sessions"))["hosts"]!.AsArray().Single()!;
            (host["requested_model"]!.GetValue<string>(), host["model_unlisted"]!.GetValue<bool>())
                .Should()
                .Be(("my-own-model", true));
            (await File.ReadAllTextAsync(Path.Combine(calls, "calls.jsonl"), Token))
                .Should()
                .Contain("\"--model\",\"my-own-model\"");
            // the session's handshake refreshed the catalog in turn
            await ProviderFixture.EventuallyAsync(
                () => store.Read("claude-code").Source is "session",
                TimeSpan.FromSeconds(15)
            );
            await Write(server.Client, project + "/agents/" + key + "/close", []);
            await LogAsync(server.Client, project, key, e => Is(e, "status") && Text(e, "status") == "closed");
        }
        finally
        {
            TestDirectory.Delete(calls);
        }
    }

    private static async Task<JsonObject> Trusted(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Harness-UI", "1");
        using var response = await client.SendAsync(request, Token);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!.AsObject();
    }

    private static void Tasks(SyncFixture fixture)
    {
        using var database = HarnessDatabase.Open(fixture.DatabasePath, cancellationToken: Token);
        new TaskStore(database).Import(
            [new TaskDefinition { Id = "TASK-0001", Title = "agent task" }],
            "fixture",
            cancellationToken: Token
        );
    }

    private static Dictionary<string, string> Environment(string calls)
    {
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var agents = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../../src/Harness.Agents/bin",
                Configuration,
                TestDirectory.TargetFramework,
                "harness-agents" + suffix
            )
        );
        var worker = Path.Combine(Path.GetDirectoryName(ProviderFixture.Worker)!, "Harness.ProviderWorker" + suffix);
        return new()
        {
            ["HARNESS_AGENTS_PATH"] = agents,
            ["HARNESS_CLAUDE_COMMAND"] = worker,
            ["HARNESS_FAKE_AGENT"] = "claude",
            ["HARNESS_FAKE_AGENT_DIR"] = calls,
        };
    }

    /// <summary>Reads a host's log until an event matches, as the page does without the live stream.</summary>
    private static async Task<List<JsonObject>> LogAsync(
        HttpClient client,
        string project,
        string key,
        Func<JsonObject, bool> match
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var log = await Read(client, project + "/agents/" + key + "/log");
            var events = log["events"]!.AsArray().OfType<JsonObject>().ToList();
            if (events.Any(match))
            {
                return events;
            }
            await Task.Delay(100, timeout.Token);
        }
    }

    private static bool Is(JsonObject item, string name) => Text(item, "event") == name;

    private static string Text(JsonObject item, string name) => AgentHostFiles.Text(item, name);

    private static async Task<JsonObject> Read(HttpClient client, string path) =>
        (await client.GetFromJsonAsync<JsonObject>(path, Token))!;

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, JsonObject body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Harness-UI", "1");
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(request, Token);
    }

    private static async Task<JsonObject> Write(HttpClient client, string path, JsonObject body)
    {
        using var response = await Post(client, path, body);
        var text = await response.Content.ReadAsStringAsync(Token);
        response.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>The /api/events stream's "agent" events, collected as they come.</summary>
    private sealed class LiveEvents : IDisposable
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        private readonly List<(string Key, JsonObject Event)> _events = [];
        private readonly Task _reading;

        public LiveEvents(HttpClient client) => _reading = ReadAsync(client);

        private async Task ReadAsync(HttpClient client)
        {
            try
            {
                using var stream = await client.GetStreamAsync("/api/events", _stop.Token);
                using var reader = new StreamReader(stream);
                var kind = "";
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                    {
                        kind = line["event: ".Length..];
                    }
                    else if (line.StartsWith("data: ", StringComparison.Ordinal) && kind is "agent")
                    {
                        var batch = JsonNode.Parse(line["data: ".Length..])!.AsObject();
                        lock (_events)
                        {
                            foreach (var item in batch["events"]!.AsArray().OfType<JsonObject>())
                            {
                                _events.Add((batch["key"]!.GetValue<string>(), item));
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
                when (exception is OperationCanceledException or IOException or HttpRequestException)
            {
                // The test is over.
            }
        }

        public async Task<JsonObject> WaitAsync(string key, Func<JsonObject, bool> match)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                lock (_events)
                {
                    if (_events.FirstOrDefault(e => e.Key == key && match(e.Event)).Event is { } found)
                    {
                        return found;
                    }
                }
                await Task.Delay(50, timeout.Token);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _reading.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
            _stop.Dispose();
        }
    }
}
