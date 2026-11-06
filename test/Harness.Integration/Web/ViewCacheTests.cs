using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Workspaces;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Web;

/// <summary>harness ui's view cache: kept while what a view reads is unchanged, recomputed after any change.</summary>
public sealed class ViewCacheTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AViewIsKeptUntilAnEditACommitACheckoutOrItsDatabaseChanges()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var views = new ViewCache(fixture.Lifecycle);
        ViewMember[] members = [new("prj_test", fixture.Root, fixture.DatabasePath)];
        var computed = 0;
        Task<JsonNode?> Compute() => Task.FromResult<JsonNode?>(new JsonObject { ["n"] = ++computed });
        Task<JsonNode?> GetAsync() => views.GetAsync("project:prj_test:structure", members, Compute, Token);

        (await GetAsync())!["n"]!.GetValue<int>().Should().Be(1);
        (await GetAsync())!["n"]!.GetValue<int>().Should().Be(1, "nothing changed");

        // build output and harness's own state are not what a view reads
        fixture.Write("bin/Debug/out.txt", "built");
        fixture.Write(".harness/scratch.tmp", "state");
        await Task.Delay(500, Token);
        (await GetAsync())!["n"]!.GetValue<int>().Should().Be(1, "ignored paths keep the view");

        fixture.Write("a.cs", "edited");
        (await EventuallyAsync(GetAsync, 2)).Should().BeTrue("an edited tracked file recomputes");

        await fixture.CommitAsync("edit a");
        (await GetAsync())!["n"]!.GetValue<int>().Should().Be(3, "a commit moves HEAD");

        await fixture.GitAsync("switch", "-q", "-c", "older", "HEAD~1");
        (await EventuallyAsync(GetAsync, 4)).Should().BeTrue("a checkout recomputes");

        using (var database = fixture.Database())
        {
            new MemoryStore(database).Put(
                new()
                {
                    Type = "decision",
                    Content = "Views read the knowledge too",
                    Source = new("user", "test", "test"),
                },
                Token
            );
        }

        (await GetAsync())!["n"]!.GetValue<int>().Should().Be(5, "a database change recomputes");
        (await GetAsync())!["n"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task TheCacheIsBoundedAndHandsOutCopies()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var views = new ViewCache(fixture.Lifecycle);
        ViewMember[] members = [new("prj_test", fixture.Root, fixture.DatabasePath)];
        for (var index = 0; index < ViewCache.Capacity + 10; index++)
        {
            await views.GetAsync(
                "view:" + index,
                members,
                () => Task.FromResult<JsonNode?>(new JsonObject { ["n"] = 1 }),
                Token
            );
        }

        views.Count.Should().Be(ViewCache.Capacity);
        var first = await views.GetAsync("copied", members, () => Task.FromResult<JsonNode?>(new JsonObject()), Token);
        first!["changed"] = true;
        (await views.GetAsync("copied", members, () => Task.FromResult<JsonNode?>(null), Token))!
            .AsObject()
            .ContainsKey("changed")
            .Should()
            .BeFalse("a caller's change does not reach the cache");
    }

    [Fact]
    public async Task AnAnalysisIsSharedUntilAnEditOrACommitButOutlivesDatabaseWritesAndIsBounded()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var views = new ViewCache(fixture.Lifecycle);
        ViewMember[] members = [new("prj_test", fixture.Root, fixture.DatabasePath)];
        var made = 0;
        Task<object> GetAsync() => views.AnalysisAsync(members, () => (object)++made, Token);

        (await GetAsync()).Should().Be(1);
        (await GetAsync()).Should().Be(1, "nothing changed");

        // the analysis reads only the working tree: a knowledge write keeps it (the views over it follow it)
        using (var database = fixture.Database())
        {
            new MemoryStore(database).Put(
                new()
                {
                    Type = "decision",
                    Content = "Analyses read only the trees",
                    Source = new("user", "test", "test"),
                },
                Token
            );
        }
        (await GetAsync()).Should().Be(1, "a database write keeps the analysis");

        fixture.Write("a.cs", "edited");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((int)await GetAsync() != 2)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "an edit replaces the analysis");
            await Task.Delay(100, Token);
        }
        await fixture.CommitAsync("edit a");
        (await GetAsync()).Should().Be(3, "a commit moves HEAD");

        // one per member set, the least recently used dropped first
        for (var index = 0; index < ViewCache.Analyses + 2; index++)
        {
            await views.AnalysisAsync<object>(
                [new("prj_" + index, fixture.Root, fixture.DatabasePath)],
                () => index,
                Token
            );
        }
        views.AnalysisCount.Should().Be(ViewCache.Analyses);
    }

    [Fact]
    public async Task RequestsSharingAnAnalysisReadEachMemberAndComputeEachPieceOnce()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write("Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Shop.Lib</PackageId></PropertyGroup></Project>");
        fixture.Write("Lib/Store.cs", "namespace Shop.Lib;\npublic sealed class OrderStore {}\n");
        using var other = await SyncFixture.CreateAsync();
        other.Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Shop.Lib\" Version=\"1.0.0\" /></ItemGroup></Project>");
        other.Write("App/OrderStore.cs", "namespace Shop.App;\npublic sealed class OrderStore {}\n");
        WorkspaceMember[] members =
        [
            new("lib", "prj_lib", fixture.Root, fixture.DatabasePath),
            new("app", "prj_app", other.Root, other.DatabasePath),
        ];
        var evidence = new WorkspaceEvidenceReader(members, shared: true);

        // the overview's calls arrive together: they take turns and each piece is read once
        var answers = await Task.WhenAll(
            Enumerable
                .Range(0, 6)
                .Select(index =>
                    evidence.ExclusiveAsync(
                        async () =>
                        {
                            await evidence.WarmAsync(index % 2 is 0 ? MemberReads.Stacks | MemberReads.Sources : MemberReads.Stacks, Token);
                            return (
                                Graph: await evidence.DependenciesAsync(Token),
                                Summary: await evidence.DependencySummaryAsync(Token),
                                Stack: await evidence.WorkspaceStackAsync(Token),
                                Duplicates: await new WorkspaceDuplicateReader(members, evidence).ReadAsync(Token)
                            );
                        },
                        Token
                    )
                )
        );
        answers.Select(a => a.Graph).Distinct().Should().ContainSingle("the dependency graph is computed once");
        answers.Select(a => a.Summary).Distinct().Should().ContainSingle();
        answers.Select(a => a.Stack).Distinct().Should().ContainSingle();
        answers.Select(a => a.Duplicates).Distinct().Should().ContainSingle("the duplicate index is built once");
        answers[0].Graph.Edges.Should().ContainSingle(edge => edge.From == "prj_app" && edge.To == "prj_lib");
        answers[0].Duplicates.Groups.Should().Contain(group => group.Name == "OrderStore");

        var reads = members.Sum(member => evidence.RepositoryAsync(member, Token).Result.Repository.PhysicalReads);
        await evidence.ExclusiveAsync(
            async () =>
            {
                await evidence.WarmAsync(MemberReads.Stacks | MemberReads.Sources, Token);
                return await new ArchitectureReader(members, evidence).SystemAsync(Token);
            },
            Token
        );
        members
            .Sum(member => evidence.RepositoryAsync(member, Token).Result.Repository.PhysicalReads)
            .Should()
            .Be(reads, "the system view reads what the others already read");

        // a cancelled request leaves the shared repositories usable by the next
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var refused = () => evidence.ExclusiveAsync(() => evidence.DependenciesAsync(cancelled.Token), cancelled.Token);
        await refused.Should().ThrowAsync<OperationCanceledException>();
        (await evidence.ExclusiveAsync(() => evidence.WorkspaceStackAsync(Token), Token))
            .Should()
            .BeSameAs(answers[0].Stack);
    }

    [Fact]
    public async Task ATreeViewIsKeptPerProjectUntilItsManifestOrHeadChangesWhateverIsWritten()
    {
        using var first = await SyncFixture.CreateAsync();
        using var second = await SyncFixture.CreateAsync();
        using var views = new ViewCache(first.Lifecycle);
        Dictionary<string, int> computed = new(StringComparer.Ordinal) { ["first"] = 0, ["second"] = 0 };
        Task<JsonNode?> GetAsync(string name, SyncFixture fixture) =>
            views.TreeViewAsync(
                "stack:" + name,
                [new(name, fixture.Root, "")],
                () => Task.FromResult<JsonNode?>(new JsonObject { ["n"] = ++computed[name] }),
                Token
            );

        (await GetAsync("first", first))!["n"]!.GetValue<int>().Should().Be(1);
        (await GetAsync("second", second))!["n"]!.GetValue<int>().Should().Be(1);
        (await GetAsync("first", first))!["n"]!.GetValue<int>().Should().Be(1, "nothing changed");

        // a database write is no reason to read a stack again
        using (var database = first.Database())
        {
            new MemoryStore(database).Put(
                new()
                {
                    Type = "decision",
                    Content = "Stacks read only the tree",
                    Source = new("user", "test", "test"),
                },
                Token
            );
        }
        (await GetAsync("first", first))!["n"]!.GetValue<int>().Should().Be(1, "a database write keeps it");

        // an edited manifest reads that project again, and only that one
        first.Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await GetAsync("first", first))!["n"]!.GetValue<int>() != 2)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the edited manifest reaches the stack");
            await Task.Delay(100, Token);
        }
        (await GetAsync("second", second))!["n"]!.GetValue<int>().Should().Be(1, "the other project is unchanged");

        second.Write("notes.md", "moved");
        await second.CommitAsync("move HEAD");
        deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await GetAsync("second", second))!["n"]!.GetValue<int>() < 2)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "a commit moves HEAD");
            await Task.Delay(100, Token);
        }
        (await GetAsync("first", first))!["n"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task HarnessUiShowsAProjectsStackAndItsChangeWithoutReadingItAgainMeanwhile()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"
        );
        fixture.Write("App/Program.cs", "class Program {}\n");
        await fixture.CommitAsync("app");
        await using var server = await UiFixture.StartAsync(fixture);
        var id = UiWriteTests.ProjectId(fixture);
        async Task<string> BadgeAsync() =>
            (await UiTests.ReadAsync(server.Client, "/api/stacks"))[id]?["badge"]?.GetValue<string>() ?? "";

        (await BadgeAsync()).Should().Contain(".NET 8");
        fixture.Write(
            "App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!(await BadgeAsync()).Contains(".NET 10", StringComparison.Ordinal))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the edited project file reaches the badge");
            await Task.Delay(200, Token);
        }
    }

    [Fact]
    public async Task HarnessUiShowsAProjectAddedSinceItsStructureWasRead()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var server = await UiFixture.StartAsync(fixture);
        var path = $"/api/projects/{UiWriteTests.ProjectId(fixture)}/structure";
        (await UiTests.ReadAsync(server.Client, path))["known"]!.GetValue<bool>().Should().BeFalse();

        fixture.Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!(await UiTests.ReadAsync(server.Client, path))["known"]!.GetValue<bool>())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the edit reaches the cached structure");
            await Task.Delay(200, Token);
        }
    }

    /// <summary>Asks until the view was computed the given number of times; a watcher reports changes a moment later.</summary>
    private static async Task<bool> EventuallyAsync(Func<Task<JsonNode?>> get, int computed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if ((await get())!["n"]!.GetValue<int>() == computed)
            {
                return true;
            }

            await Task.Delay(100, Token);
        }

        return false;
    }
}
