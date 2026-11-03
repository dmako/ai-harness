using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Projects;
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
