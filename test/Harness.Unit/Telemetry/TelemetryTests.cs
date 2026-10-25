using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Reports;
using Harness.Core.Telemetry;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Telemetry;
using Harness.Unit.Architecture;
using Xunit;

namespace Harness.Unit.Telemetry;

public sealed class TelemetryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task QueuesDropIncomingEventsIsolateSubscribersAndFlushOnClose()
    {
        await using var bus = new EventBus<int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> slow = [];
        List<int> fast = [];
        bus.Subscribe(
            "slow",
            async v =>
            {
                entered.TrySetResult();
                await release.Task;
                slow.Add(v);
            },
            1
        );
        bus.Subscribe(
            "fast",
            v =>
            {
                fast.Add(v);
                return Task.CompletedTask;
            },
            10
        );
        bus.Subscribe("broken", _ => throw new IOException("optional writer failed"), 10);
        bus.Publish(1);
        await entered.Task.WaitAsync(Token);
        bus.Publish(2);
        bus.Publish(3);
        bus.Dropped()["slow"].Should().Be(1);
        var closing = bus.DisposeAsync().AsTask();
        closing.IsCompleted.Should().BeFalse();
        release.TrySetResult();
        await closing.WaitAsync(Token);
        slow.Should().Equal(1, 2);
        fast.Should().Equal(1, 2, 3);
        bus.Failures.Should().Be(3);
        bus.Publish(4);
        await bus.DisposeAsync();
        fast.Should().HaveCount(3);
    }

    [Fact]
    public async Task RecorderRedactsBeforePersistenceAndNeverCreatesMissingStorage()
    {
        using var tree = new ReaderTree([]);
        var path = Path.Combine(tree.Root, "state.db");
        await using (var missing = new TelemetryRecorder(path))
        {
            missing.Observe(new ToolObservation(Now, "search_project", "", "codex", 1, 20, false));
        }

        File.Exists(path).Should().BeFalse();
        using var db = HarnessDatabase.Open(path, cancellationToken: Token);
        await using (var recorder = new TelemetryRecorder(path, version: _ => "1.2.3"))
        {
            recorder.Observe(
                new ToolObservation(Now, "search_project", "", "codex", 1, 20, false, "password=synthetic-value")
            );
            recorder.Observe(
                new Harness.Core.Providers.ProviderResult { Provider = "fixture", Capability = "code.symbol_search" },
                new("code.symbol_search", "password=synthetic-value")
            );
            recorder.Observe(
                new ContextObservation(
                    "wt",
                    "head",
                    "password=synthetic-value",
                    8000,
                    100,
                    [new("a.cs", "A", "definition", "fixture", "code.symbol_search")]
                )
            );
        }
        db.GetDiagnostic("provider_version:fixture").Should().Be("1.2.3");
        using var c = db.OpenConnection();
        using var command = c.CreateCommand();
        command.CommandText =
            "SELECT error FROM tool_calls UNION ALL SELECT query FROM provider_calls UNION ALL SELECT task FROM context_calls";
        using var reader = await command.ExecuteReaderAsync(Token);
        var count = 0;
        while (await reader.ReadAsync(Token))
        {
            reader.GetString(0).Should().NotContain("synthetic-value");
            count++;
        }
        count.Should().Be(3);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(20, 10)]
    public void NearestRankMedianUsesTheLowerMiddle(int count, int expected) =>
        TelemetryStore.Percentile(Enumerable.Range(1, count).Select(i => (long)i), 50).Should().Be(expected);

    [Fact]
    public void RetentionPrunesAtSourceBoundariesAndCascadesContextEvidence()
    {
        using var tree = new ReaderTree([]);
        using var db = HarnessDatabase.Open(Path.Combine(tree.Root, "state.db"), cancellationToken: Token);
        var store = new TelemetryStore(db, new Clock(Now));
        Sql(
            db,
            "INSERT INTO tool_calls(id,at,tool,kind,client,duration_ms,response_bytes,empty) VALUES(1,'2026-10-20T00:00:00Z','old','','',0,0,0),(20099,'2026-10-20T00:00:00Z','new','','',0,0,0)"
        );
        store.RecordTool(new(Now, "ping", "", "", 0, 0, false), Token);
        store.Metrics(Now.AddDays(-1)).Tools.Should().NotContain(t => t.Tool == "old");
        Sql(
            db,
            "INSERT INTO provider_calls(id,at,provider,capability,query,items,response_bytes,duration_ms) VALUES(1,'2026-10-20T00:00:00Z','old','code.symbol_search','',0,0,0),(10099,'2026-10-20T00:00:00Z','new','code.symbol_search','',0,0,0)"
        );
        store.RecordProvider(new(Now, "fixture", "code.symbol_search", "", 0, 0, 0, false), Token);
        store.Metrics(Now.AddDays(-1)).Providers.Should().NotContain(p => p.Provider == "old");
        store.RecordContext(
            new("wt", "head", "old", 1, 1, [new("old.cs", "Old", "definition", "fixture", "code.symbol_search")]),
            Token
        );
        SeedOutcomes(db, 1, [new("old.cs", 1)]);
        Sql(
            db,
            "INSERT INTO context_calls(id,at,worktree_id,head,task,budget,used) VALUES(2000,'2026-10-20T00:00:00Z','wt','head','last',1,1)"
        );
        store.RecordContext(
            new("wt", "head", "new", 1, 1, [new("new.cs", "New", "definition", "fixture", "code.symbol_search")]),
            Token
        );
        store.ContextStats(Now.AddDays(-1)).Changed.Should().Be(0);
        Sql(
            db,
            "INSERT INTO tool_events(id,at,client,session_id,worktree_id,tool,kind,mcp_server,mcp_tool,path) VALUES(1,'2026-10-20T00:00:00Z','old','s','wt','Read','read','','','a.cs'),(50099,'2026-10-20T00:00:00Z','new','s','wt','Read','read','','','a.cs')"
        );
        store.RecordEvent(new(Now, "codex", "s", "wt", "Read", "read", Paths: ["a.cs", "b.cs"]), Token);
        store.Metrics(Now.AddDays(-1)).Hooks.Events.Should().Be(3);
    }

    [Fact]
    public async Task EmptyVersionsRetryAndSuccessfulVersionIsRecordedOnlyOnce()
    {
        using var tree = new ReaderTree([]);
        using var db = HarnessDatabase.Open(Path.Combine(tree.Root, "state.db"), cancellationToken: Token);
        var probes = 0;
        await using (
            var recorder = new TelemetryRecorder(
                db.Path,
                version: _ => Interlocked.Increment(ref probes) is 1 ? "" : "v2"
            )
        )
        {
            for (var i = 0; i < 3; i++)
            {
                recorder.Observe(
                    new Harness.Core.Providers.ProviderResult
                    {
                        Provider = "fixture",
                        Capability = "code.symbol_search",
                    },
                    new("code.symbol_search")
                );
            }
        }

        probes.Should().Be(2);
        db.GetDiagnostic("provider_version:fixture").Should().Be("v2");
    }

    [Fact]
    public void ProviderBypassesReadClientMetadataWithoutChangingConfiguration()
    {
        using var tree = new ReaderTree(
            new()
            {
                [".mcp.json"] =
                    """{"mcpServers":{"alpha-mcp":{"command":"unrelated"},"same-executable":{"command":"/synthetic/tool"},"harness":{"command":"tool"}}}""",
                ["claude.json"] = """{"mcpServers":{"alpha":{"command":"unrelated"}}}""",
                ["codex.toml"] = """
                [mcp_servers.beta]
                command = "beta-tool"
                """,
            }
        );
        var providers = new Dictionary<string, Harness.Core.Providers.ProviderConfiguration>
        {
            ["alpha"] = new() { Command = "tool" },
            ["beta"] = new() { Command = "beta-tool" },
        };
        var before = File.ReadAllBytes(Path.Combine(tree.Root, ".mcp.json"));
        var map = ProviderServerMap.Read(
            tree.Root,
            providers,
            Path.Combine(tree.Root, "claude.json"),
            Path.Combine(tree.Root, "codex.toml")
        );
        map.Should().HaveCount(4);
        map["same-executable"].Should().Be("alpha");
        map.Should().NotContainKey("harness");
        File.ReadAllBytes(Path.Combine(tree.Root, ".mcp.json")).Should().Equal(before);
        File.WriteAllText(Path.Combine(tree.Root, ".mcp.json"), """{"mcpServers":{"bad":{"command":42}}}""");
        ProviderServerMap
            .Read(tree.Root, providers, Path.Combine(tree.Root, "absent.json"), Path.Combine(tree.Root, "absent.toml"))
            .Should()
            .BeEmpty();
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Constant test-owned SQL seeds ID gaps to exercise retention boundaries."
    )]
    private static void Sql(HarnessDatabase db, string sql)
    {
        using var c = db.OpenConnection();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only constant fixture queries are used."
    )]
    private static void SeedOutcomes(HarnessDatabase db, long id, KeyValuePair<string, int>[] outcomes)
    {
        using var c = db.OpenConnection();
        foreach (var (path, hit) in outcomes)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "INSERT INTO context_outcomes(call_id,path,language,hit) VALUES($id,$path,$language,$hit)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$language", TelemetryStore.Language(path));
            cmd.Parameters.AddWithValue("$hit", hit);
            cmd.ExecuteNonQuery();
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
