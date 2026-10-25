using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Findings;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Sync;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Harness.Integration.Workspaces;
using Harness.Providers.Registry;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Integration.Findings;

[Collection("Provider processes")]
public sealed class FindingsProcessTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublicToolsFollowSchemasAndRequireProposalApproval()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "src/Domain/Domain.csproj",
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Dal/Dal.csproj\" /></ItemGroup></Project>"
        );
        fixture.Write(
            "src/Dal/Dal.csproj",
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        using var db = fixture.Database();
        var rule = new MemoryStore(db)
            .Put(
                new()
                {
                    Type = "constraint",
                    Content = "forbid: Domain -> Dal",
                    Force = true,
                },
                Token
            )
            .Memory!;
        var result = await fixture.RunAsync();
        result.Findings.Should().ContainSingle().Which.Error.Should().BeEmpty();
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
        await using var client = await McpClient.CreateAsync(
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
        );
        var tools = await client.ListToolsAsync(cancellationToken: Token);
        tools.Should().HaveCount(30);
        foreach (var name in new[] { "architecture_findings", "architecture_propose" })
        {
            ToolSchema.AssertMatches(tools.Single(tool => tool.Name == name));
        }
        var full = await CallAsync("architecture_findings", new() { ["kind"] = "boundary", ["snippets"] = true });
        var boundary = full.GetProperty("groups")[0].GetProperty("findings").EnumerateArray().Single();
        var key = boundary.GetProperty("key").GetString()!;
        boundary.GetProperty("rule").GetProperty("memory").GetString().Should().Be(rule.Id);
        full.GetProperty("groups")[0].GetProperty("code").GetArrayLength().Should().BeGreaterThan(0);
        var proposed = await CallAsync(
            "architecture_propose",
            new()
            {
                ["kind"] = "by_design",
                ["finding"] = key,
                ["reason"] = "A deliberate compatibility boundary",
            }
        );
        proposed.GetProperty("status").GetString().Should().Be("pending");
        proposed.GetProperty("proposal").TryGetProperty("decided_by", out _).Should().BeFalse();
        new TaskStore(db).List(cancellationToken: Token).Should().BeEmpty();
        new FindingStore(db).MarkOf(key, Token).Should().BeNull();

        var duplicate = await CallAsync(
            "architecture_propose",
            new()
            {
                ["kind"] = "by_design",
                ["finding"] = key,
                ["reason"] = "Another reason",
            }
        );
        duplicate.GetProperty("status").GetString().Should().Be("exists");
        var id = proposed.GetProperty("proposal").GetProperty("id").GetString()!;
        var project = result.Registration.Project.Id;
        await new FindingSets(
            fixture.Root,
            project,
            db,
            fixture.Lifecycle.GlobalConfigurationPath,
            fixture.Lifecycle.Registry
        ).ApproveAsync(id, "human", token: Token);
        var hidden = await CallAsync("architecture_findings", new() { ["kind"] = "boundary", ["hide_marked"] = true });
        hidden.GetProperty("groups")[0].GetProperty("marked_hidden").GetInt32().Should().Be(1);
        hidden.GetProperty("groups")[0].TryGetProperty("findings", out _).Should().BeFalse();
        var brief = await CallAsync("architecture_findings", []);
        brief
            .GetProperty("groups")[0]
            .GetProperty("brief")
            .EnumerateArray()
            .Should()
            .Contain(f => f.GetProperty("key").GetString() == key && f.GetProperty("mark").GetString() == "by_design");
        var invalid = await client.CallToolAsync(
            "architecture_findings",
            new Dictionary<string, object?> { ["scope"] = "invalid" },
            cancellationToken: Token
        );
        invalid.IsError.Should().BeTrue();
        var badArgument = await client.CallToolAsync(
            "architecture_propose",
            new Dictionary<string, object?> { ["kind"] = "task", ["unknown"] = true },
            cancellationToken: Token
        );
        badArgument.IsError.Should().BeTrue();
        // The existing memory proposal path corrects drift without changing files or silently settling a decision.
        var memoryProposal = await CallAsync(
            "memory_propose",
            new()
            {
                ["type"] = "constraint",
                ["content"] = "forbid: Domain -> Other",
                ["supersedes"] = rule.Id,
                ["rationale"] = "Boundary changed",
            }
        );
        memoryProposal.GetProperty("proposal").GetProperty("supersedes").GetString().Should().Be(rule.Id);
        new MemoryStore(db).Get(rule.Id, Token).SupersededBy.Should().BeEmpty();
        fixture.Write(
            "src/Domain/Domain.csproj",
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        await fixture.RunAsync();
        var gone = await CallAsync("architecture_findings", new() { ["key"] = key, ["gone"] = true });
        gone.GetProperty("groups")[0].GetProperty("findings")[0].GetProperty("status").GetString().Should().Be("gone");
        var suppressed = await CallAsync("architecture_findings", new() { ["key"] = key });
        suppressed.GetProperty("groups")[0].TryGetProperty("findings", out _).Should().BeFalse();
        async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args)
        {
            var response = await client.CallToolAsync(tool, args, cancellationToken: Token);
            response
                .IsError.Should()
                .BeNull(string.Join("; ", response.Content.OfType<TextContentBlock>().Select(c => c.Text)));
            using var text = JsonDocument.Parse(response.Content.OfType<TextContentBlock>().Single().Text);
            JsonElement.DeepEquals(response.StructuredContent!.Value, text.RootElement).Should().BeTrue();
            return response.StructuredContent.Value;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Register transfers provider ownership to the await-using registry."
    )]
    [Fact]
    public async Task FailedProviderKeepsLastEvidenceAndHealthyEmptyAnswerRemovesIt()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "App.csproj",
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        await using var providers = new ProviderRegistry();
        var phase = 0;
        providers.Register(
            new ProviderRegistryTests.Fake(
                "fixture",
                [Capabilities.ArchCycles],
                execute: (_, _) =>
                    Task.FromResult(
                        phase switch
                        {
                            0 => new ProviderResult
                            {
                                Items =
                                [
                                    new()
                                    {
                                        Provider = "fixture",
                                        Tool = "cycles",
                                        Members = ["A", "B"],
                                    },
                                ],
                            },
                            1 => new ProviderResult { Error = new IOException("fixture failure") },
                            _ => new ProviderResult(),
                        }
                    )
            )
        );
        var sync = new ProjectSynchronizer(fixture.Lifecycle, providers: providers);
        await sync.RunAsync(fixture.Root, token: Token);
        using var db = fixture.Database();
        new FindingStore(db).Current(Token).Findings.Should().Contain(f => f.Source.Provider == "fixture");
        var project = (await sync.RunAsync(fixture.Root, token: Token)).Registration.Project.Id;
        var before = providers.Providers().OfType<ProviderRegistryTests.Fake>().Single().Calls;
        var quick = new FindingSets(
            fixture.Root,
            project,
            db,
            fixture.Lifecycle.GlobalConfigurationPath,
            fixture.Lifecycle.Registry
        );
        (await quick.ReadAsync("project", Token))
            .Groups.Single()
            .Findings.Should()
            .Contain(f => f.Source.Provider == "fixture");
        providers.Providers().OfType<ProviderRegistryTests.Fake>().Single().Calls.Should().Be(before);
        phase = 1;
        await sync.RunAsync(fixture.Root, token: Token);
        new FindingStore(db).Current(Token).Findings.Should().Contain(f => f.Source.Provider == "fixture");
        phase = 2;
        await sync.RunAsync(fixture.Root, token: Token);
        new FindingStore(db).Current(Token).Findings.Should().NotContain(f => f.Source.Provider == "fixture");
    }

    [Fact]
    public async Task QuickReadsRetainSyncOnlyDriftUntilNextSync()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "App.csproj",
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        using var db = fixture.Database();
        var memory = new MemoryStore(db)
            .Put(
                new()
                {
                    Type = "decision",
                    Content = "Keep `MissingPolicy` as the authority",
                    Force = true,
                },
                Token
            )
            .Memory!;
        var synced = await fixture.RunAsync();
        var sets = new FindingSets(
            fixture.Root,
            synced.Registration.Project.Id,
            db,
            fixture.Lifecycle.GlobalConfigurationPath,
            fixture.Lifecycle.Registry
        );
        fixture.Write("Policy.cs", "class MissingPolicy {}");
        (await sets.ReadAsync("project", Token))
            .Groups.Single()
            .Findings.Should()
            .Contain(f => f.Kind == "stale-knowledge" && f.Rule!.Memory == memory.Id);
        await fixture.RunAsync();
        (await sets.ReadAsync("project", Token))
            .Groups.Single()
            .Findings.Should()
            .NotContain(f => f.Kind == "stale-knowledge" && f.Status != "gone");
    }

    [Fact]
    public async Task SyncRecordsComplexityAndThresholdChangesKeepTheFinding()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "App.csproj",
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        fixture.Write(
            "Router.cs",
            "namespace App;\npublic class Router\n{\n    public int Route(int a)\n    {\n"
                + string.Concat(Enumerable.Repeat("        if (a > 0) { a--; }\n", 13))
                + "        return a;\n    }\n}\n"
        );
        using var db = fixture.Database();
        var synced = await fixture.RunAsync();
        var sets = new FindingSets(
            fixture.Root,
            synced.Registration.Project.Id,
            db,
            fixture.Lifecycle.GlobalConfigurationPath,
            fixture.Lifecycle.Registry
        );
        var first = (await sets.ReadAsync("project", Token))
            .Groups.Single()
            .Findings.Should()
            .ContainSingle(f => f.Kind == "complexity")
            .Which;
        first.Key.Should().Be("complexity:Router.cs#Router.Route");
        first.Level.Should().Be("info");
        first.Measure.Should().Be(14);

        fixture.Write(".harness/config.toml", "[architecture.complexity]\nnote = 5\nwarn = 10\nproblem = 30\n");
        await fixture.RunAsync();
        var second = (await sets.ReadAsync("project", Token))
            .Groups.Single()
            .Findings.Should()
            .ContainSingle(f => f.Kind == "complexity")
            .Which;
        second.Key.Should().Be(first.Key);
        second.Level.Should().Be("warn");
        second.Status.Should().Be("changed");
    }

    [Fact]
    public async Task WorkspaceProposalsKeepMarksInWorkspaceAndTasksInCurrentProject()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.Store.CreateAsync("suite", Token);
        await fixture.Store.JoinAsync("suite", fixture.Members.Select(m => m.Id).ToArray(), Token);
        foreach (var repo in fixture.Repositories)
        {
            repo.Write(
                "App.csproj",
                "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
            );
            repo.Write("appsettings.json", """{"ConnectionStrings":{"Main":"Server=db;Database=shared"}}""");
            repo.Write("Program.cs", "[ServiceContract]\ninterface IShared {}\nclass SharedDbContext : DbContext {}");
        }
        var root = fixture.Repositories[0];
        var sync = await new ProjectSynchronizer(fixture.Lifecycle).RunAsync(root.Root, token: Token);
        sync.Findings.Should().Contain(f => f.Where == "workspace suite" && f.Error.Length == 0);
        using var db = root.Database();
        var sets = new FindingSets(
            root.Root,
            fixture.Members[0].Id,
            db,
            fixture.Lifecycle.GlobalConfigurationPath,
            fixture.Lifecycle.Registry
        );
        var read = await sets.ReadAsync("workspace", Token);
        var finding = read.Groups.Single().Findings.First(f => f.Kind is "shared-database");
        var proposal = await sets.ProposeAsync(
            new("by_design", finding.Key, "workspace", Reason: "These are one bounded context"),
            "agent",
            Token
        );
        await sets.ApproveAsync(proposal.Proposal.Id, "author", token: Token);
        new FindingStore(db).MarkOf(finding.Key, Token).Should().BeNull();
        var levels = new KnowledgeLevels(fixture.Lifecycle.GlobalConfigurationPath, fixture.Lifecycle.Registry);
        using var workspaceDb = HarnessDatabase.OpenReadOnly(
            levels.KnowledgePath(levels.Load(), new("workspace", "suite"))
        );
        new FindingStore(workspaceDb).MarkOf(finding.Key, Token)!.State.Should().Be("by_design");
        var task = await sets.ProposeAsync(new("task", finding.Key, "workspace"), "agent", Token);
        await sets.ApproveAsync(task.Proposal.Id, "author", token: Token);
        new TaskStore(db).List(cancellationToken: Token).Should().ContainSingle().Which.Id.Should().Be("ARCH-0001");
        new FindingStore(workspaceDb).MarkOf(finding.Key, Token)!.TaskProject.Should().Be(fixture.Members[0].Id);
        var obsolete = await sets.ProposeAsync(
            new("by_design", finding.Key, "workspace", Reason: "obsolete"),
            "agent",
            Token
        );
        await fixture.Store.LeaveAsync("suite", fixture.Members[0].Id, Token);
        var action = () => sets.ApproveAsync(obsolete.Proposal.Id, "author", token: Token);
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no longer in workspace*");
    }
}
