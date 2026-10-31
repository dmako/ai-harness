using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Infrastructure.Memory;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Memory;

/// <summary>The store's record of shared knowledge, and the file's place in a project.</summary>
public sealed class SharedKnowledgeStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MemoriesRecordWhetherTheyAreSharedAndTheFileItsLastReadHash()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await fixture.Lifecycle.InitAsync(fixture.Root, cancellationToken: Token);
        using var database = fixture.Database();
        var store = new MemoryStore(database);
        var id = store
            .Put(
                new()
                {
                    Type = "decision",
                    Content = "Knowledge is shared through Git",
                    Source = new("user", "test", "test"),
                },
                Token
            )
            .Memory!.Id;

        store.SharedStates(Token).Should().BeEmpty("a new memory is local");
        store.SetShared(id, SharedState.Project, Token);
        store.SharedStates(Token).Should().Equal(new Dictionary<string, string> { [id] = SharedState.Project });
        store.SetShared(id, SharedState.Removed, Token);
        store.SharedStates(Token)[id].Should().Be(SharedState.Removed);
        store.SetShared(id, SharedState.Local, Token);
        store.SharedStates(Token).Should().BeEmpty();
        FluentActions.Invoking(() => store.SetShared(id, "everyone", Token)).Should().Throw<ArgumentException>();
        FluentActions
            .Invoking(() => store.SetShared("mem_none", SharedState.Project, Token))
            .Should()
            .Throw<KeyNotFoundException>();

        var file = KnowledgeFile.In(Path.Combine(fixture.Root, ".harness"));
        store.KnowledgeFileHash(file, Token).Should().BeEmpty("never read");
        store.RecordKnowledgeFile(file, "abc", Token);
        store.RecordKnowledgeFile(file, "def", Token);
        store.KnowledgeFileHash(file, Token).Should().Be("def");
    }

    [Fact]
    public async Task InitLeavesTheSharedKnowledgeFileTrackedByGit()
    {
        using var fixture = await SyncFixture.CreateAsync();
        // the fixture's own .gitignore hides the whole .harness folder; a team sharing knowledge does not
        fixture.Write(".gitignore", "*.log\n");
        await fixture.Lifecycle.InitAsync(fixture.Root, cancellationToken: Token);
        var file = KnowledgeFile.In(Path.Combine(fixture.Root, ".harness"));
        KnowledgeFile.Write(file, [new("mem_a1", "decision", "Shared", MemoryScope.Project)]);

        (await fixture.ProcessAsync("git", ["-C", fixture.Root, "check-ignore", "-q", ".harness/knowledge.toml"]))
            .Code.Should()
            .Be(1, ".harness/.gitignore must not hide the shared knowledge");
        (await fixture.ProcessAsync("git", ["-C", fixture.Root, "check-ignore", "-q", ".harness/harness.db"]))
            .Code.Should()
            .Be(0, "the database stays out of Git");
        (
            await fixture.ProcessAsync(
                "git",
                ["-C", fixture.Root, "status", "--porcelain", "--", ".harness/knowledge.toml"]
            )
        )
            .Output.Should()
            .Contain("knowledge.toml");
    }
}
