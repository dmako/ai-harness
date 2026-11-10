using AwesomeAssertions;
using Harness.Core.Sync;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Sync;
using Xunit;

namespace Harness.Integration.Sync;

public sealed class SyncGitStartsTests
{
    /// <summary>The git processes started for <paramref name="root"/> while <paramref name="work"/> runs.</summary>
    internal static async Task<List<string>> CountAsync(string root, Func<Task> work)
    {
        List<string> starts = [];
        // git runs in the repository's physical root (macOS reaches its temporary folder through /var and /private/var)
        var physical = GitRepository.PhysicalDirectory(Path.GetFullPath(root));
        void Count(string started, IReadOnlyList<string> arguments)
        {
            if (GitRepository.PathEquals(started, physical))
            {
                lock (starts)
                {
                    starts.Add(string.Join(' ', arguments));
                }
            }
        }

        GitRepository.Started += Count;
        try
        {
            await work();
        }
        finally
        {
            GitRepository.Started -= Count;
        }

        return starts;
    }

    /// <summary>A fixture with a C# project, so that the sync's findings read the repository's files.</summary>
    private static async Task<SyncFixture> ProjectAsync()
    {
        var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "app/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        fixture.Write("app/Program.cs", "class Program { static void Main() {} }");
        await fixture.CommitAsync("project");
        return fixture;
    }

    [Fact]
    public async Task AnIncrementalSyncChecksItsBaseOnceAndListsItsFilesOnce()
    {
        using var fixture = await ProjectAsync();
        await fixture.RunAsync(new() { Mode = "full" });
        fixture.Write("a.cs", "changed");
        fixture.Write("c.cs", "added");
        await fixture.CommitAsync("change");
        fixture.Write("untracked.cs", "new");
        File.Delete(Path.Combine(fixture.Root, "b.cs"));

        ProjectSyncResult? result = null;
        var starts = await CountAsync(fixture.Root, async () => result = await fixture.RunAsync());

        result!.Files!.Mode.Should().Be("incremental");
        result
            .Files.Changes.Select(change => $"{change.Kind} {change.Path}")
            .Should()
            .BeEquivalentTo("modified a.cs", "added c.cs", "added untracked.cs", "deleted b.cs");
        fixture
            .Hashes()
            .Keys.Should()
            .BeEquivalentTo("a.cs", "c.cs", "untracked.cs", ".gitignore", "app/App.csproj", "app/Program.cs");
        result.Findings.Should().Contain(findings => findings.Where == "project" && findings.Result != null);
        // cat-file, diff, one ls-files for what changed and for the findings, status; and, since an untracked
        // file came with a deleted one, the rename check's ls-tree and hash-object
        var renames = starts.Where(s =>
            s.StartsWith("ls-tree", StringComparison.Ordinal) || s.StartsWith("hash-object", StringComparison.Ordinal)
        );
        starts.Except(renames).Should().HaveCount(4, string.Join("; ", starts));
        starts.Count(s => s.StartsWith("cat-file", StringComparison.Ordinal)).Should().Be(1);
        starts.Count(s => s.StartsWith("ls-files", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task AFullSyncListsItsFilesOnceForItselfAndItsFindings()
    {
        using var fixture = await ProjectAsync();
        ProjectSyncResult? result = null;
        var starts = await CountAsync(
            fixture.Root,
            async () => result = await fixture.RunAsync(new() { Mode = "full" })
        );

        result!.Files!.Mode.Should().Be("full");
        result.Findings.Should().Contain(findings => findings.Where == "project" && findings.Result != null);
        starts.Count(s => s.StartsWith("ls-files", StringComparison.Ordinal)).Should().Be(1, string.Join("; ", starts));
    }

    [Fact]
    public async Task OutsideASyncEachFileListIsReadAgain()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var repository = await GitRepository.DiscoverAsync(fixture.Root, TestContext.Current.CancellationToken);
        var starts = await CountAsync(
            fixture.Root,
            async () =>
            {
                await repository.ListFilesAsync(TestContext.Current.CancellationToken);
                fixture.Write("later.cs", "x");
                (await repository.ListFilesAsync(TestContext.Current.CancellationToken)).Should().Contain("later.cs");
            }
        );
        starts.Should().HaveCount(2);
    }
}
