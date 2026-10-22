using System.Diagnostics.CodeAnalysis;
using System.Text;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Retrieval;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Retrieval;

public sealed class RetrievalIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The disposed registry owns its fixture provider."
    )]
    public async Task RealMemoryAndGitFeedApplicableContext()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write("a.cs", "changed");
        await fixture.CommitAsync("update A");
        fixture.Write("README.md", "unrelated documentation");
        await fixture.CommitAsync("unrelated");
        var repository = await GitRepository.DiscoverAsync(fixture.Root, Token);
        var commits = await repository.RecentCommitsAsync(["a.cs"], 5, Token);
        commits.Select(commit => commit.Subject).Should().Equal("update A", "base");
        commits.Should().OnlyContain(commit => commit.Paths.Count == 1 && commit.Paths[0] == "a.cs");
        (await repository.RecentCommitsAsync([], 5, Token)).Should().BeEmpty();
        (await repository.RecentCommitsAsync(["a.cs"], 0, Token)).Should().BeEmpty();
        using var database = fixture.Database();
        var store = new MemoryStore(database);
        var decision = store
            .Put(
                new()
                {
                    Type = "decision",
                    Content = "Keep operations deterministic",
                    Scope = new("path", "a.cs"),
                },
                Token
            )
            .Memory!;
        var goal = store.Put(new() { Type = "goal", Content = "Optimize C# intelligence first" }, Token).Memory!;
        await using var registry = new ProviderRegistry();
        registry.Register(
            new ProviderRegistryTests.Fake(
                "code",
                [Capabilities.SymbolSearch],
                execute: (_, _) =>
                    Task.FromResult(
                        new ProviderResult
                        {
                            Items =
                            [
                                new()
                                {
                                    Name = "A",
                                    Path = "a.cs",
                                    Provider = "code",
                                },
                            ],
                        }
                    )
            )
        );
        var gathered = await RetrievalGatherer.GatherAsync(
            new(
                registry,
                (query, token) => Task.FromResult(new RetrievalMemories(store.Search(query, token))),
                repository.RecentCommitsAsync,
                "main"
            ),
            new("inspect operations"),
            Token
        );
        gathered.Errors.Should().BeEmpty();
        gathered.Candidates.Single(candidate => candidate.Id == "memory:" + decision.Id).Scores["scope"].Should().Be(1);
        gathered.Candidates.Single(candidate => candidate.Id == "memory:" + goal.Id).Scores["scope"].Should().Be(0.2);
        gathered
            .Candidates.Where(candidate => candidate.Source == "git")
            .Select(candidate => candidate.Scores["recency"])
            .Should()
            .Equal(1, 0.5);
    }

    [Fact]
    public async Task PhysicalFileReadsCacheSuccessAndFailureOnlyForTheirRequest()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write("a.cs", "first\r\né😀\nlast");
        var reader = new RetrievalFileReader(fixture.Root);
        (await reader.ReadBodyAsync("a.cs", 2, 99, Token)).Should().Be("é😀\nlast");
        fixture.Write("a.cs", "updated");
        (await reader.ReadBodyAsync("a.cs", 1, 1, Token)).Should().Be("first\r");
        (await new RetrievalFileReader(fixture.Root).ReadBodyAsync("a.cs", 1, 1, Token)).Should().Be("updated");
        await FluentActions
            .Awaiting(() => reader.ReadBodyAsync("missing.cs", 1, 1, Token))
            .Should()
            .ThrowAsync<FileNotFoundException>();
        fixture.Write("missing.cs", "now present");
        await FluentActions
            .Awaiting(() => reader.ReadBodyAsync("missing.cs", 1, 1, Token))
            .Should()
            .ThrowAsync<FileNotFoundException>();
        (await new RetrievalFileReader(fixture.Root).ReadBodyAsync("missing.cs", 1, 1, Token))
            .Should()
            .Be("now present");
    }

    [Fact]
    public async Task PhysicalReadsRejectOutsidePathsInvalidUtf8AndOversizedFiles()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var reader = new RetrievalFileReader(fixture.Root);
        await FluentActions
            .Awaiting(() => reader.ReadBodyAsync("../outside.cs", 1, 1, Token))
            .Should()
            .ThrowAsync<InvalidDataException>();
        await FluentActions
            .Awaiting(() => reader.ReadBodyAsync(Path.Combine(fixture.Root, "a.cs"), 1, 1, Token))
            .Should()
            .ThrowAsync<InvalidDataException>();
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "invalid.cs"), new byte[] { 0xff }, Token);
        await FluentActions
            .Awaiting(() => reader.ReadBodyAsync("invalid.cs", 1, 1, Token))
            .Should()
            .ThrowAsync<DecoderFallbackException>();
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "large.cs"), new byte[4 * 1024 * 1024 + 1], Token);
        await FluentActions
            .Awaiting(() => reader.ReadBodyAsync("large.cs", 1, 1, Token))
            .Should()
            .ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task UnixFileAndDirectoryLinksCannotEscapeTheRepository()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Unix symlink fixture; exercised in Linux validation.");
        }

        using var fixture = await SyncFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryRoot, "outside.cs"), "outside", Token);
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "escape.cs"), "../outside.cs");
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "escape"), "..");
        File.CreateSymbolicLink(Path.Combine(fixture.Root, "inside.cs"), "a.cs");
        var reader = new RetrievalFileReader(fixture.Root);
        await FluentActions
            .Awaiting(() => reader.ReadBodyAsync("escape.cs", 1, 1, Token))
            .Should()
            .ThrowAsync<InvalidDataException>();
        await FluentActions
            .Awaiting(() => reader.ReadBodyAsync("escape/outside.cs", 1, 1, Token))
            .Should()
            .ThrowAsync<InvalidDataException>();
        (await reader.ReadBodyAsync("inside.cs", 1, 1, Token)).Should().Be("a");
    }
}
