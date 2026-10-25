using AwesomeAssertions;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Graphify;
using Harness.Infrastructure.Workspaces;
using Harness.Integration.Sync;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration;

public sealed class DirectoryAliasTests
{
    [Fact]
    public async Task ParentDirectoryAliasesPreserveWorkspaceSessionAndWorktreeIdentity()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Directory symlink creation may require Windows privileges.");
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write("App.slnx", "<Solution />");
        fixture.Write(".claude/worktrees/linked/.git", "gitdir: /checkout/.git/worktrees/linked\n");
        var alias = Path.Combine(fixture.DirectoryRoot, "alias");
        Directory.CreateSymbolicLink(alias, fixture.DirectoryRoot);
        var aliasedRoot = Path.Combine(alias, "repository");
        var aliasedChild = Path.Combine(aliasedRoot, ".claude", "worktrees", "linked");
        var token = TestContext.Current.CancellationToken;
        var initialized = await fixture.Lifecycle.InitAsync(aliasedRoot, cancellationToken: token);
        var physicalRoot = (await GitRepository.DiscoverAsync(fixture.Root, token)).Root;
        initialized.Location.Root.Should().Be(physicalRoot);
        var store = new WorkspaceStore(fixture.Lifecycle.GlobalConfigurationPath, fixture.Lifecycle.Registry);
        store.Discover(aliasedRoot).Should().ContainSingle().Which["root"]!
            .GetValue<string>()
            .Should()
            .Be(physicalRoot);
        store.Discover(Path.Combine(aliasedRoot, ".claude")).Should().BeEmpty();
        GraphifyDiscovery.Canonical(aliasedRoot).Should().Be(physicalRoot);
        GraphifyDiscovery
            .Canonical(aliasedChild)
            .Should()
            .Be(Path.Combine(physicalRoot, ".claude", "worktrees", "linked"));
        await using var manager = new GraphifySessionManager(new(aliasedRoot, "dotnet"));
        manager.Root.Should().Be(physicalRoot);
        var filter = new NestedWorktreeFilter(aliasedRoot);
        filter.Skip(".claude/worktrees/linked/missing.cs").Should().BeTrue();
        filter.Skip(Path.Combine(aliasedChild, "missing.cs")).Should().BeTrue();
        filter.Skip(Path.Combine(physicalRoot, ".claude", "worktrees", "linked", "missing.cs")).Should().BeTrue();
        filter.Skip(Path.Combine(aliasedRoot, "..", "outside", "missing.cs")).Should().BeFalse();
        filter.Skip("../outside/missing.cs").Should().BeFalse();
    }

    [Fact]
    public async Task CleanupWaitsForTransientWindowsFileLocks()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows prevents deleting open files without delete sharing.");
        var root = Directory.CreateTempSubdirectory("harness-cleanup-").FullName;
        var file = Path.Combine(root, "locked.txt");
        await File.WriteAllTextAsync(file, "fixture", TestContext.Current.CancellationToken);
        using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var cleanup = Task.Run(() => TestDirectory.Delete(root), TestContext.Current.CancellationToken);
            try
            {
                await Task.Delay(200, TestContext.Current.CancellationToken);
                cleanup.IsCompleted.Should().BeFalse("the file is still held without delete sharing");
            }
            finally
            {
                await locked.DisposeAsync();
                await cleanup;
            }
            Directory.Exists(root).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                TestDirectory.Delete(root);
            }
        }
    }
}
