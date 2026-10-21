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
    private static readonly string[] ToolNames = typeof(Harness.Mcp.HarnessMcpServer).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("Harness.Mcp.Schemas.", StringComparison.Ordinal)).Select(n => n["Harness.Mcp.Schemas.".Length..^5]).Order(StringComparer.Ordinal).ToArray();
    [Theory]
    [InlineData("activity")]
    [InlineData("metrics")]
    [InlineData("stats")]
    [InlineData("trends")]
    [InlineData("build")]
    [InlineData("bypasses")]
    public async Task MetricsContextTrendsAndReportsMatchFrozenGo(string mode)
    {
        var oracle = Oracle();
        using var tree = new ReaderTree([]);
        var path = Path.Combine(tree.Root, "state.db");
        using var database = HarnessDatabase.Open(path, cancellationToken: Token);
        var clock = new Clock(Now.AddHours(-1));
        var store = new TelemetryStore(database, clock);
        var since = Now.AddDays(-4);
        for (var i = 0; i < 20; i++)
        {
            store.RecordTool(new(Now.AddDays(-3).AddMinutes(i), i % 2 is 0 ? "search_project" : "get_context", i % 3 is 0 ? "impact [workspace deep]" : "[workspace light]", i % 2 is 0 ? "codex" : "claude-code", i * 750, i * 55, i % 4 is 0, i % 5 is 0 ? "provider unavailable" : ""), Token);
            store.RecordProvider(new(Now.AddDays(-3).AddMinutes(i), "fixture", "code.symbol_search", "Widget", i % 3 is 0 ? 0 : 3, i * 100, i < 10 ? i * 1500 : 100, i is 2, i < 8 ? "unavailable" : ""), Token);
        }
        var units = new ContextUnitObservation[] { new("src/Widget.cs", "Widget", "definition", "fixture", "code.symbol_search", 2, 0, 0, true), new("src/Widget.cs", "Widget", "definition", "fixture", "code.references", 1, 1, 1), new("src/Widget.cs", "Widget", "definition", "peer", "code.symbol_search", 3, 2, 1) };
        store.RecordContext(new("wt", "head", "Fix Widget", 8000, 5000, units), Token);
        store.RecordEvent(new(Now.AddMinutes(-59), "codex", "session", "wt", "get_context", "mcp", "harness", "get_context", Task: "Fix Widget"), Token);
        store.RecordEvent(new(Now.AddMinutes(-58), "codex", "session", "wt", "Read", "read", Paths: ["src/Widget.cs", "src/Missed.cs"]), Token);
        store.RecordEvent(new(Now.AddMinutes(-57), "codex", "session", "wt", "Edit", "edit", Paths: ["src/Widget.cs"]), Token);
        store.RecordEvent(new(Now.AddMinutes(-56), "codex", "session", "wt", "mcp", "mcp", "fixture-mcp", "search"), Token);
        SeedOutcomes(database, 1, [new("src/Widget.cs", 1), new("src/Missed.cs", 0), new("test/WidgetTests.cs", 0), new(".harness/ignored.db", 0)]);
        var servers = new Dictionary<string, string> { ["fixture-mcp"] = "fixture" };
        object? actual = mode switch { "metrics" => store.Metrics(since), "stats" => store.ContextStats(since), "trends" => store.Trends(since, Now), "build" => store.Report(4, Now, ToolNames, servers), "activity" => store.Activity(since, 20), _ => store.Bypasses(since, servers) };
        var expected = await RunOracleAsync(oracle, new { mode, db = path, since, now = Now, days = 4, servers });
        Equal(actual, expected);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task ThresholdsAndRecoveryJudgmentsMatchFrozenGo(int scenario)
    {
        var report = new LocalReport { Days = 30, Context = new() { Calls = 5, Changed = 20, Hits = 5, HitRate = .25 }, Metrics = new() { Hooks = new() { Events = 1 } } };
        switch (scenario)
        {
            case 0:
                report.Context.Calls = 4;
                break;
            case 1:
                report.Context.HitRate = .249;
                report.Context.Language["csharp"] = [0, 10];
                report.Context.Language["other"] = [0, 20];
                break;
            case 2:
                report.Context.Changed = 25;
                report.Context.CalleeMisses = 3;
                break;
            case 3:
                report.Context.Changed = 25;
                report.Context.CalleeMisses = 2;
                break;
            case 4:
                report.Context.AfterRead = new() { Touched = 10, Hits = 2, HitRate = .2 };
                break;
            case 5:
                report.Metrics.Providers = [new() { Provider = "fixture", Capability = "code.references", Calls = 5, ErrorRate = .5, P95MS = 10000 }];
                break;
            case 6:
                report.Metrics.Providers = [new() { Provider = "fixture", Capability = "code.references", Calls = 5, ErrorRate = .1, P95MS = 3000 }];
                break;
            case 7:
                report.Metrics.Providers = [new() { Provider = "fixture", Capability = "code.references", Calls = 4, ErrorRate = 1, P95MS = 10000 }];
                break;
            case 8:
                report.Metrics.Providers = [new() { Provider = "fixture", Capability = "code.references", Calls = 20, ErrorRate = .5, EmptyRate = .8, P95MS = 10000 }];
                report.Trends = [new() { Provider = "fixture", Capability = "code.references", LastCall = "2026-10-20T11:00:00Z", LastError = "2026-10-18T10:00:00Z", LastSlow = "2026-10-19T12:00:00Z", CallsAfterError = 10, CallsAfterSlow = 2, Days = [new() { Day = "2026-10-18", Calls = 10, Errors = 10, Slow = 5 }, new() { Day = "2026-10-19" }, new() { Day = "2026-10-20", Calls = 10 }] }];
                break;
            case 9:
                report.Metrics.Tools = [new() { Tool = "get_context", Calls = 20, Kind = "[workspace deep]", ErrorRate = .1 }];
                report.Knowledge = new() { Pending = 2, OldestPending = Now.AddDays(-8) };
                break;
            case 10:
                report.Metrics.Bypasses = [new() { Provider = "fixture", Server = "fixture-mcp", Tool = "search", Calls = 5 }, new() { Provider = "fixture", Server = "other", Tool = "search", Calls = 4 }];
                break;
            case 11:
                report.Knowledge = new() { Pending = 1, OldestPending = Now.AddDays(-7) };
                break;
        }
        Equal(ReportAnalyzer.Findings(report, Now, ToolNames), await RunOracleAsync(Oracle(), new { mode = "findings", report, now = Now }));
    }
    [Fact]
    public async Task QueuesDropIncomingEventsIsolateSubscribersAndFlushOnClose()
    {
        await using var bus = new EventBus<int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> slow = [];
        List<int> fast = [];
        bus.Subscribe("slow", async v => { entered.TrySetResult(); await release.Task; slow.Add(v); }, 1);
        bus.Subscribe("fast", v => { fast.Add(v); return Task.CompletedTask; }, 10);
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
            recorder.Observe(new ToolObservation(Now, "search_project", "", "codex", 1, 20, false, "password=synthetic-value"));
            recorder.Observe(new Harness.Core.Providers.ProviderResult { Provider = "fixture", Capability = "code.symbol_search" }, new("code.symbol_search", "password=synthetic-value"));
            recorder.Observe(new ContextObservation("wt", "head", "password=synthetic-value", 8000, 100, [new("a.cs", "A", "definition", "fixture", "code.symbol_search")]));
        }
        db.GetDiagnostic("provider_version:fixture").Should().Be("1.2.3");
        using var c = db.OpenConnection();
        using var command = c.CreateCommand();
        command.CommandText = "SELECT error FROM tool_calls UNION ALL SELECT query FROM provider_calls UNION ALL SELECT task FROM context_calls";
        using var reader = await command.ExecuteReaderAsync(Token);
        var count = 0;
        while (await reader.ReadAsync(Token))
        { reader.GetString(0).Should().NotContain("synthetic-value"); count++; }
        count.Should().Be(3);
    }
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(20, 10)]
    public void NearestRankMedianHasTheGoLowerMiddle(int count, int expected) => TelemetryStore.Percentile(Enumerable.Range(1, count).Select(i => (long)i), 50).Should().Be(expected);
    [Fact]
    public void RetentionPrunesAtSourceBoundariesAndCascadesContextEvidence()
    {
        using var tree = new ReaderTree([]);
        using var db = HarnessDatabase.Open(Path.Combine(tree.Root, "state.db"), cancellationToken: Token);
        var store = new TelemetryStore(db, new Clock(Now));
        Sql(db, "INSERT INTO tool_calls(id,at,tool,kind,client,duration_ms,response_bytes,empty) VALUES(1,'2026-10-20T00:00:00Z','old','','',0,0,0),(20099,'2026-10-20T00:00:00Z','new','','',0,0,0)");
        store.RecordTool(new(Now, "ping", "", "", 0, 0, false), Token);
        store.Metrics(Now.AddDays(-1)).Tools.Should().NotContain(t => t.Tool == "old");
        Sql(db, "INSERT INTO provider_calls(id,at,provider,capability,query,items,response_bytes,duration_ms) VALUES(1,'2026-10-20T00:00:00Z','old','code.symbol_search','',0,0,0),(10099,'2026-10-20T00:00:00Z','new','code.symbol_search','',0,0,0)");
        store.RecordProvider(new(Now, "fixture", "code.symbol_search", "", 0, 0, 0, false), Token);
        store.Metrics(Now.AddDays(-1)).Providers.Should().NotContain(p => p.Provider == "old");
        store.RecordContext(new("wt", "head", "old", 1, 1, [new("old.cs", "Old", "definition", "fixture", "code.symbol_search")]), Token);
        SeedOutcomes(db, 1, [new("old.cs", 1)]);
        Sql(db, "INSERT INTO context_calls(id,at,worktree_id,head,task,budget,used) VALUES(2000,'2026-10-20T00:00:00Z','wt','head','last',1,1)");
        store.RecordContext(new("wt", "head", "new", 1, 1, [new("new.cs", "New", "definition", "fixture", "code.symbol_search")]), Token);
        store.ContextStats(Now.AddDays(-1)).Changed.Should().Be(0);
        Sql(db, "INSERT INTO tool_events(id,at,client,session_id,worktree_id,tool,kind,mcp_server,mcp_tool,path) VALUES(1,'2026-10-20T00:00:00Z','old','s','wt','Read','read','','','a.cs'),(50099,'2026-10-20T00:00:00Z','new','s','wt','Read','read','','','a.cs')");
        store.RecordEvent(new(Now, "codex", "s", "wt", "Read", "read", Paths: ["a.cs", "b.cs"]), Token);
        store.Metrics(Now.AddDays(-1)).Hooks.Events.Should().Be(3);
    }
    [Fact]
    public async Task EmptyVersionsRetryAndSuccessfulVersionIsRecordedOnlyOnce()
    {
        using var tree = new ReaderTree([]);
        using var db = HarnessDatabase.Open(Path.Combine(tree.Root, "state.db"), cancellationToken: Token);
        var probes = 0;
        await using (var recorder = new TelemetryRecorder(db.Path, version: _ => Interlocked.Increment(ref probes) is 1 ? "" : "v2"))
        {
            for (var i = 0; i < 3; i++)
            {
                recorder.Observe(new Harness.Core.Providers.ProviderResult { Provider = "fixture", Capability = "code.symbol_search" }, new("code.symbol_search"));
            }
        }

        probes.Should().Be(2);
        db.GetDiagnostic("provider_version:fixture").Should().Be("v2");
    }
    [Fact]
    public void ProviderBypassesReadClientMetadataWithoutChangingConfiguration()
    {
        using var tree = new ReaderTree(new()
        {
            [".mcp.json"] = """{"mcpServers":{"alpha-mcp":{"command":"unrelated"},"same-executable":{"command":"/synthetic/tool"},"harness":{"command":"tool"}}}""",
            ["claude.json"] = """{"mcpServers":{"alpha":{"command":"unrelated"}}}""",
            ["codex.toml"] = """
                [mcp_servers.beta]
                command = "beta-tool"
                """,
        });
        var providers = new Dictionary<string, Harness.Core.Providers.ProviderConfiguration> { ["alpha"] = new() { Command = "tool" }, ["beta"] = new() { Command = "beta-tool" } };
        var before = File.ReadAllBytes(Path.Combine(tree.Root, ".mcp.json"));
        var map = ProviderServerMap.Read(tree.Root, providers, Path.Combine(tree.Root, "claude.json"), Path.Combine(tree.Root, "codex.toml"));
        map.Should().HaveCount(4);
        map["same-executable"].Should().Be("alpha");
        map.Should().NotContainKey("harness");
        File.ReadAllBytes(Path.Combine(tree.Root, ".mcp.json")).Should().Equal(before);
        File.WriteAllText(Path.Combine(tree.Root, ".mcp.json"), """{"mcpServers":{"bad":{"command":42}}}""");
        ProviderServerMap.Read(tree.Root, providers, Path.Combine(tree.Root, "absent.json"), Path.Combine(tree.Root, "absent.toml")).Should().BeEmpty();
    }
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Constant test-owned SQL seeds ID gaps to exercise retention boundaries.")]
    private static void Sql(HarnessDatabase db, string sql)
    {
        using var c = db.OpenConnection();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only constant fixture queries are used.")]
    private static void SeedOutcomes(HarnessDatabase db, long id, KeyValuePair<string, int>[] outcomes)
    {
        using var c = db.OpenConnection();
        foreach (var (path, hit) in outcomes)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO context_outcomes(call_id,path,language,hit) VALUES($id,$path,$language,$hit)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$language", TelemetryStore.Language(path));
            cmd.Parameters.AddWithValue("$hit", hit);
            cmd.ExecuteNonQuery();
        }
    }
    private static void Equal(object? actual, JsonNode? expected)
    {
        var value = JsonSerializer.SerializeToNode(actual, TelemetryJson.Options);
        JsonNode.DeepEquals(value, expected).Should().BeTrue($"C#: {value}\nGo: {expected}");
    }
    private static string Oracle() => Environment.GetEnvironmentVariable("HARNESS_GO_TELEMETRY_ORACLE") is { Length: > 0 } path ? path : throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_TELEMETRY_ORACLE using Telemetry/build_oracle.py.");
    [SuppressMessage("Security", "S4036:Searching OS commands in PATH is security-sensitive", Justification = "The explicitly configured frozen oracle runs without a shell.")]
    private static async Task<JsonNode?> RunOracleAsync(string executable, object request)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, TelemetryJson.Options));
            process.StandardInput.Close();
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
            process.ExitCode.Should().Be(0, await stderr);
            return JsonNode.Parse(await stdout);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
