using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Graphify;
using Harness.Core.Providers;
using Harness.Graphify;
using Harness.Infrastructure.Graphify;
using Harness.Providers.Graphify;
using Xunit;

namespace Harness.Integration.Providers;

[Collection("Provider processes")]
public sealed class GraphifyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal const string Symbols = """
        {"success":true,"items":[
         {"id":"iface","label":"N.IService.Save(System.Guid)","declaration_kind":"method","location":{"file":"src/IService.cs","line":4}},
         {"id":"parameter","label":"N.IService.Save:id","declaration_kind":"parameter"},
         {"id":"impl","label":"N.Service.Save(System.Guid)","declaration_kind":"method","location":{"file":"src/Service.cs","line":7}},
         {"id":"other","label":"N.Service.SaveCore()","declaration_kind":"method","location":{"file":"src/Service.cs","line":9}}],"page":{"has_more":false}}
        """;
    internal const string Callers = """
        {"success":true,"items":[{"relation":"calls","location":{"file":"src/Controller.cs","line":14},"origin":{"id":"controller","label":"N.Controller.Run()","declaration_kind":"method","location":{"file":"src/Controller.cs","line":12}}}]}
        """;

    [Fact]
    public void DiscoversRootNestedSolutionsDuplicateFormatsAndExplicitArguments()
    {
        using var fixture = new GraphifyFixture();
        GraphifyDiscovery.FindInput(fixture.Root).AtRoot.Should().BeTrue();
        File.Delete(Path.Combine(fixture.Root, "App.slnx"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "src", "App"));
        File.WriteAllText(Path.Combine(fixture.Root, "src", "App", "App.sln"), "");
        File.WriteAllText(Path.Combine(fixture.Root, "src", "App", "App.slnx"), "");
        GraphifyDiscovery.FindInput(fixture.Root).Path.Should().Be("src/App/App.slnx");
        var (arguments, description) = GraphifyDiscovery.WatchArguments(fixture.Root, ["--configuration", "Release"]);
        arguments
            .Should()
            .Equal("--configuration", "Release", "--input", Path.Combine(fixture.Root, "src", "App", "App.slnx"));
        description.Should().Be("solution src/App/App.slnx");
        File.WriteAllText(Path.Combine(fixture.Root, "src", "Other.sln"), "");
        GraphifyDiscovery.FindInput(fixture.Root).Candidates.Should().HaveCount(2);
        FluentActions
            .Invoking(() => GraphifyDiscovery.WatchArguments(fixture.Root, []))
            .Should()
            .Throw<InvalidDataException>()
            .WithMessage("*none chosen*args*--input*");
        GraphifyDiscovery
            .WatchArguments(fixture.Root, ["--input=chosen.sln"])
            .Arguments.Should()
            .Equal("--input=chosen.sln");
        var parsed = GraphifyApplication.Parse([
            "--root",
            fixture.Root,
            "--graphify=custom",
            "--idle",
            "5m",
            "--stop-on-exit",
            "--",
            "--input",
            "Chosen.sln",
        ]);
        parsed.Options.Command.Should().Be("custom");
        parsed.Options.Idle.Should().Be(TimeSpan.FromMinutes(5));
        parsed.Options.StopOnExit.Should().BeTrue();
        parsed.Options.WatchArguments.Should().Equal("--input", "Chosen.sln");
        GraphifyApplication
            .Parse(["--idle=1h30m", "--clean=1", "--stop-on-exit=F"])
            .Options.Idle.Should()
            .Be(TimeSpan.FromMinutes(90));
        GraphifyApplication.Parse(["--idle=-1.5s"]).Options.Idle.Should().Be(TimeSpan.FromMilliseconds(-1500));
        GraphifyApplication.Parse(["--idle=0"]).Options.Idle.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task ReusesExternalSessionFiltersSymbolsAndPreservesOtherToolsOwnership()
    {
        using var fixture = new GraphifyFixture();
        fixture.Ready("borrowed");
        await using var manager = fixture.Manager();
        await using (var provider = new GraphifyProvider(fixture.Options(), manager))
        {
            provider.Capabilities().Should().HaveCount(4);
            provider.Capabilities().Single(cap => cap.Capability == Capabilities.Callers).Evidence.Should().Be("exact");
            (await provider.HealthAsync(false, Token)).State.Should().Be("idle");
            var result = await provider.ExecuteAsync(new(Capabilities.SymbolSearch, "Save", Limit: 2), Token);
            result.Error.Should().BeNull();
            result.Items.Should().HaveCount(2);
            result.Truncated.Should().BeTrue();
            result
                .Items[0]
                .Should()
                .BeEquivalentTo(
                    new ProviderItem
                    {
                        Kind = "symbol",
                        Name = "Save",
                        QualifiedName = "N.IService.Save(System.Guid)",
                        Label = "method",
                        Path = "src/IService.cs",
                        StartLine = 4,
                        Provider = "graphify-csharp",
                        Tool = "query symbols",
                    }
                );
            result.Bytes.Should().Be(System.Text.Encoding.UTF8.GetByteCount(Symbols));
            (await provider.HealthAsync(true, Token)).Detail.Should().Be("warm session borrowed");
        }
        fixture.Calls().Where(call => call[0] is "watch" or "stop").Should().BeEmpty();
        var record = fixture.Records()["sessions"]!["borrowed"]!;
        record["last_query"].Should().NotBeNull();
        record["started_by"].Should().BeNull();
    }

    [Theory]
    [InlineData("code.callers", "symbol", "query callers")]
    [InlineData("code.references", "match", "query usages")]
    public async Task RelationsResolveExactNamesForwardLimitsAndApplyPathFilters(
        string capability,
        string kind,
        string tool
    )
    {
        using var fixture = new GraphifyFixture();
        fixture.Ready("external");
        await using var manager = fixture.Manager();
        await using var provider = new GraphifyProvider(fixture.Options(), manager);
        var result = await provider.ExecuteAsync(new(capability, "IService.Save", "src", 1), Token);
        result.Error.Should().BeNull();
        result.Items.Should().ContainSingle();
        result.Items[0].Kind.Should().Be(kind);
        result.Items[0].Tool.Should().Be(tool);
        result.Items[0].Snippet.Should().Be("-> Save");
        fixture
            .Calls()
            .Where(call => call[0] is "query" && call[1] is not "symbols")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("iface")
            .And.Contain("1");
        (await provider.ExecuteAsync(new(capability, "IService.Save", "elsewhere"), Token)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ImpactWalksBreadthFirstDeduplicatesCyclesAndUsesDefinitionLocations()
    {
        using var fixture = new GraphifyFixture();
        fixture.Ready("s1");
        fixture.Write(
            "callers-controller.json",
            """{"success":true,"items":[{"relation":"calls","location":{"file":"src/Api.cs","line":9},"origin":{"id":"api","label":"N.Api.Execute()","declaration_kind":"method","location":{"file":"src/Api.cs","line":5}}}]}"""
        );
        fixture.Write(
            "callers-api.json",
            """{"success":true,"items":[{"relation":"calls","location":{"file":"src/Controller.cs","line":22},"origin":{"id":"controller","label":"N.Controller.Run()"}}]}"""
        );
        await using var manager = fixture.Manager();
        await using var provider = new GraphifyProvider(fixture.Options(), manager);
        var result = await provider.ExecuteAsync(new(Capabilities.Impact, "IService.Save"), Token);
        result.Error.Should().BeNull();
        result.Items.Select(item => item.Name).Should().Equal("Run", "Execute");
        result.Items.Select(item => item.Depth).Should().Equal(1, 2);
        result.Items[1].StartLine.Should().Be(5);
        result.Truncated.Should().BeFalse();
        (await provider.ExecuteAsync(new(Capabilities.Impact, "IService.Save", Limit: 1), Token))
            .Truncated.Should()
            .BeTrue();
    }

    [Fact]
    public async Task ImpactOnInterfacesFollowsUsagesAndReportsIncompleteDiscovery()
    {
        using var fixture = new GraphifyFixture();
        fixture.Ready("s1");
        fixture.Write(
            "symbols.json",
            """{"success":true,"scope":{"input_discovery_complete":false},"diagnostics":[{"message":"provenance from '/a/Custom.targets' was not traced"}],"items":[{"id":"iface","label":"N.IService","declaration_kind":"interface"}]}"""
        );
        await using var manager = fixture.Manager();
        await using var provider = new GraphifyProvider(fixture.Options(), manager);
        var result = await provider.ExecuteAsync(new(Capabilities.Impact, "IService"), Token);
        result.Error.Should().BeNull();
        result.Items.Should().ContainSingle().Which.Tool.Should().Be("query usages");
        fixture.Records()["sessions"]!["s1"]!["discovery_incomplete"]!
            .GetValue<string>()
            .Should()
            .Be("Custom.targets adds items graphify cannot trace");
    }

    [Fact]
    public async Task AcknowledgedStopClearsOnlyTheMatchingPendingLaunchBeforeTheProcessExits()
    {
        using var fixture = new GraphifyFixture();
        fixture.Ready("stopping");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.RecordsPath)!);
        var root = GraphifyDiscovery.Canonical(fixture.Root);
        var other = Path.Combine(root, "other");
        await File.WriteAllTextAsync(
            fixture.RecordsPath,
            new JsonObject
            {
                ["sessions"] = new JsonObject(),
                ["starting"] = new JsonObject
                {
                    [root] = new JsonObject { ["pid"] = Environment.ProcessId, ["by"] = "fixture owner" },
                    [other] = new JsonObject { ["pid"] = -1, ["by"] = "other owner" },
                },
            }.ToJsonString(),
            Token
        );
        await using var manager = fixture.Manager();
        (await manager.ListAsync(false, Token)).Should().ContainSingle();
        await manager.StopAsync("stopping", Token);
        fixture.Records()["starting"]!.AsObject().ContainsKey(root).Should().BeFalse();
        fixture.Records()["starting"]!.AsObject().ContainsKey(other).Should().BeTrue();
        // The old PID is deliberately still alive: restart must not treat it as a launch in progress.
        var watch = await manager.StartAsync(Token);
        watch.Should().NotBeNull();
        try
        {
            await ProviderFixture.EventuallyAsync(() => fixture.Calls().Any(call => call[0] is "watch"));
        }
        finally
        {
            watch.Kill();
            await ProviderFixture.GoneAsync(watch.Id);
        }
    }

    [Fact]
    public async Task StartsOnlyOneWatchSharesItAndStopsOnlyItsOwnProcess()
    {
        using var fixture = new GraphifyFixture();
        await using var manager = fixture.Manager();
        var provider = new GraphifyProvider(fixture.Options(), manager);
        try
        {
            var result = await provider.ExecuteAsync(new(Capabilities.SymbolSearch, "Save"), Token);
            result.Error.Should().BeNull();
            var owned = (await manager.ListAsync(false, Token)).Should().ContainSingle().Which;
            owned.StartedBy.Should().Be("fixture owner");
            await using (var otherManager = fixture.Manager())
            await using (var other = new GraphifyProvider(fixture.Options(), otherManager))
            {
                (await other.ExecuteAsync(new(Capabilities.SymbolSearch, "Save"), Token)).Error.Should().BeNull();
            }

            GraphifySessionManager.ProcessAlive(owned.Pid).Should().BeTrue(manager.LastLog());
            fixture.Calls().Count(call => call[0] is "watch").Should().Be(1);
            await provider.DisposeAsync();
            await ProviderFixture.GoneAsync(owned.Pid);
            File.Exists(Path.Combine(fixture.Descriptors, owned.Id + ".json")).Should().BeFalse();
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task DefaultCloseKeepsOwnedSessionWarmForAnotherAdapter()
    {
        using var fixture = new GraphifyFixture();
        await using var manager = fixture.Manager();
        await using (var provider = new GraphifyProvider(fixture.Options() with { StopOnExit = false }, manager))
        {
            (await provider.ExecuteAsync(new(Capabilities.Callers, "IService.Save"), Token)).Error.Should().BeNull();
        }

        var session = (await manager.ListAsync(false, Token)).Single();
        GraphifySessionManager.ProcessAlive(session.Pid).Should().BeTrue(manager.LastLog());
        await using (var provider = new GraphifyProvider(fixture.Options(), manager))
        {
            (await provider.ExecuteAsync(new(Capabilities.References, "IService.Save"), Token)).Error.Should().BeNull();
        }

        GraphifySessionManager.ProcessAlive(session.Pid).Should().BeTrue(manager.LastLog());
        await manager.StopAsync(session.Id, Token);
        await ProviderFixture.GoneAsync(session.Pid);
    }

    [Fact]
    public async Task LoadingIsBoundedAndStopOnExitRemovesTheLoadingDescriptor()
    {
        using var fixture = new GraphifyFixture();
        fixture.Write("never-ready", "");
        await using var manager = fixture.Manager();
        var provider = new GraphifyProvider(fixture.Options() with { ReadyWait = TimeSpan.FromSeconds(1) }, manager);
        try
        {
            // Establish the loading session before measuring the query deadline;
            // cold process startup can itself exceed one second on a busy host.
            provider.Warm();
            var startup = Stopwatch.StartNew();
            while ((await manager.ListAsync(false, Token)).Count is 0 && startup.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(100, Token);
            }

            (await manager.ListAsync(false, Token)).Should().ContainSingle(manager.LastLog());
            var query = Stopwatch.StartNew();
            var result = await provider.ExecuteAsync(new(Capabilities.SymbolSearch, "Save"), Token);
            query.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            result.Error!.Message.Should().Contain("still loading").And.Contain("retry shortly");
            var loading = (await manager.ListAsync(false, Token)).Single();
            loading.Ready.Should().BeFalse();
            await provider.DisposeAsync();
            await ProviderFixture.GoneAsync(loading.Pid);
            File.Exists(Path.Combine(fixture.Descriptors, loading.Id + ".json")).Should().BeFalse();
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartupFailureAndVanishedSessionAreReportedAndReplaced()
    {
        using var fixture = new GraphifyFixture();
        fixture.Write("watch-fails", "");
        await using var manager = fixture.Manager();
        await using var provider = new GraphifyProvider(fixture.Options(), manager);
        (await provider.ExecuteAsync(new(Capabilities.SymbolSearch, "Save"), Token))
            .Error!.Message.Should()
            .Contain("watch exited")
            .And.Contain("no solution or project");
        File.Delete(Path.Combine(fixture.Data, "watch-fails"));
        fixture.Ready("old");
        (await provider.ExecuteAsync(new(Capabilities.SymbolSearch, "Save"), Token)).Error.Should().BeNull();
        fixture.Ready("replacement");
        (await provider.ExecuteAsync(new(Capabilities.Callers, "IService.Save"), Token)).Error.Should().BeNull();
        fixture.Calls().Should().Contain(call => call.Contains("replacement", StringComparer.Ordinal));
    }
}

internal sealed class GraphifyFixture : IDisposable
{
    public string DirectoryRoot { get; } =
        GraphifyDiscovery.Canonical(Directory.CreateTempSubdirectory("harness-graphify-test-").FullName);
    public string Root => Path.Combine(DirectoryRoot, "repository");
    public string Data => Path.Combine(DirectoryRoot, "fake");
    public string Descriptors => Path.Combine(Data, "descriptors");
    public string RecordsPath => Path.Combine(DirectoryRoot, "config", "graphify-sessions.json");

    public GraphifyFixture()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Descriptors);
        File.WriteAllText(Path.Combine(Root, "App.slnx"), "");
        Write("symbols.json", GraphifyTests.Symbols);
        Write("callers-iface.json", GraphifyTests.Callers);
        Write("usages-iface.json", GraphifyTests.Callers);
    }

    public GraphifyOptions Options() =>
        new(Root, "dotnet")
        {
            CommandArguments = [ProviderFixture.Worker],
            Environment = new Dictionary<string, string?> { ["HARNESS_FAKE_GRAPHIFY"] = Data },
            RecordsPath = RecordsPath,
            SessionDirectory = Descriptors,
            ReadyWait = TimeSpan.FromSeconds(5),
            StopOnExit = true,
            StartedBy = "fixture owner",
        };

    public GraphifySessionManager Manager(TimeProvider? clock = null) => new(Options(), clock: clock);

    public void Write(string name, string content) => File.WriteAllText(Path.Combine(Data, name), content);

    public void Ready(string id) =>
        Write(
            "sessions.json",
            JsonSerializer.Serialize(
                new
                {
                    success = true,
                    sessions = new[]
                    {
                        new
                        {
                            session_id = id,
                            process_id = Environment.ProcessId,
                            reachability = "reachable",
                            lifecycle_state = "ready",
                            ready = true,
                            repository_root = Root,
                        },
                    },
                }
            )
        );

    public string[][] Calls()
    {
        var path = Path.Combine(Data, "calls.log");
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        // The worker appends newline-delimited records; ignore a record still being written.
        return reader
            .ReadToEnd()
            .Split('\n')
            .SkipLast(1)
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<string[]>(line)!)
            .ToArray();
    }

    public JsonObject Records() => JsonNode.Parse(File.ReadAllText(RecordsPath))!.AsObject();

    public void Dispose()
    {
        var file = Path.Combine(Data, "sessions.json");
        if (File.Exists(file))
        {
            foreach (var session in JsonNode.Parse(File.ReadAllText(file))!["sessions"]!.AsArray())
            {
                if (
                    session?["session_id"]?.GetValue<string>().StartsWith("watch-", StringComparison.Ordinal) is true
                    && session["process_id"]?.GetValue<int>() is { } pid
                )
                {
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(5000);
                        }
                    }
                    catch (ArgumentException)
                    { /* Already reaped. */
                    }
                }
            }
        }

        Path.GetFullPath(DirectoryRoot)
            .Should()
            .StartWith(
                Path.TrimEndingDirectorySeparator(GraphifyDiscovery.Canonical(Path.GetTempPath()))
                    + Path.DirectorySeparatorChar
            );
        TestDirectory.Delete(DirectoryRoot);
    }
}
