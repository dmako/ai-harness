using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Graphify;
using Harness.Core.Providers;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Graphify;
using Harness.Infrastructure.Processes;
using Harness.Providers.Graphify;
using Harness.Providers.Mcp;
using Harness.Providers.Packs;
using Xunit;

namespace Harness.Integration.Providers;

[Collection("Provider processes")]
public sealed class GraphifySessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StaleCleanupChecksPidDescriptorIdentityAndRootAndPreservesLiveOrUnknownOwners()
    {
        using var fixture = new GraphifyFixture();
        var other = Directory.CreateDirectory(Path.Combine(fixture.DirectoryRoot, "other")).FullName;
        var gone = Path.Combine(fixture.DirectoryRoot, "gone");
        object Row(string id, string root, int pid) =>
            new
            {
                session_id = id,
                process_id = pid,
                repository_root = root,
                reachability = "stale",
            };
        fixture.Write(
            "sessions.json",
            JsonSerializer.Serialize(
                new
                {
                    sessions = new[]
                    {
                        Row("dead", fixture.Root, 999999),
                        Row("deleted", gone, 999999),
                        Row("other", other, 999999),
                        Row("live", fixture.Root, Environment.ProcessId),
                        Row("changed", fixture.Root, 999999),
                        Row("unknown", fixture.Root, 0),
                        Row("../unsafe", fixture.Root, 999999),
                    },
                }
            )
        );
        foreach (var id in new[] { "dead", "deleted", "other", "live", "changed", "unknown" })
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.Descriptors, id + ".json"),
                JsonSerializer.Serialize(
                    new
                    {
                        session_id = id,
                        process_id = id switch
                        {
                            "live" or "changed" => Environment.ProcessId,
                            "unknown" => 0,
                            _ => 999999,
                        },
                    }
                ),
                Token
            );
        }

        await using var manager = fixture.Manager();
        (await manager.CleanAsync(Token)).Should().Be(2);
        File.Exists(Path.Combine(fixture.Descriptors, "dead.json")).Should().BeFalse();
        File.Exists(Path.Combine(fixture.Descriptors, "deleted.json")).Should().BeFalse();
        foreach (var id in new[] { "other", "live", "changed", "unknown" })
        {
            File.Exists(Path.Combine(fixture.Descriptors, id + ".json")).Should().BeTrue(id);
        }
    }

    [Fact]
    public async Task RecentListsShareFiveSecondCacheAndTouchAndDiscoveryWritesAreThrottled()
    {
        using var fixture = new GraphifyFixture();
        fixture.Ready("external");
        var clock = new Clock();
        await using var manager = fixture.Manager(clock);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => manager.ListAsync(true, Token)));
        fixture.Calls().Count(call => call[0] is "ps").Should().Be(1);
        await manager.TouchAsync("external", Token);
        var first = await File.ReadAllTextAsync(fixture.RecordsPath, Token);
        clock.Now += TimeSpan.FromSeconds(10);
        await manager.TouchAsync("external", Token);
        (await File.ReadAllTextAsync(fixture.RecordsPath, Token)).Should().Be(first);
        await manager.NoteDiscoveryAsync("external", "incomplete", Token);
        first = await File.ReadAllTextAsync(fixture.RecordsPath, Token);
        await manager.NoteDiscoveryAsync("external", "incomplete", Token);
        (await File.ReadAllTextAsync(fixture.RecordsPath, Token)).Should().Be(first);
        clock.Now += TimeSpan.FromMinutes(1);
        await manager.TouchAsync("external", Token);
        fixture.Records()["sessions"]!["external"]!["last_query"]!
            .GetValue<string>()
            .Should()
            .NotBe(JsonNode.Parse(first)!["sessions"]!["external"]!["last_query"]!.GetValue<string>());
        await manager.ListAsync(true, Token);
        fixture.Calls().Count(call => call[0] is "ps").Should().Be(2);
    }

    [Fact]
    public async Task SweepStopsOnlyIdleHarnessOwnedSessionsAndForgetsGoneRecords()
    {
        using var fixture = new GraphifyFixture();
        var rows = new[] { "idle", "busy", "byhand", "unknown" }.Select(id => new
        {
            session_id = id,
            process_id = Environment.ProcessId,
            reachability = "reachable",
            ready = true,
            repository_root = fixture.Root,
        });
        fixture.Write("sessions.json", JsonSerializer.Serialize(new { sessions = rows }));
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.RecordsPath)!);
        var old = DateTimeOffset.UtcNow.AddHours(-3).ToString("O", CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(
            fixture.RecordsPath,
            JsonSerializer.Serialize(
                new
                {
                    sessions = new Dictionary<string, object>
                    {
                        ["idle"] = new { started_by = "harness", last_query = old },
                        ["busy"] = new
                        {
                            started_by = "harness",
                            last_query = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        },
                        ["gone"] = new { started_by = "harness", last_query = old },
                        ["unknown"] = new { started_by = "harness" },
                    },
                    starting = new { },
                    load_ms = new { },
                }
            ),
            Token
        );
        await using var manager = fixture.Manager();
        (await manager.SweepAsync(TimeSpan.FromHours(2), Token)).Should().Equal("idle");
        fixture.Calls().Where(call => call[0] is "stop").Should().ContainSingle().Which[1].Should().Be("idle");
        fixture.Records()["sessions"]!.AsObject().Select(pair => pair.Key).Should().Equal("busy", "unknown");
    }

    [Fact]
    public async Task OwnedLoadingWatchNeverClaimsOrStopsAReadySessionStartedByAnotherTool()
    {
        using var fixture = new GraphifyFixture();
        fixture.Write("never-ready", "");
        await using var manager = fixture.Manager();
        var provider = new GraphifyProvider(fixture.Options(), manager);
        try
        {
            provider.Warm();
            await ProviderFixture.EventuallyAsync(() => File.Exists(Path.Combine(fixture.Data, "sessions.json")));
            var sessions = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.Combine(fixture.Data, "sessions.json"), Token)
            )!;
            var owned = sessions["sessions"]![0]!;
            var ownedId = owned["session_id"]!.GetValue<string>();
            var pid = owned["process_id"]!.GetValue<int>();
            // Serialize this fixture edit with the fake server's independent writer.
            await using (
                var lease = await Harness.Infrastructure.Graphify.GraphifyRecords.LockAsync(
                    Path.Combine(fixture.Data, "fake.lock"),
                    TimeSpan.FromSeconds(5),
                    Token
                )
            )
            {
                sessions = JsonNode.Parse(
                    await File.ReadAllTextAsync(Path.Combine(fixture.Data, "sessions.json"), Token)
                )!;
                sessions["sessions"]!
                    .AsArray()
                    .Add(
                        new JsonObject
                        {
                            ["session_id"] = "borrowed-ready",
                            ["process_id"] = Environment.ProcessId,
                            ["ready"] = true,
                            ["reachability"] = "reachable",
                            ["repository_root"] = fixture.Root,
                        }
                    );
                await File.WriteAllTextAsync(
                    Path.Combine(fixture.Data, "sessions.json"),
                    sessions.ToJsonString(),
                    Token
                );
            }
            (await provider.ExecuteAsync(new(Capabilities.SymbolSearch, "Save"), Token)).Error.Should().BeNull();
            await provider.DisposeAsync();
            await ProviderFixture.GoneAsync(pid);
            fixture.Calls().Where(call => call[0] is "stop").Should().ContainSingle().Which[1].Should().Be(ownedId);
            fixture.Records()["sessions"]!["borrowed-ready"]!["started_by"].Should().BeNull();
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task LightRegistryExcludesGraphifyBeforeConstructionAndFullRegistryAllowsDetachedWatch()
    {
        using var fixture = new GraphifyFixture();
        var config = Path.Combine(fixture.DirectoryRoot, "config.toml");
        await File.WriteAllTextAsync(config, "[providers.graphify-csharp]\n", Token);
        var configuration = ConfigurationLoader.LoadFiles("", config).Value;
        var factory = new CountingFactory();
        await using var light = ConfiguredProviders.Build(configuration.Providers, fixture.Root, factory, light: true);
        await light.DiscoverAsync(Token);
        light.Providers().Should().BeEmpty();
        light.Capabilities().Should().BeEmpty();
        factory.Ids.Should().BeEmpty();
        var capture = new CapturingFactory();
        await using var full = ConfiguredProviders.Build(configuration.Providers, fixture.Root, capture);
        await full.DiscoverAsync(Token);
        capture.Options.Should().NotBeEmpty().And.OnlyContain(options => options.AllowDetachedChildren);
    }

    [Fact]
    public async Task ConcurrentCompanionsSerializeStartupAndPreserveSharedOwnershipRecords()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Companion fake apphost is Windows-built.");
        }

        using var fixture = new GraphifyFixture();
        await using var manager = fixture.Manager();
        var clients = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ => McpProcessClient.StartAsync(new ProviderProcessFactory(), HostOptions(fixture), Token))
        );
        try
        {
            var results = await Task.WhenAll(
                clients.Select(client =>
                    client.CallToolAsync(
                        "code_callers",
                        new Dictionary<string, object?> { ["query"] = "IService.Save" },
                        Token
                    )
                )
            );
            results.Should().OnlyContain(result => result.IsError != true);
            fixture.Calls().Count(call => call[0] is "watch").Should().Be(1);
            var session = (await manager.ListAsync(false, Token)).Single();
            session.StartedBy.Should().StartWith("harness (harness-graphify pid ");
            fixture.Records()["sessions"]![session.Id]!["last_query"].Should().NotBeNull();
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
        var warm = (await manager.ListAsync(false, Token)).Single();
        GraphifySessionManager.ProcessAlive(warm.Pid).Should().BeTrue(manager.LastLog());
        await manager.StopAsync(warm.Id, Token);
        await ProviderFixture.GoneAsync(warm.Pid);
    }

    [Fact]
    public async Task CompanionDisconnectLeavesWarmProcessAliveAndAnotherCompanionReusesIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Companion fake apphost is Windows-built; portable manager lifecycle is exercised separately on Unix."
            );
        }

        using var fixture = new GraphifyFixture();
        await using var manager = fixture.Manager();
        var options = HostOptions(fixture);
        await using (var client = await McpProcessClient.StartAsync(new ProviderProcessFactory(), options, Token))
        {
            var result = await client.CallToolAsync(
                "code_callers",
                new Dictionary<string, object?> { ["query"] = "IService.Save" },
                Token
            );
            result.IsError.Should().NotBeTrue();
            result.StructuredContent!.Value.GetProperty("items").GetArrayLength().Should().Be(1);
        }
        var warm = (await manager.ListAsync(false, Token)).Single();
        GraphifySessionManager
            .ProcessAlive(warm.Pid)
            .Should()
            .BeTrue("the warm watch must survive the adapter's kill-on-close job: " + manager.LastLog());
        await using (
            var client = await McpProcessClient.StartAsync(
                new ProviderProcessFactory(),
                HostOptions(fixture, stopOnExit: true),
                Token
            )
        )
        {
            foreach (var tool in new[] { "code_references", "code_impact" })
            {
                (
                    await client.CallToolAsync(
                        tool,
                        new Dictionary<string, object?> { ["query"] = "IService.Save" },
                        Token
                    )
                )
                    .IsError.Should()
                    .NotBeTrue();
            }
        }
        GraphifySessionManager.ProcessAlive(warm.Pid).Should().BeTrue("a reused session belongs to its original owner");
        fixture.Calls().Count(call => call[0] is "watch").Should().Be(1);
        await manager.StopAsync(warm.Id, Token);
        await ProviderFixture.GoneAsync(warm.Pid);
    }

    internal static McpProcessOptions HostOptions(GraphifyFixture fixture, bool stopOnExit = false, string? root = null)
    {
        var build = new DirectoryInfo(ProviderFixture.Worker).Parent!.Parent!.Name;
        var host = Path.Combine(
            ProviderFixture.RepositoryRoot,
            "src",
            "Harness.Graphify",
            "bin",
            build,
            "net10.0",
            "harness-graphify.dll"
        );
        var fake = Path.ChangeExtension(ProviderFixture.Worker, ".exe");
        return new(
            "graphify",
            new(
                "dotnet",
                [
                    host,
                    "--root",
                    root ?? fixture.Root,
                    "--graphify",
                    fake,
                    .. stopOnExit ? new[] { "--stop-on-exit" } : [],
                ]
            )
            {
                AllowDetachedChildren = true,
                Environment = new Dictionary<string, string?>
                {
                    ["HARNESS_FAKE_GRAPHIFY"] = fixture.Data,
                    ["HARNESS_CONFIG_DIR"] = Path.GetDirectoryName(fixture.RecordsPath),
                },
            }
        )
        {
            CallTimeout = TimeSpan.FromSeconds(10),
        };
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingFactory : IProviderProcessFactory
    {
        public List<ProviderProcessOptions> Options { get; } = [];

        public IProviderProcess Start(ProviderProcessOptions options)
        {
            Options.Add(options);
            throw new IOException("Only capturing process options for discovery.");
        }
    }
}
