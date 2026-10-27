using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Core.Tasks;
using Harness.Core.Telemetry;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;
using Harness.Infrastructure.Telemetry;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Harness.Mcp.Context;
using Harness.Providers.Registry;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using Xunit;

namespace Harness.Integration.Telemetry;

[Collection("Provider processes")]
public sealed class TelemetryIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The awaited registry owns the fake provider."
    )]
    public async Task ContextRecordsProvenanceAndSyncScores()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write("a.cs", "class Caller\n{\n public void Run()\n {\n   Target();\n }\n}\n");
        fixture.Write("b.cs", "class Worker\n{\n public void Target()\n {\n   Old();\n }\n}\n");
        await fixture.CommitAsync("define methods");
        var location = await fixture.Lifecycle.LocateAsync(fixture.Root, cancellationToken: Token);
        await using var registry = new ProviderRegistry();
        registry.Register(
            new ProviderRegistryTests.Fake(
                "fixture",
                [Capabilities.SymbolSearch],
                execute: (_, _) =>
                    Task.FromResult(
                        new ProviderResult
                        {
                            Items =
                            [
                                new()
                                {
                                    Provider = "fixture",
                                    Name = "Caller",
                                    Path = "a.cs",
                                    StartLine = 1,
                                    EndLine = 7,
                                },
                            ],
                        }
                    )
            )
        );
        await using (var recorder = new TelemetryRecorder(fixture.DatabasePath, version: _ => "fixture-1"))
        {
            registry.SetObserver(recorder.Observe);
            await using var session = new HarnessStateSession(
                location,
                fixture.Lifecycle.Registry,
                new("test", "", "test", "test"),
                fixture.Lifecycle.GlobalConfigurationPath
            )
            {
                ContextObserver = recorder,
            };
            var answer = await new ContextTools(registry, session).CallAsync(
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"task":"Caller"}"""),
                Token
            );
            using var parsed = JsonDocument.Parse(answer.Json);
            parsed.RootElement.GetProperty("code").GetArrayLength().Should().BeGreaterThan(0);
        }
        using var database = fixture.Database();
        var store = new TelemetryStore(database);
        store.ContextStats(DateTimeOffset.UtcNow.AddDays(-1)).Open.Should().Be(1);
        database.GetDiagnostic("provider_version:fixture").Should().Be("fixture-1");
        var eventAt = DateTimeOffset.UtcNow.AddSeconds(1);
        store.RecordEvent(
            new(
                eventAt,
                "codex",
                "session",
                fixture.Worktree,
                "get_context",
                "mcp",
                "harness",
                "get_context",
                Task: "Caller"
            ),
            Token
        );
        store.RecordEvent(
            new(eventAt.AddSeconds(1), "codex", "session", fixture.Worktree, "Read", "read", Paths: ["a.cs", "b.cs"]),
            Token
        );
        fixture.Write("a.cs", "class Caller\n{\n public void Run()\n {\n   Target();\n   Log();\n }\n}\n");
        fixture.Write("b.cs", "class Worker\n{\n public void Target()\n {\n   New();\n }\n}\n");
        await fixture.CommitAsync("change both");
        await fixture.RunAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var actual = store.ContextStats(since);
        actual.Changed.Should().Be(2);
        actual.Hits.Should().Be(1);
        actual.CalleeMisses.Should().Be(1);
        actual.AfterRead.Touched.Should().Be(2);
        actual.AfterRead.Hits.Should().Be(1);
        await fixture.RunAsync();
        store.ContextStats(since).Changed.Should().Be(2);
    }

    [Fact]
    public async Task PublicMcpMetricsFlushOnDisconnectAndReportsStayLocal()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var binary = Path.Combine(
            ProviderFixture.RepositoryRoot,
            "src",
            "Harness.Cli",
            "bin",
            Directory.GetParent(ProviderFixture.Worker)!.Parent!.Name,
            "net10.0",
            "harness.dll"
        );
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["HARNESS_CONFIG_DIR"] = fixture.Global;
        await using (
            var client = await McpClient.CreateAsync(
                new StdioClientTransport(
                    new()
                    {
                        Command = "dotnet",
                        Arguments = [binary, "mcp", "--project", fixture.Root],
                        WorkingDirectory = fixture.Root,
                        EnvironmentVariables = environment,
                        InheritEnvironmentVariables = false,
                    }
                ),
                cancellationToken: Token
            )
        )
        {
            await client.CallToolAsync("ping", new Dictionary<string, object?>(), cancellationToken: Token);
            await client.CallToolAsync(
                "search_project",
                new Dictionary<string, object?> { ["query"] = "Widget" },
                cancellationToken: Token
            );
            (
                await client.CallToolAsync(
                    "search_project",
                    new Dictionary<string, object?> { ["query"] = 1 },
                    cancellationToken: Token
                )
            )
                .IsError.Should()
                .BeTrue();
        }
        // Transport EOF drains the independent server recorder before process exit; the CLI read retries only process shutdown.
        var metrics = await fixture.CliAsync("metrics", "--json");
        metrics.Code.Should().Be(0, metrics.Error);
        using var parsed = JsonDocument.Parse(metrics.Output);
        parsed
            .RootElement.GetProperty("tools")
            .EnumerateArray()
            .Sum(t => t.GetProperty("calls").GetInt32())
            .Should()
            .Be(3);
        parsed
            .RootElement.GetProperty("tools")
            .EnumerateArray()
            .Sum(t => t.GetProperty("errors").GetInt32())
            .Should()
            .Be(1);
        var stats = await fixture.CliAsync("context", "stats", "--json");
        stats.Code.Should().Be(0, stats.Error);
        using var context = JsonDocument.Parse(stats.Output);
        context.RootElement.GetProperty("calls").GetInt32().Should().Be(0);
        var report = await fixture.CliAsync("report", "--json");
        report.Code.Should().Be(0, report.Error);
        using var reports = JsonDocument.Parse(report.Output);
        reports
            .RootElement.GetProperty("projects")[0]
            .GetProperty("findings")
            .GetArrayLength()
            .Should()
            .BeGreaterThan(0);
        (await fixture.CliAsync("metrics")).Output.Should().Contain("MCP tools over the last 30 days");
        (await fixture.CliAsync("report", "--all", "--json")).Code.Should().Be(0);
    }

    [Fact]
    public async Task WriterErrorsAreContainedAndOldSchemasAreNotMigrated()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Sql("DELETE FROM schema_migrations WHERE version=20; DROP TABLE tool_calls");
        var recorder = new TelemetryRecorder(fixture.DatabasePath, 0);
        recorder.Observe(new ToolObservation(DateTimeOffset.UtcNow, "ping", "", "codex", 1, 1, false));
        await recorder.DisposeAsync();
        recorder.Failures.Should().Be(1);
        recorder.Observe(new ToolObservation(DateTimeOffset.UtcNow, "ping", "", "codex", 1, 1, false));
        using var database = HarnessDatabase.OpenReadOnly(fixture.DatabasePath);
        database.SchemaVersion().Should().Be(19);
    }

    [Fact]
    public async Task ExpiryLostHistoryAndTaskCompletionPreserveOutcomeLifecycle()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var location = await fixture.Lifecycle.LocateAsync(fixture.Root, cancellationToken: Token);
        var head = (await location.Repository!.ReadHeadAsync(Token)).Commit;
        using var db = fixture.Database();
        var unit = new ContextUnitObservation("a.cs", "A", "definition", "fixture", "code.symbol_search");
        new TelemetryStore(db, new FixedClock(DateTimeOffset.UtcNow.AddDays(-31))).RecordContext(
            new(fixture.Worktree, head, "expired", 1, 1, [unit]),
            Token
        );
        var store = new TelemetryStore(db);
        store.RecordContext(new(fixture.Worktree, "deadbeef", "lost", 1, 1, [unit]), Token);
        store.RecordContext(new(fixture.Worktree, head, "current", 1, 1, [unit]), Token);
        var resolved = await store.ResolveAsync(location.Repository, fixture.Worktree, head, "sync", Token);
        resolved.Expired.Should().Be(1);
        store.ContextStats(DateTimeOffset.UtcNow.AddDays(-40)).Open.Should().Be(1);
        fixture.Write("a.cs", "new body");
        await fixture.CommitAsync("finish work");
        var next = (await location.Repository.ReadHeadAsync(Token)).Commit;
        var tasks = new TaskStore(db);
        tasks.Import(
            [
                new()
                {
                    Id = "OBS-0001",
                    Title = "Complete observed work",
                    Type = "chore",
                    Priority = "medium",
                },
            ],
            "fixture",
            cancellationToken: Token
        );
        var claim = tasks.Claim("OBS-0001", new("human"), TimeSpan.FromMinutes(30), Token);
        await using var session = new HarnessStateSession(
            location,
            fixture.Lifecycle.Registry,
            new("test", "", "test", "test"),
            fixture.Lifecycle.GlobalConfigurationPath
        );
        var completed = await session.CompleteTaskAsync(
            "OBS-0001",
            new("human", claim.Token),
            new("Completed", next),
            Token
        );
        completed.Task.Status.Should().Be("done");
        var stats = store.ContextStats(DateTimeOffset.UtcNow.AddDays(-40));
        stats.Open.Should().Be(0);
        stats.Changed.Should().Be(1);
        stats.Hits.Should().Be(1);
        await session.PutMemoryAsync(
            new()
            {
                Type = "decision",
                Content = "Keep local context scoring deterministic",
                Force = true,
            },
            Token
        );
        var activity = store.Activity(DateTimeOffset.UtcNow.AddDays(-1), 20)!;
        activity.Should().Contain(item => item.Kind == "memory");
        activity
            .Should()
            .Contain(item => item.Kind == "task" && item.Title.StartsWith("OBS-0001 done:", StringComparison.Ordinal));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
