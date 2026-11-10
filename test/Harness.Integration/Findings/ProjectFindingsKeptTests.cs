using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Core.Sync;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Sync;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Findings;

[Collection("Provider processes")]
public sealed class ProjectFindingsKeptTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Cycle(string member) =>
        Harness.Core.Architecture.ArchitectureAnalyzer.Key(
            "cycle",
            [.. new[] { member, "B" }.Order(StringComparer.Ordinal)]
        );

    private static string Router(int branches) =>
        "namespace App;\npublic class Router\n{\n    public int Route(int a)\n    {\n"
        + string.Concat(Enumerable.Repeat("        if (a > 0) { a--; }\n", branches))
        + "        return a;\n    }\n}\n";

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Register transfers provider ownership to the await-using registry."
    )]
    [Fact]
    public async Task StoredProjectFindingsAreKeptUntilAnythingTheyDependOnChanges()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "App.csproj",
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        fixture.Write("Router.cs", Router(13));
        await fixture.CommitAsync("app");
        var answer = "A";
        await using var providers = new ProviderRegistry();
        providers.Register(
            new ProviderRegistryTests.Fake(
                "fixture",
                [Capabilities.ArchCycles],
                execute: (_, _) =>
                    Task.FromResult(
                        new ProviderResult
                        {
                            Items =
                            [
                                new()
                                {
                                    Provider = "fixture",
                                    Tool = "cycles",
                                    Members = [answer, "B"],
                                },
                            ],
                        }
                    )
            )
        );
        await using var index = new SyncTests.Index();
        var sync = new ProjectSynchronizer(fixture.Lifecycle, providers: providers);

        async Task<FindingSync> SyncAsync(string mode = "")
        {
            var result = await sync.RunAsync(fixture.Root, new() { Mode = mode, Refreshers = [index] }, Token);
            return result.Findings.Single(finding => finding.Where == "project");
        }

        string[] Stored()
        {
            using var db = fixture.Database();
            return new FindingStore(db)
                .Current(Token)
                .Findings.Where(finding => finding.Status is not "gone")
                .Select(finding => string.Join('|', finding.Key, finding.Title, finding.Level, finding.Measure))
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        // Computed after a change, equal to what a full sync computes, and kept by the next sync.
        async Task RecomputedAsync(string because)
        {
            (await SyncAsync()).Kept.Should().BeNull(because);
            var computed = Stored();
            (await SyncAsync("full")).Kept.Should().BeNull("harness sync --full always computes them");
            Stored().Should().Equal(computed, "the kept-or-computed findings are a full computation's");
            (await SyncAsync()).Kept.Should().Be(computed.Length, "nothing changed since");
        }

        await RecomputedAsync("the first sync computes them");
        Stored()
            .Should()
            .Contain(finding => finding.StartsWith("complexity:Router.cs#Router.Route|", StringComparison.Ordinal));
        Stored().Should().Contain(finding => finding.StartsWith(Cycle("A") + "|", StringComparison.Ordinal));

        fixture.Write("Router.cs", Router(25));
        await RecomputedAsync("a file was edited");
        await fixture.CommitAsync("more routes");
        await RecomputedAsync("a commit");
        await fixture.GitAsync("checkout", "-q", "-b", "elsewhere");
        await RecomputedAsync("a branch switch");
        fixture.Write(".harness/config.toml", "[architecture.complexity]\nnote = 5\nwarn = 10\nproblem = 20\n");
        await RecomputedAsync("a threshold changed");
        Stored()
            .Should()
            .Contain(finding =>
                finding.StartsWith("complexity:Router.cs#Router.Route|", StringComparison.Ordinal)
                && finding.Contains("|problem|", StringComparison.Ordinal)
            );
        using (var db = fixture.Database())
        {
            new MemoryStore(db).Put(
                new()
                {
                    Type = "decision",
                    Content = "Keep `MissingPolicy` as the authority",
                    Force = true,
                },
                Token
            );
        }

        await RecomputedAsync("a decision was recorded");
        Stored().Should().Contain(finding => finding.StartsWith("stale-knowledge", StringComparison.Ordinal));

        // A provider answers differently only once its index changed: a sync that refreshes it computes them.
        answer = "C";
        (await SyncAsync()).Kept.Should().NotBeNull("the provider's index did not change");
        index.Failure = new IOException("index busy");
        fixture.Write("Router.cs", Router(26));
        (await SyncAsync()).Kept.Should().BeNull();
        index.Failure = null;
        (await SyncAsync()).Kept.Should().BeNull("the stale provider refreshed its index");
        Stored().Should().Contain(finding => finding.StartsWith(Cycle("C") + "|", StringComparison.Ordinal));
        Stored().Should().NotContain(finding => finding.StartsWith(Cycle("A") + "|", StringComparison.Ordinal));
        (await SyncAsync()).Kept.Should().NotBeNull();

        // Another harness build computes them.
        var project = (await sync.RunAsync(fixture.Root, token: Token)).Registration.Project.Id;
        using (var db = fixture.Database())
        {
            async Task<FindingSync> BuildAsync(string build) =>
                (
                    await new FindingSets(
                        fixture.Root,
                        project,
                        db,
                        fixture.Lifecycle.GlobalConfigurationPath,
                        fixture.Lifecycle.Registry
                    )
                    {
                        Build = build,
                    }.SyncAsync("", null, null, true, Token)
                ).Single(finding => finding.Where == "project");
            (await BuildAsync("upgraded")).Kept.Should().BeNull("a new harness build computes them");
            (await BuildAsync("upgraded")).Kept.Should().NotBeNull();
        }
    }
}
