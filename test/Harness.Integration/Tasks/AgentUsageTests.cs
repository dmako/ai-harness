using AwesomeAssertions;
using Harness.Core.Hooks;
using Harness.Core.Tasks;
using Harness.Infrastructure.Agents;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Xunit;

namespace Harness.Integration.Tasks;

/// <summary>
/// Agent session usage read from fixture transcripts (TASK-0114): Claude Code transcripts with a resumed session's
/// copied history, repeated message lines and a subagent, and Codex rollouts with cumulative token counts.
/// </summary>
public sealed class AgentUsageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Nine = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    private static string At(int minute, int second = 0) =>
        Nine.AddMinutes(minute).AddSeconds(second).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private static string Prompt(string session, int minute, int second = 0) =>
        $$"""{"type":"user","sessionId":"{{session}}","timestamp":"{{At(minute, second)}}","message":{"role":"user","content":"PRIVATE PROMPT"} }""";

    private static string ToolResult(string session, int minute, int second = 0) =>
        $$"""{"type":"user","sessionId":"{{session}}","timestamp":"{{At(minute, second)}}","message":{"role":"user","content":[{"type":"tool_result","content":"PRIVATE OUTPUT"}]} }""";

    private static string Assistant(
        string session,
        int minute,
        int second,
        string id,
        string model,
        int input,
        int output,
        int cacheRead = 0,
        int cacheWrite = 0,
        int thinking = 0
    ) =>
        $$"""{"type":"assistant","sessionId":"{{session}}","timestamp":"{{At(minute, second)}}","message":{"id":"{{id}}","model":"{{model}}","content":[{"type":"text","text":"PRIVATE ANSWER"}],"usage":{"input_tokens":{{input}},"cache_read_input_tokens":{{cacheRead}},"cache_creation_input_tokens":{{cacheWrite}},"output_tokens":{{output}},"output_tokens_details":{"thinking_tokens":{{thinking}} } } } }""";

    private static string Codex(int minute, int second, string type, string payload) =>
        $$"""{"timestamp":"{{At(minute, second)}}","type":"{{type}}","payload":{{payload}}}""";

    private static string TokenCount(int minute, int second, int input, int cached, int output, int reasoning) =>
        Codex(
            minute,
            second,
            "event_msg",
            $$"""{"type":"token_count","info":{"total_token_usage":{"input_tokens":{{input}},"cached_input_tokens":{{cached}},"cache_write_input_tokens":0,"output_tokens":{{output}},"reasoning_output_tokens":{{reasoning}} } } }"""
        );

    [Fact]
    public void AClaudeSessionCountsEachMessageOnceSkipsResumedHistoryAndAddsItsSubagents()
    {
        using var s = new State("TASK-0001");
        const string Session = "c1";
        var main = s.Claude(
            Session,
            // the earlier session's history a resumed session's file starts with
            Assistant("old", -30, 0, "msg_old", "claude-opus-5-5", 1000, 1000),
            Prompt(Session, 60),
            // one message, its blocks on two lines with the same usage
            Assistant(Session, 60, 5, "msg_1", "claude-opus-5-5", 10, 20, 100, 50, 5),
            Assistant(Session, 60, 6, "msg_1", "claude-opus-5-5", 10, 20, 100, 50, 5),
            ToolResult(Session, 60, 10),
            Assistant(Session, 60, 20, "msg_2", "claude-opus-5-5", 1, 30),
            // half an hour idle, then a new turn on a dated model id
            Prompt(Session, 90),
            Assistant(Session, 90, 10, "msg_3", "claude-haiku-5-5-20261001", 5, 5)
        );
        s.Subagent(
            Session,
            "agent-a1",
            Prompt(Session, 60, 12),
            Assistant(Session, 60, 15, "msg_s", "claude-opus-5-5", 100, 100)
        );
        s.Record("claude-code", Session, "TASK-0001", HookTaskActions.Claim, 59);
        s.Record("claude-code", Session, "TASK-0001", HookTaskActions.Complete, 100);

        s.Refresh().Should().Be(1);

        var session = s.Session("TASK-0001");
        (session.InputTokens, session.CacheReadTokens, session.CacheWriteTokens, session.OutputTokens)
            .Should()
            .Be((116L, 100L, 50L, 155L));
        session.ReasoningTokens.Should().Be(5);
        session.Model.Should().Be("claude-haiku-5-5-20261001, claude-opus-5-5");
        session.ActiveMs.Should().Be(33_000, "20 s and 10 s of turns and 3 s of the subagent; not the idle half hour");
        (session.FirstActivity, session.LastActivity).Should().Be(("2026-11-08T10:00:00Z", "2026-11-08T10:30:10Z"));
        session.TranscriptPath.Should().Be(main);
        var opus = (111 * 4.0 + 100 * 0.2 + 50 * 5.0 + 150 * 20.0) / 1e6;
        var haiku = (5 * 0.1 + 5 * 0.5) / 1e6;
        session.CostUsd.Should().BeApproximately(opus + haiku, 1e-12);
        s.StoredText().Should().NotContain("PRIVATE");
    }

    [Fact]
    public void LaterReadsContinueFromTheOffsetAndARotatedFileIsReadAgain()
    {
        using var s = new State("TASK-0001");
        const string Session = "c2";
        var main = s.Claude(
            Session,
            Prompt(Session, 60),
            Assistant(Session, 60, 10, "msg_1", "claude-opus-5-5", 10, 10)
        );
        s.Record("claude-code", Session, "TASK-0001", HookTaskActions.Claim, 59);
        s.Refresh();
        s.Session("TASK-0001").InputTokens.Should().Be(10);

        // a line still being written is left for the next read
        File.AppendAllText(
            main,
            Assistant(Session, 61, 0, "msg_2", "claude-opus-5-5", 7, 3) + "\n" + "{\"type\":\"assi"
        );
        s.Refresh();
        s.Session("TASK-0001").InputTokens.Should().Be(17);
        s.Refresh();
        s.Session("TASK-0001").InputTokens.Should().Be(17, "nothing new was read twice");

        File.WriteAllText(
            main,
            Prompt(Session, 62) + "\n" + Assistant(Session, 62, 5, "msg_9", "claude-opus-5-5", 2, 2) + "\n"
        );
        s.Refresh();
        (s.Session("TASK-0001").InputTokens, s.Session("TASK-0001").OutputTokens).Should().Be((2L, 2L));
    }

    [Fact]
    public void CodexCumulativeTokenCountsBecomeDeltasAndTurnsGiveActiveTime()
    {
        using var s = new State("TASK-0001");
        const string Thread = "01a1b887-9e86-7943-b0c9-8aa4caa8d75a";
        s.CodexRollout(
            Thread,
            Codex(60, 0, "session_meta", $$"""{"id":"{{Thread}}","cwd":"C:\\repo"}"""),
            Codex(60, 0, "turn_context", """{"model":"gpt-test","cwd":"C:\\repo"}"""),
            Codex(60, 1, "event_msg", """{"type":"task_started"}"""),
            Codex(60, 2, "response_item", """{"type":"message","content":"PRIVATE"}"""),
            TokenCount(60, 30, 1000, 800, 50, 10),
            TokenCount(61, 0, 3000, 2500, 120, 30),
            Codex(
                61,
                1,
                "event_msg",
                """{"type":"task_complete","duration_ms":60000,"last_agent_message":"PRIVATE"}"""
            ),
            // a resumed thread in a new process counts from zero again
            TokenCount(80, 0, 500, 0, 5, 0)
        );
        s.Record("codex", Thread, "TASK-0001", HookTaskActions.Claim, 59);

        s.Refresh(
            new PriceConfiguration
            {
                Input = 1,
                CachedInput = 0.1,
                Output = 10,
            }
        );

        var session = s.Session("TASK-0001");
        (session.InputTokens, session.CacheReadTokens, session.OutputTokens, session.ReasoningTokens)
            .Should()
            .Be((1000L, 2500L, 125L, 30L));
        session.ActiveMs.Should().Be(60_000);
        session.Model.Should().Be("gpt-test");
        session.CostUsd.Should().BeApproximately((1000 * 1 + 2500 * 0.1 + 125 * 10.0) / 1e6, 1e-12);
        s.StoredText().Should().NotContain("PRIVATE");
    }

    [Fact]
    public void ASessionsUsageIsSharedBetweenItsTasksByTimeAndAnUnknownModelHasNoPrice()
    {
        using var s = new State("TASK-0001", "TASK-0002");
        const string Session = "c3";
        s.Claude(
            Session,
            Prompt(Session, 60),
            Assistant(Session, 70, 0, "msg_a", "claude-opus-5-5", 1, 0),
            Assistant(Session, 82, 0, "msg_lead", "claude-opus-5-5", 20, 0),
            Assistant(Session, 110, 0, "msg_b", "model-nobody-prices", 300, 0)
        );
        s.Record("claude-code", Session, "TASK-0001", HookTaskActions.Claim, 60);
        s.Record("claude-code", Session, "TASK-0001", HookTaskActions.Complete, 80);
        s.Record("claude-code", Session, "TASK-0002", HookTaskActions.Claim, 85);

        s.Refresh();

        s.Session("TASK-0001").InputTokens.Should().Be(1);
        var second = s.Session("TASK-0002");
        second.InputTokens.Should().Be(320, "the work leading to the claim and all after it while still working");
        second.CostUsd.Should().BeNull("a model it used has no price");
    }

    [Fact]
    public void ASessionWithoutATranscriptKeepsNoUsageAndNoError()
    {
        using var s = new State("TASK-0001");
        s.Record("codex", "no-such-thread", "TASK-0001", HookTaskActions.Claim, 0);
        s.Refresh().Should().Be(1);
        var session = s.Session("TASK-0001");
        (session.InputTokens, session.CostUsd, session.TranscriptPath).Should().Be((0L, (double?)null, ""));
    }

    [Fact]
    public void ThePriceTableShipsClaudeModelsAndTheConfigurationOverridesOrAddsModels()
    {
        var shipped = ConfigurationLoader.Prices(new HarnessConfiguration());
        shipped.Find("claude-opus-5-5").Should().Be(new ModelPrice(4, 0.2, 5, 20));
        shipped.Find("CLAUDE-SONNET-5-5-20261001").Should().Be(shipped.Find("claude-sonnet-5-5"));
        shipped.Find("claude-opus-5").Should().Be(new ModelPrice(5, 0.5, 6.25, 25), "not the longer opus-5-5 entry");
        shipped.Find("gpt-test").Should().BeNull();

        var configured = ConfigurationLoader.Prices(
            new HarnessConfiguration
            {
                Pricing = new()
                {
                    ["claude-opus-5-5"] = new PriceConfiguration { Output = 30 },
                    ["gpt-test"] = new PriceConfiguration { Input = 2, Output = 8 },
                },
            }
        );
        configured.Find("claude-opus-5-5").Should().Be(new ModelPrice(4, 0.2, 5, 30));
        configured.Find("gpt-test").Should().Be(new ModelPrice(2, 2, 2, 8));
    }

    [Fact]
    public void NegativePricesAreRefusedInTheConfiguration()
    {
        using var s = new State();
        var path = Path.Combine(s.Directory, "config.toml");
        File.WriteAllText(path, "[pricing.\"gpt-test\"]\ninput = -1\n");
        FluentActions
            .Invoking(() => ConfigurationLoader.LoadFiles(path, ""))
            .Should()
            .Throw<InvalidDataException>()
            .WithMessage("*pricing.gpt-test*");
        File.WriteAllText(path, "[pricing.\"gpt-test\"]\ninput = 1.5\ncached_input = 0.15\noutput = 6\n");
        ConfigurationLoader.LoadFiles(path, "").Value.Pricing["gpt-test"].CachedInput.Should().Be(0.15);
    }

    private sealed class State : IDisposable
    {
        public State(params string[] tasks)
        {
            Directory = Path.Combine(Path.GetTempPath(), "harness-usage-" + Guid.NewGuid().ToString("N"));
            Roots = new(Path.Combine(Directory, "claude", "projects"), Path.Combine(Directory, "codex"));
            System.IO.Directory.CreateDirectory(Directory);
            Database = HarnessDatabase.Open(Path.Combine(Directory, "h.db"), cancellationToken: Token);
            Store = new(Database);
            Store.Import(
                tasks.Select(id => new TaskDefinition { Id = id, Title = id }).ToArray(),
                "fixture",
                cancellationToken: Token
            );
        }

        public string Directory { get; }
        public TranscriptRoots Roots { get; }
        public HarnessDatabase Database { get; }
        public TaskStore Store { get; }

        public string Claude(string session, params string[] lines) =>
            Write(Path.Combine(Roots.ClaudeProjects, "C--repo", session + ".jsonl"), lines);

        public void Subagent(string session, string agent, params string[] lines) =>
            Write(Path.Combine(Roots.ClaudeProjects, "C--repo", session, "subagents", agent + ".jsonl"), lines);

        public void CodexRollout(string thread, params string[] lines) =>
            Write(
                Path.Combine(
                    Roots.CodexHome,
                    "sessions",
                    "2026",
                    "11",
                    "08",
                    $"rollout-2026-11-08T10-00-00-{thread}.jsonl"
                ),
                lines
            );

        private static string Write(string path, string[] lines)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            return path;
        }

        public void Record(string client, string session, string task, string action, int minute) =>
            Store.RecordSession(client, session, task, action, Nine.AddMinutes(minute), Token);

        public int Refresh(PriceConfiguration? gpt = null) =>
            AgentUsageRefresh.Refresh(
                Database,
                ConfigurationLoader.Prices(
                    new HarnessConfiguration { Pricing = gpt is null ? new() : new() { ["gpt-test"] = gpt } }
                ),
                Roots,
                cancellationToken: Token
            );

        public AgentSession Session(string task) => Store.Sessions(task, Token).Should().ContainSingle().Subject;

        public string StoredText()
        {
            using var connection = Database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT group_concat(state,' ') || (SELECT group_concat(model,' ') FROM agent_usage) FROM agent_transcripts";
            return command.ExecuteScalar() as string ?? "";
        }

        public void Dispose()
        {
            Database.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
