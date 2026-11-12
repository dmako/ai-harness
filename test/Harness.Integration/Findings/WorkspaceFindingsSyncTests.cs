using AwesomeAssertions;
using Harness.Core.Architecture;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Integration.Workspaces;
using Xunit;

namespace Harness.Integration.Findings;

public sealed class WorkspaceFindingsSyncTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorkspaceFindingsAreComputedOnceFromTheProjectReadAndAgainOnlyWhenAMemberChanged()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.Store.CreateAsync("suite", Token);
        await fixture.Store.JoinAsync("suite", fixture.Members.Select(m => m.Id).ToArray(), Token);
        foreach (var repo in fixture.Repositories)
        {
            repo.Write(
                "App.csproj",
                """
                <Project>
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.0" /></ItemGroup>
                </Project>
                """
            );
            repo.Write("appsettings.json", """{"ConnectionStrings":{"Main":"Server=db;Database=shared"}}""");
            repo.Write("Program.cs", "[ServiceContract]\ninterface IShared {}\nclass SharedDbContext : DbContext {}");
        }

        var levels = new KnowledgeLevels(fixture.Lifecycle.GlobalConfigurationPath, fixture.Lifecycle.Registry);

        // One member's sync of its findings, as ProjectSynchronizer runs it.
        async Task<(FindingSync Workspace, FindingSets Sets)> SyncAsync(int member)
        {
            using var db = fixture.Repositories[member].Database();
            var sets = new FindingSets(
                fixture.Members[member].Root,
                fixture.Members[member].Id,
                db,
                fixture.Lifecycle.GlobalConfigurationPath,
                fixture.Lifecycle.Registry
            );
            var results = await sets.SyncAsync("", null, null, false, Token);
            results.Should().OnlyContain(result => result.Error.Length == 0);
            return (results.Single(result => result.Where == "workspace suite"), sets);
        }

        // The stored workspace findings equal a full computation from scratch.
        async Task StoredAreFullAsync()
        {
            var full = (
                await new ArchitectureReader(fixture.Members).SystemAsync(Token, "suite", levels)
            ).Findings.Select(Show);
            using var db = HarnessDatabase.OpenReadOnly(levels.KnowledgePath(levels.Load(), new("workspace", "suite")));
            new Harness.Infrastructure.Findings.FindingStore(db)
                .Current(Token)
                .Findings.Where(finding => finding.Status is not "gone")
                .Select(Show)
                .Should()
                .BeEquivalentTo(full);
        }

        var (first, sets) = await SyncAsync(0);
        first.Kept.Should().BeNull();
        first.Result.Should().NotBeNull();
        first.Result.Found.Should().BeGreaterThan(0);
        sets.Evidence!.Counts.Listed.Should().Be(3, "each member is listed once for the project and the workspace");
        sets.Evidence.Counts.Built.Should().Be(3, "each member is built once for the project and the workspace");
        await StoredAreFullAsync();

        // Nothing changed: another member's sync keeps them.
        (await SyncAsync(1))
            .Workspace.Kept.Should()
            .Be(first.Result.Found);

        // An uncommitted edit in a member, not yet synced there.
        fixture
            .Repositories[2]
            .Write("appsettings.json", """{"ConnectionStrings":{"Main":"Server=db;Database=own"}}""");
        (await SyncAsync(0)).Workspace.Kept.Should().BeNull();
        await StoredAreFullAsync();
        (await SyncAsync(0)).Workspace.Kept.Should().NotBeNull();

        // A commit in a member.
        await fixture.Repositories[1].CommitAsync("member work");
        (await SyncAsync(0)).Workspace.Kept.Should().BeNull();
        await StoredAreFullAsync();

        // A branch switch in a member.
        await fixture.Repositories[2].GitAsync("checkout", "-q", "-b", "elsewhere");
        (await SyncAsync(1)).Workspace.Kept.Should().BeNull();
        await StoredAreFullAsync();
        (await SyncAsync(0)).Workspace.Kept.Should().NotBeNull();

        // A member's configuration.
        fixture.Repositories[0].Write(".harness/config.toml", "[sync]\nignore = [\"docs/**\"]\n");
        (await SyncAsync(1)).Workspace.Kept.Should().BeNull();
        await StoredAreFullAsync();

        // Knowledge the findings quote, and the same file edited again.
        using (var workspace = levels.OpenLevel(levels.Load(), new("workspace", "suite"), fixture.Members[0].Id, Token))
        {
            new MemoryStore(workspace).Put(
                new()
                {
                    Type = "decision",
                    Content = "Shared code lives in the platform library",
                    Force = true,
                },
                Token
            );
        }

        (await SyncAsync(0)).Workspace.Kept.Should().BeNull();
        fixture
            .Repositories[2]
            .Write("appsettings.json", """{"ConnectionStrings":{"Main":"Server=db;Database=shared"}}""");
        (await SyncAsync(0)).Workspace.Kept.Should().BeNull();
        await StoredAreFullAsync();
    }

    private static string Show(ArchitectureFinding finding) =>
        string.Join(
            '|',
            finding.Kind,
            finding.Key,
            finding.Title,
            finding.Level,
            string.Join(';', finding.Evidence ?? [])
        );
}
