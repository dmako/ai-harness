using AwesomeAssertions;
using Harness.Core.Findings;
using Harness.Infrastructure.Architecture.Complexity;
using Harness.Infrastructure.Architecture.Metrics;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Sync;
using Xunit;

namespace Harness.Integration.Sync;

public sealed class MetricsWorkerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SyncSchedulesTheWorkerAndExactValuesHoldOnlyForTheirCommit()
    {
        using var fixture = await MetricsFixture.CreateAsync();
        var launcher = new RecordingLauncher();
        var tool = new FakeTool { ["App/Router.cs#Route"] = 40, ["Lib/Clock.cs#Tick"] = 3 };
        var first = await fixture.SyncAsync(launcher);
        var head = first.State.Commit;

        // Scheduled for HEAD on Windows only; Microsoft.CodeAnalysis.Metrics does not run elsewhere.
        if (OperatingSystem.IsWindows())
        {
            launcher.Started.Should().Equal(head);
            first.Metrics.Should().StartWith("started for " + head[..7]);
        }
        else
        {
            launcher.Started.Should().BeEmpty();
            first.Metrics.Should().StartWith("unsupported");
        }

        (await fixture.RouteAsync()).Source.Tool.Should().Be("internal/complexity");

        var run = await new MetricsWorker(fixture.Inner.Lifecycle, tool).RunAsync(fixture.Inner.Root, head, Token);
        run.State.Should().Be("done");
        tool.Calls.Should()
            .ContainSingle()
            .Which.Should()
            .Equal(["Lib/Lib.csproj", "App/App.csproj"], "a project is measured after its references");
        run.Members.Should().Contain(member => member.Name == "Router.Route" && member.Maintainability == 30);
        var measured = await fixture.RouteAsync();
        measured.Measure.Should().Be(40);
        measured.Level.Should().Be("warn");
        measured.Source.Tool.Should().Be(ComplexityReader.Measured);
        measured.Why.Should().Contain("maintainability index 30");
        measured.Status.Should().Be("changed", "the worker refreshed the snapshot itself");

        // A sync for the same commit leaves the run alone.
        var again = await fixture.SyncAsync(launcher);
        launcher.Started.Should().HaveCount(OperatingSystem.IsWindows() ? 1 : 0);
        if (OperatingSystem.IsWindows())
        {
            again.Metrics.Should().Be("ready for " + head[..7]);
        }

        // HEAD moves: its lexical count stands until the run for the new commit is done.
        fixture.Inner.Write("App/Router.cs", MetricsFixture.Router(15));
        await fixture.Inner.CommitAsync("one more branch");
        var moved = await fixture.SyncAsync(launcher);
        var lexical = await fixture.RouteAsync();
        lexical.Source.Tool.Should().Be("internal/complexity");
        lexical.Measure.Should().Be(15);
        fixture
            .Store.Status(moved.State.Commit)
            .State.Should()
            .Be(OperatingSystem.IsWindows() ? "stale" : "unsupported");

        // A measured commit is not measured again.
        (await new MetricsWorker(fixture.Inner.Lifecycle, tool).RunAsync(fixture.Inner.Root, head, Token))
            .State.Should()
            .Be("done");
        tool.Calls.Should().HaveCount(1);

        // HEAD moves on: a run for the commit in between is dropped and leaves nothing behind.
        fixture.Inner.Write("App/Notes.cs", "namespace App;\npublic class Notes\n{\n}\n");
        await fixture.Inner.CommitAsync("notes");
        var latest = await fixture.SyncAsync(launcher);
        (await new MetricsWorker(fixture.Inner.Lifecycle, tool).RunAsync(fixture.Inner.Root, moved.State.Commit, Token))
            .State.Should()
            .Be("superseded");
        fixture.Store.Load(moved.State.Commit).Should().BeNull();

        // The run for HEAD measures only the project that changed and keeps the other's members.
        tool["App/Router.cs#Route"] = 41;
        var next = await new MetricsWorker(fixture.Inner.Lifecycle, tool).RunAsync(
            fixture.Inner.Root,
            latest.State.Commit,
            Token
        );
        next.State.Should().Be("done");
        tool.Calls[^1].Should().Equal("App/App.csproj");
        next.Reused.Should().Equal("Lib/Lib.csproj");
        next.Base.Should().Be(head);
        next.Members.Should().Contain(member => member.Name == "Clock.Tick", "kept from the earlier run");
        (await fixture.RouteAsync()).Measure.Should().Be(41);
        if (OperatingSystem.IsWindows())
        {
            launcher.Started.Should().Equal(head, moved.State.Commit, latest.State.Commit);
            fixture.Store.Status(latest.State.Commit).State.Should().Be("ready");
        }
    }

    [Fact]
    public async Task FailedRunKeepsLexicalFindingsAndSaysWhatToFix()
    {
        using var fixture = await MetricsFixture.CreateAsync();
        var head = (await fixture.SyncAsync(new RecordingLauncher())).State.Commit;
        var tool = new FakeTool { Failure = new MetricsToolException("restore", "could not restore (offline)") };

        var run = await new MetricsWorker(fixture.Inner.Lifecycle, tool).RunAsync(fixture.Inner.Root, head, Token);

        run.State.Should().Be("failed");
        run.Failure.Should().Be("restore");
        (await fixture.RouteAsync()).Source.Tool.Should().Be("internal/complexity");
        if (OperatingSystem.IsWindows())
        {
            var status = fixture.Store.Status(head);
            status.State.Should().Be("failed");
            status.Detail.Should().Contain("offline");
            status.Fix.Should().Contain("NuGet");
            var check = (await fixture.Inner.Lifecycle.InspectAsync(fixture.Inner.Root, Token))
                .Checks("test")
                .Should()
                .ContainSingle(check => check.Name == "metrics")
                .Which;
            check.Level.Should().Be("warn");
        }
    }

    [Fact]
    public async Task DisabledWorkerIsNeitherScheduledNorUsed()
    {
        using var fixture = await MetricsFixture.CreateAsync(enabled: false);
        var launcher = new RecordingLauncher();
        var result = await fixture.SyncAsync(launcher);
        result.Metrics.Should().BeEmpty();
        launcher.Started.Should().BeEmpty();
        (await fixture.Inner.Lifecycle.InspectAsync(fixture.Inner.Root, Token))
            .Checks("test")
            .Should()
            .NotContain(check => check.Name == "metrics");
    }

    [Fact]
    public async Task RealMetricsToolMeasuresTheFixture()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("HARNESS_REAL_METRICS") is "1" && OperatingSystem.IsWindows(),
            "Set HARNESS_REAL_METRICS=1 on Windows to restore Microsoft.CodeAnalysis.Metrics from NuGet and run it."
        );
        using var fixture = await MetricsFixture.CreateAsync();
        var head = (await fixture.SyncAsync(new RecordingLauncher())).State.Commit;
        var tool = new MetricsExecutable(Path.Combine(fixture.Inner.DirectoryRoot, "tools"));

        var run = await new MetricsWorker(fixture.Inner.Lifecycle, tool).RunAsync(fixture.Inner.Root, head, Token);

        run.State.Should().Be("done", run.Error);
        run.Members.Should().Contain(member => member.Name == "Router.Route" && member.Complexity == 14);
    }

    private sealed class RecordingLauncher : IMetricsLauncher
    {
        public List<string> Started { get; } = [];

        // Process id 0 never runs, so no test process is taken for a worker.
        public int Start(string root, string commit, string log)
        {
            Started.Add(commit);
            return 0;
        }
    }

    /// <summary>Writes a report in Microsoft.CodeAnalysis.Metrics' format for the members it is told about.</summary>
    private sealed class FakeTool : Dictionary<string, int>, IMetricsTool
    {
        public List<string[]> Calls { get; } = [];
        public MetricsToolException? Failure { get; init; }
        public string Version => "fake 1";

        public Task<string> MeasureAsync(
            string root,
            IReadOnlyList<string> projects,
            string report,
            CancellationToken token
        )
        {
            Calls.Add(projects.Select(project => Path.GetRelativePath(root, project).Replace('\\', '/')).ToArray());
            if (Failure is not null)
            {
                throw Failure;
            }

            var members = this.Where(pair =>
                    projects.Any(project =>
                        pair.Key.StartsWith(
                            Path.GetFileName(Path.GetDirectoryName(project)) + "/",
                            StringComparison.Ordinal
                        )
                    )
                )
                .Select(pair =>
                {
                    var (file, name) = (pair.Key.Split('#')[0], pair.Key.Split('#')[1]);
                    var path = Path.Combine(root, file);
                    var line =
                        File.ReadAllLines(path)
                            .ToList()
                            .FindIndex(text => text.Contains($" {name}(", StringComparison.Ordinal)) + 1;
                    var type = Path.GetFileNameWithoutExtension(file);
                    return $"""
                        <Method Name="int {type}.{name}(int a)" File="{path}" Line="{line}">
                          <Metrics>
                            <Metric Name="MaintainabilityIndex" Value="30" />
                            <Metric Name="CyclomaticComplexity" Value="{pair.Value}" />
                          </Metrics>
                        </Method>
                        """;
                });
            File.WriteAllText(
                report,
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <CodeMetricsReport Version="1.0">
                  <Targets><Target Name="App.csproj"><Assembly Name="App"><Namespaces><Namespace Name="App"><Types>
                    <NamedType Name="Any"><Members>{string.Concat(members)}</Members></NamedType>
                  </Types></Namespace></Namespaces></Assembly></Target></Targets>
                </CodeMetricsReport>
                """
            );
            return Task.FromResult(report);
        }
    }

    private sealed class MetricsFixture(SyncFixture inner, string projectId) : IDisposable
    {
        public SyncFixture Inner => inner;
        public MetricsStore Store => MetricsStore.For(inner.Lifecycle.GlobalConfigurationPath, projectId);

        public static async Task<MetricsFixture> CreateAsync(bool enabled = true)
        {
            var inner = await SyncFixture.CreateAsync();
            try
            {
                inner.Write(
                    "Lib/Lib.csproj",
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
                );
                inner.Write(
                    "Lib/Clock.cs",
                    "namespace Lib;\npublic class Clock\n{\n    public int Tick(int a) => a + 1;\n}\n"
                );
                inner.Write(
                    "App/App.csproj",
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                        + "<ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup></Project>"
                );
                inner.Write("App/Router.cs", Router(14));
                inner.Write(
                    ".harness/config.toml",
                    $"[architecture.complexity.metrics]\nenabled = {(enabled ? "true" : "false")}\n"
                );
                await inner.CommitAsync("projects");
                using var database = inner.Database();
                var id = Harness.Infrastructure.Projects.ProjectStore.Load(database)!.Id;
                return new(inner, id);
            }
            catch
            {
                inner.Dispose();
                throw;
            }
        }

        public static string Router(int complexity) =>
            "namespace App;\npublic class Router\n{\n    public int Route(int a)\n    {\n"
            + string.Concat(Enumerable.Repeat("        if (a > 0) { a--; }\n", complexity - 1))
            + "        return a;\n    }\n}\n";

        public Task<ProjectSyncResult> SyncAsync(IMetricsLauncher launcher) =>
            new ProjectSynchronizer(inner.Lifecycle, metricsLauncher: launcher).RunAsync(inner.Root, token: Token);

        public async Task<Finding> RouteAsync()
        {
            using var database = inner.Database();
            var sets = new FindingSets(
                inner.Root,
                projectId,
                database,
                inner.Lifecycle.GlobalConfigurationPath,
                inner.Lifecycle.Registry
            );
            return (await sets.ReadAsync("project", Token))
                .Groups.Single()
                .Findings.Should()
                .ContainSingle(finding => finding.Key == "complexity:App/Router.cs#Router.Route")
                .Which;
        }

        public void Dispose() => inner.Dispose();
    }
}
