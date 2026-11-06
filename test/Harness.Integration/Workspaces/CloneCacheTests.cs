using AwesomeAssertions;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Architecture.Clones;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Sync;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Workspaces;

public sealed class CloneCacheTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Account =
        "class A { public int Read(int id) { var value = repository.Find(id); if (value is null) { throw new InvalidOperationException(\"missing account\"); } return value.AccountId; } }";

    [Fact]
    public async Task ASyncLexesOnlyTheFilesThatChangedAndFindsWhatAFullReadFinds()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write("src/a.cs", Account);
        fixture.Write("src/b.cs", Account.Replace("class A", "class B", StringComparison.Ordinal));
        fixture.Write("src/c.cs", "class C { public int Twice(int x) { if (x > 0) { return x * 2; } return 0; } }");
        fixture.Write("pkg/one.go", "package pkg\n\nfunc One(x int) int {\n\tif x > 1 {\n\t\treturn x\n\t}\n\treturn Two(x)\n}\n");
        fixture.Write("pkg/two.go", "package pkg\n\nfunc Two(x int) int {\n\treturn x + 1\n}\n");
        await fixture.CommitAsync("sources");
        var cache = new CloneCache(Path.Combine(fixture.DirectoryRoot, "cache"));
        // The cache is keyed by the repository's physical root, as sync records it (macOS: /private/var, not /var).
        var root = (await GitRepository.DiscoverAsync(fixture.Root, Token)).Root;
        // The fixture's own a.cs and b.cs are C# files too.
        const int Files = 7;

        async Task<(CloneExtractor Extractor, AnalysisRepository Repository)> SyncAsync()
        {
            await fixture.RunAsync();
            cache.Record(root, fixture.Hashes());
            var repository = await AnalysisRepository.OpenAsync(fixture.Root, Token);
            var extractor = new CloneExtractor(repository, cache);
            extractor.Extract();
            return (extractor, repository);
        }

        async Task<string[]> FullAsync()
        {
            var reader = new RepeatedCodeReader(await AnalysisRepository.OpenAsync(fixture.Root, Token));
            return Show(reader.Read(), reader.Extractor);
        }

        var first = await SyncAsync();
        first.Extractor.Lexed.Should().Be(Files);
        File.Exists(cache.PathFor(root)).Should().BeTrue();

        var unchanged = await SyncAsync();
        unchanged.Extractor.Lexed.Should().Be(0, "no file changed");
        unchanged.Repository.PhysicalReads.Should().Be(0, "recorded hashes spare reading unchanged files");
        Show(new RepeatedCodeReader(unchanged.Repository, cache).Read(), unchanged.Extractor)
            .Should()
            .Equal(await FullAsync());

        fixture.Write("src/c.cs", "class C { public int Twice(int x) { if (x > 1) { return x * 2; } return 1; } }");
        var one = await SyncAsync();
        one.Extractor.Lexed.Should().Be(1, "only the changed file is lexed");
        var repeated = new RepeatedCodeReader(one.Repository, cache);
        Show(repeated.Read(), repeated.Extractor).Should().Equal(await FullAsync());

        // A Go file's functions depend on its package: its package is read again with it.
        fixture.Write("pkg/two.go", "package pkg\n\nfunc Two(x int) int {\n\treturn x + 2\n}\n");
        (await SyncAsync()).Extractor.Lexed.Should().Be(2);

        // Without recorded hashes (a read outside sync) files are read and hashed, and still not lexed again.
        var plain = new CloneExtractor(await AnalysisRepository.OpenAsync(fixture.Root, Token), new(cache.Directory));
        plain.Extract();
        plain.Lexed.Should().Be(0);

        // Another extractor version or harness build, or an unreadable cache, starts over.
        var other = new CloneExtractor(
            await AnalysisRepository.OpenAsync(fixture.Root, Token),
            new(cache.Directory, "another build")
        );
        other.Extract();
        other.Lexed.Should().Be(Files);
        await File.WriteAllTextAsync(cache.PathFor(root), "not a cache", Token);
        var corrupt = new CloneExtractor(await AnalysisRepository.OpenAsync(fixture.Root, Token), cache);
        corrupt.Extract();
        corrupt.Lexed.Should().Be(Files);
        File.Delete(Path.Combine(fixture.Root, "src", "b.cs"));
        var removed = await SyncAsync();
        removed.Extractor.Lexed.Should().Be(0);
        removed.Extractor.Extract().Should().NotContain(function => function.Path == "src/b.cs");
    }

    private static string[] Show(IReadOnlyList<Harness.Core.Architecture.ArchitectureFinding> findings, CloneExtractor extractor) =>
        findings
            .Select(f => string.Join('|', f.Kind, f.Key, f.Title, f.Why, f.Rank, string.Join(';', f.Evidence ?? [])))
            .Concat(extractor.Complexity().Select(member => member.ToString()))
            .Concat(extractor.Branches().Select(branch => branch.Path + ":" + branch.Line))
            .ToArray();
}
