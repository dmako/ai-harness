using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Workspaces;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Harness.Mcp.Context;
using Harness.Mcp.Providers;
using Harness.Mcp.Workspaces;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Workspaces;

[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Registered fixture providers transfer ownership to a disposed registry."
)]
public sealed class WorkspaceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManagementIsAtomicAndPreservesMemberDatabasesAndWorkspaceKnowledgeIdentity()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        var hashes = fixture.Hashes();
        var workspace = await fixture.Store.CreateAsync("  EXAMPLE  ", Token);
        workspace.Name.Should().Be("example");
        await Task.WhenAll(
            fixture.Members.Select(member => fixture.Store.JoinAsync(workspace.Name, [member.Id], Token))
        );
        fixture.Store.Find("example").Members.Should().BeEquivalentTo(fixture.Members.Select(member => member.Id));
        (await fixture.Store.JoinAsync("example", [fixture.Members[0].Id], Token)).Should().BeEmpty();
        var renamed = await fixture.Store.RenameAsync("example", " Renamed ", "  Friendly\n workspace 🧪  ", Token);
        renamed.Id.Should().Be(workspace.Id);
        renamed.Title.Should().Be("Friendly workspace 🧪");
        fixture.Store.Discover(Path.GetTempPath()).Should().HaveCount(3);
        (await fixture.Store.LeaveAsync("renamed", fixture.Members[1].Id, Token)).Should().BeTrue();
        (await fixture.Store.LeaveAsync("renamed", fixture.Members[1].Id, Token)).Should().BeFalse();
        await fixture.Lifecycle.Registry.RenameAsync(fixture.Members[0].Id, "Alpha", "display", Token);
        WorkspaceStore
            .ShortName(fixture.Lifecycle.Registry.Load()["projects"]!.AsArray()[0]!.AsObject())
            .Should()
            .Be("Alpha");
        await fixture.Store.DeleteAsync("renamed", Token);
        (await fixture.Lifecycle.Registry.ForgetAsync(fixture.Members[2].Id, Token)).Should().BeTrue();
        fixture.Hashes().Should().Equal(hashes);
        Directory.GetFiles(fixture.Global, "*.tmp").Should().BeEmpty();
        fixture.Store.Load().Should().BeEmpty();
    }

    [Theory]
    [InlineData("../bad")]
    [InlineData("bad name")]
    [InlineData("-bad")]
    public async Task InvalidNamesAndMalformedMachineFilesDoNotWrite(string name)
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await FluentActions
            .Awaiting(() => fixture.Store.CreateAsync(name, Token))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        File.Exists(Path.Combine(fixture.Global, "workspaces.json")).Should().BeFalse();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Global, "workspaces.json"),
            """{"version":1,"workspaces":"invalid"}""",
            Token
        );
        var before = await File.ReadAllBytesAsync(Path.Combine(fixture.Global, "workspaces.json"), Token);
        await FluentActions
            .Awaiting(() => fixture.Store.CreateAsync("valid", Token))
            .Should()
            .ThrowAsync<InvalidOperationException>();
        (await File.ReadAllBytesAsync(Path.Combine(fixture.Global, "workspaces.json"), Token)).Should().Equal(before);
    }

    [Fact]
    public async Task WorkspaceSearchKeepsCurrentFirstCachesModesAndNeverStartsExcludedMetadataInLightMode()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        var membership = new WorkspaceMembership(fixture.Members[1], fixture.Members);
        var built = new List<(string, bool)>();
        var heavyCalls = 0;
        await using var current = new ProviderRegistry();
        current.Register(
            new ProviderRegistryTests.Fake(
                "live",
                [Capabilities.SymbolSearch],
                execute: (_, _) =>
                    Task.FromResult(
                        new ProviderResult
                        {
                            Items =
                            [
                                new()
                                {
                                    Name = "Widget",
                                    Path = "a.cs",
                                    Provider = "live",
                                },
                            ],
                        }
                    )
            )
        );
        var heavy = new ProviderRegistryTests.Fake(
            "heavy",
            [Capabilities.SymbolSearch],
            execute: (_, _) =>
            {
                heavyCalls++;
                return Task.FromResult(new ProviderResult());
            }
        );
        current.Register(heavy);
        current.ExcludeFromContext("heavy");
        await using var search = new WorkspaceSearchSession(
            membership,
            current,
            (member, deep) =>
            {
                lock (built)
                {
                    built.Add((member.Id, deep));
                }

                var registry = new ProviderRegistry();
                registry.Register(
                    new ProviderRegistryTests.Fake(
                        "live",
                        [Capabilities.SymbolSearch],
                        execute: (_, _) =>
                            Task.FromResult(
                                new ProviderResult
                                {
                                    Items =
                                    [
                                        new()
                                        {
                                            Name = "Widget",
                                            Path = "a.cs",
                                            Provider = "live",
                                        },
                                    ],
                                }
                            )
                    )
                );
                return registry;
            }
        );
        var first = await search.QueryAsync(new(Capabilities.SymbolSearch, "Widget"), false, "", Token);
        first.Hits.Select(hit => hit.Member.Name).Should().Equal("beta", "alpha", "gamma");
        await search.QueryAsync(new(Capabilities.SymbolSearch, "Widget"), false, "", Token);
        built.Should().HaveCount(2);
        heavyCalls.Should().Be(0);
        heavy.Discoveries.Should().Be(0);
        var only = await search.QueryAsync(new(Capabilities.SymbolSearch, "Widget"), true, "alpha", Token);
        only.Hits.Should().ContainSingle().Which.Member.Name.Should().Be("alpha");
        built.Should().HaveCount(3);
        await search.QueryAsync(new(Capabilities.SymbolSearch, "Widget"), true, "beta", Token);
        heavyCalls.Should().Be(1);
    }

    [Fact]
    public async Task WorkspaceContextAndLayeredKnowledgeDoNotLeakAnotherWorkspace()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.Store.CreateAsync("one", Token);
        await fixture.Store.JoinAsync("one", fixture.Members.Take(2).Select(member => member.Id).ToArray(), Token);
        await fixture.Store.CreateAsync("two", Token);
        await fixture.Store.JoinAsync("two", [fixture.Members[2].Id], Token);
        await using var session = await fixture.SessionAsync(0);
        await session.PutMemoryAsync(
            new()
            {
                Type = "decision",
                Content = "Widget shared workspace rule",
                Scope = new("workspace", "one"),
            },
            Token
        );
        await session.PutMemoryAsync(
            new()
            {
                Type = "warning",
                Content = "Widget user rule",
                Scope = new("user", ""),
            },
            Token
        );
        await using (var other = await fixture.SessionAsync(2))
        {
            await other.PutMemoryAsync(
                new()
                {
                    Type = "decision",
                    Content = "Widget unrelated workspace secret",
                    Scope = new("workspace", "two"),
                },
                Token
            );
        }

        await using (var beta = await fixture.SessionAsync(1))
        {
            await beta.PutMemoryAsync(
                new()
                {
                    Type = "decision",
                    Content = "Widget path rule",
                    Scope = new("path", "a.cs"),
                },
                Token
            );
        }

        var created = 0;
        var disposed = 0;
        await using var current = new ProviderRegistry();
        await using var tools = new WorkspaceTools(
            session,
            membership =>
            {
                created++;
                return new WorkspaceSearchSession(
                    membership,
                    current,
                    (member, _) =>
                    {
                        var registry = new ProviderRegistry();
                        registry.Register(new WidgetProvider(() => disposed++));
                        return registry;
                    }
                );
            },
            current,
            session
        );
        var providerTools = new ProviderTools(current, session, tools);
        var result = await providerTools.CallAsync(
            "search_project",
            Args("""{"query":"Widget","kind":"symbol","scope":"workspace","path":"beta:a.cs#Widget"}"""),
            Token
        );
        var search = JsonSerializer.SerializeToElement(result);
        search.GetProperty("results").GetArrayLength().Should().Be(1);
        search.GetProperty("results")[0].GetProperty("project").GetString().Should().Be("beta");
        var answer = await new ContextTools(current, session, tools).CallAsync(
            Args("""{"task":"Adjust Widget path rule","scope":"workspace","budget":8000}"""),
            Token
        );
        using var context = JsonDocument.Parse(answer.Json);
        context
            .RootElement.GetProperty("used_bytes")
            .GetInt32()
            .Should()
            .Be(System.Text.Encoding.UTF8.GetByteCount(answer.Json));
        context.RootElement.GetProperty("workspace")[0].GetProperty("project").GetString().Should().Be("beta");
        answer
            .Json.Should()
            .Contain("Widget path rule")
            .And.Contain("Widget shared workspace rule")
            .And.Contain("Widget user rule")
            .And.NotContain("unrelated workspace secret");
        created.Should().Be(1);
        await fixture.Store.LeaveAsync("one", fixture.Members[1].Id, Token);
        await fixture.Store.JoinAsync("one", [fixture.Members[2].Id], Token);
        var updated = await providerTools.CallAsync(
            "search_project",
            Args("""{"query":"Widget","scope":"workspace","kind":"symbol"}"""),
            Token
        );
        JsonSerializer.Serialize(updated).Should().Contain("gamma").And.NotContain("beta");
        created.Should().Be(2);
        disposed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ImpactOrdersCompilerReferencesPackageNamespaceAndNamesAndDeduplicatesLocations()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        fixture.Repositories[0].Write("a.cs", "namespace Company.Library;\nclass Widget {}\n");
        fixture
            .Repositories[0]
            .Write(
                "Library.csproj",
                "<Project><PropertyGroup><PackageId>Company.Library</PackageId></PropertyGroup></Project>"
            );
        await using var current = new ProviderRegistry();
        current.Register(
            new EvidenceProvider(
                "compiler",
                [Capabilities.SymbolSearch, Capabilities.Impact],
                execute: (request, _) =>
                    Task.FromResult(
                        new ProviderResult
                        {
                            Items =
                            [
                                new()
                                {
                                    Name = "Widget",
                                    Path = "a.cs",
                                    StartLine = 1,
                                    Provider = "compiler",
                                    QualifiedName = "Company.Library.Widget",
                                },
                            ],
                        }
                    )
            )
        );
        await using var search = new WorkspaceSearchSession(
            new(fixture.Members[0], fixture.Members.Take(2).ToArray()),
            current,
            (_, _) =>
            {
                var registry = new ProviderRegistry();
                registry.Register(
                    new EvidenceProvider(
                        "remote",
                        [Capabilities.References, Capabilities.TextSearch],
                        execute: (request, _) =>
                            Task.FromResult(
                                new ProviderResult
                                {
                                    Items = request switch
                                    {
                                        { Capability: Capabilities.References } =>
                                        [
                                            new()
                                            {
                                                Name = "Use",
                                                Path = "use.cs",
                                                StartLine = 1,
                                                Provider = "remote",
                                            },
                                        ],
                                        { Query: var query }
                                            when query.StartsWith("Include=", StringComparison.Ordinal) =>
                                        [
                                            new()
                                            {
                                                Path = "Consumer.csproj",
                                                StartLine = 2,
                                                Provider = "remote",
                                            },
                                        ],
                                        _ =>
                                        [
                                            new()
                                            {
                                                Name = "Use",
                                                Path = "use.cs",
                                                StartLine = 1,
                                                Provider = "remote",
                                            },
                                            new()
                                            {
                                                Path = "other.cs",
                                                StartLine = 3,
                                                Provider = "remote",
                                            },
                                        ],
                                    },
                                }
                            )
                    )
                );
                return registry;
            }
        );
        var answer = await search.ImpactAsync("Widget", 10, false, Token);
        answer
            .Home.Should()
            .Be(new SymbolHome("a.cs", "Company.Library", "Company.Library", "Include=\"Company.Library\""));
        answer.Hits.Select(hit => hit.Via).Should().Equal("impact", "references", "package", "namespace");
        answer.Hits.Take(3).Should().OnlyContain(hit => hit.Exact);
        answer.Hits[^1].Exact.Should().BeFalse();
    }

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public async Task OverviewReadsRealProposalCountsAndAveragesScoredAnswersWithoutWriting()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await using (var session = await fixture.SessionAsync(0))
        {
            await session.ProposeMemoryAsync(new("warning", "Review workspace operations"), Token);
        }

        using (var database = HarnessDatabase.Open(fixture.Members[0].DatabasePath, cancellationToken: Token))
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO context_calls(id,at,worktree_id,head,task,budget,used)
                VALUES(1,strftime('%Y-%m-%dT%H:%M:%SZ','now'),'fixture','','fixture',1000,100),
                      (2,strftime('%Y-%m-%dT%H:%M:%SZ','now'),'fixture','','fixture',1000,100);
                INSERT INTO context_outcomes(call_id,path,language,hit)
                VALUES(1,'a.cs','csharp',1),(1,'b.cs','csharp',0),(1,'WidgetTests.cs','csharp',0),
                      (2,'c.cs','csharp',1);
                """;
            await command.ExecuteNonQueryAsync(Token);
        }
        var hashes = fixture.Hashes();
        var row = fixture.Lifecycle.Registry.Load()["projects"]!.AsArray()[0]!.AsObject();
        var live = await ProjectOverview.InspectAsync(fixture.Lifecycle, row, Token);
        live["pending_proposals"]!.GetValue<int>().Should().Be(1);
        live["context_answers_30d"]!.GetValue<int>().Should().Be(2);
        live["context_hit_rate_30d"]!.GetValue<double>().Should().Be(0.75);
        fixture.Hashes().Should().Equal(hashes);
    }

    [Fact]
    public async Task UnresolvedPackageIdentityRetainsNamespaceAndCompilerEvidence()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        fixture.Repositories[0].Write("a.cs", "namespace Company.Library; class Widget {}");
        fixture
            .Repositories[0]
            .Write(
                "Library.csproj",
                "<Project><PropertyGroup><PackageId>$(Unavailable).Library</PackageId></PropertyGroup></Project>"
            );
        await using var current = new ProviderRegistry();
        current.Register(
            new EvidenceProvider(
                "compiler",
                [Capabilities.SymbolSearch, Capabilities.Impact],
                (_, _) =>
                    Task.FromResult(
                        new ProviderResult
                        {
                            Items =
                            [
                                new()
                                {
                                    Name = "Widget",
                                    Path = "a.cs",
                                    Provider = "compiler",
                                },
                            ],
                        }
                    )
            )
        );
        await using var search = new WorkspaceSearchSession(
            new(fixture.Members[0], fixture.Members.Take(2).ToArray()),
            current,
            (_, _) => new ProviderRegistry()
        );
        var answer = await search.ImpactAsync("Widget", 10, false, Token);
        answer.Home.Package.Should().BeEmpty();
        answer.Home.Namespace.Should().Be("Company.Library");
        answer.Hits.Should().ContainSingle().Which.Exact.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAndSlowMembersReturnPartialEvidenceWithinTheCap(bool inaccessible)
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await using var current = new ProviderRegistry();
        current.Register(new WidgetProvider(() => { }));
        var members = fixture.Members.ToArray();
        members[2] = members[2] with { Root = Path.Combine(members[2].Root, "missing") };
        var release = new TaskCompletionSource<ProviderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var search = new WorkspaceSearchSession(
            new(members[0], members),
            current,
            (member, _) =>
            {
                if (inaccessible && member.Id == members[2].Id)
                {
                    throw new UnauthorizedAccessException("fixture repository configuration is inaccessible");
                }

                var registry = new ProviderRegistry();
                registry.Register(
                    new ProviderRegistryTests.Fake("slow", [Capabilities.SymbolSearch], execute: (_, _) => release.Task)
                );
                return registry;
            },
            lightCap: TimeSpan.FromMilliseconds(100)
        );
        try
        {
            var answer = await search
                .QueryAsync(new(Capabilities.SymbolSearch, "Widget"), false, "", Token)
                .WaitAsync(TimeSpan.FromSeconds(3), Token);
            answer.Hits.Should().ContainSingle().Which.Member.Name.Should().Be("alpha");
            answer.Errors.Should().ContainSingle().Which.Should().Contain("gamma");
            answer.Notes.Should().ContainSingle().Which.Should().Contain("beta");
        }
        finally
        {
            release.TrySetException(new IOException("late fixture fault"));
        }
    }

    internal sealed class EvidenceProvider(
        string name,
        string[] capabilities,
        Func<ProviderRequest, CancellationToken, Task<ProviderResult>> execute
    ) : IProvider
    {
        public string Name => name;

        public IReadOnlyList<CapabilityInfo> Capabilities() =>
            capabilities.Select(capability => new CapabilityInfo(capability, name, Evidence: "exact")).ToArray();

        public Task<ProviderResult> ExecuteAsync(
            ProviderRequest request,
            CancellationToken cancellationToken = default
        ) => execute(request, cancellationToken);

        public ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProviderHealth("ready"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WidgetProvider(Action dispose) : IProvider
    {
        public string Name => "fixture";

        public IReadOnlyList<CapabilityInfo> Capabilities() => [new(Core.Providers.Capabilities.SymbolSearch, Name)];

        public Task<ProviderResult> ExecuteAsync(
            ProviderRequest request,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                new ProviderResult
                {
                    Items =
                    [
                        new()
                        {
                            Name = "Widget",
                            Path = "a.cs",
                            Provider = Name,
                        },
                    ],
                }
            );

        public ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProviderHealth("ready"));

        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class WorkspaceFixture : IDisposable
{
    public string Global { get; } = Directory.CreateTempSubdirectory("harness-workspace-global-").FullName;
    public List<SyncFixture> Repositories { get; } = [];
    public List<WorkspaceMember> Members { get; } = [];
    public ProjectLifecycle Lifecycle => new(Path.Combine(Global, "config.toml"), "test");
    public WorkspaceStore Store => new(Lifecycle.GlobalConfigurationPath, Lifecycle.Registry);

    public static async Task<WorkspaceFixture> CreateAsync()
    {
        var fixture = new WorkspaceFixture();
        try
        {
            foreach (var name in new[] { "alpha", "beta", "gamma" })
            {
                var repo = await SyncFixture.CreateAsync();
                fixture.Repositories.Add(repo);
                var initialized = await fixture.Lifecycle.InitAsync(
                    repo.Root,
                    cancellationToken: TestContext.Current.CancellationToken
                );
                var project = initialized.Registration!.Project;
                await fixture.Lifecycle.Registry.RenameAsync(
                    project.Id,
                    name,
                    null,
                    TestContext.Current.CancellationToken
                );
                fixture.Members.Add(new(name, project.Id, repo.Root, project.DatabasePath));
            }
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    public async Task<HarnessStateSession> SessionAsync(int member)
    {
        var location = await Lifecycle.LocateAsync(
            Members[member].Root,
            cancellationToken: TestContext.Current.CancellationToken
        );
        return new(location, Lifecycle.Registry, new("test", "", "test", "test"), Lifecycle.GlobalConfigurationPath);
    }

    public string[] Hashes() => Members.Select(member => Hash(member.DatabasePath)).ToArray();

    [SuppressMessage(
        "Major Code Smell",
        "S2925:Thread.Sleep should not be used in tests",
        Justification = "Synchronous fixture reads briefly yield while a live server holds a SQLite lock."
    )]
    private static string Hash(string path)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (IOException error)
                when (error is not FileNotFoundException && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                // On macOS the shared flock .NET takes conflicts with SQLite's fcntl write locks (e.g. a close checkpoint).
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        foreach (var repo in Repositories)
        {
            repo.Dispose();
        }

        Directory.Delete(Global, recursive: true);
    }
}
