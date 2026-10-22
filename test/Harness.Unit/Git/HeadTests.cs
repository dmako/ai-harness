using AwesomeAssertions;
using Harness.Infrastructure.Git;
using Xunit;

namespace Harness.Unit.Git;

public sealed class HeadTests
{
    [Fact]
    public async Task DiscoveryResolvesGitFilesAndSharedDirectoriesUsingOnlyMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("harness-gitfile-").FullName;
        try
        {
            var common = Path.GetFullPath(Path.Combine(directory, "main/.git"));
            var perWorktree = Path.GetFullPath(Path.Combine(common, "worktrees/linked"));
            var linked = Path.Combine(directory, "linked");
            Directory.CreateDirectory(perWorktree);
            Directory.CreateDirectory(Path.Combine(linked, "child"));
            await File.WriteAllTextAsync(
                Path.Combine(linked, ".git"),
                "gitdir: ../main/.git/worktrees/linked\n",
                TestContext.Current.CancellationToken
            );
            await File.WriteAllTextAsync(
                Path.Combine(perWorktree, "commondir"),
                "../..\n",
                TestContext.Current.CancellationToken
            );
            var repository = await GitRepository.DiscoverAsync(
                Path.Combine(linked, "child"),
                TestContext.Current.CancellationToken
            );
            repository.Root.Should().Be(linked);
            repository.GitDir.Should().Be(perWorktree);
            repository.CommonDir.Should().Be(common);
            repository.Linked.Should().BeTrue();
            repository.StateRoot.Should().Be(Path.Combine(directory, "main"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LoosePackedSymbolicDetachedAndUnbornHeadsAreReadFreshWithoutGitProcesses()
    {
        var directory = Directory.CreateTempSubdirectory("harness-head-").FullName;
        try
        {
            var git = Path.Combine(directory, ".git");
            Directory.CreateDirectory(Path.Combine(git, "refs/heads"));
            var repository = await GitRepository.DiscoverAsync(directory, TestContext.Current.CancellationToken);
            var headFile = Path.Combine(git, "HEAD");
            await File.WriteAllTextAsync(headFile, "ref: refs/heads/main\n", TestContext.Current.CancellationToken);
            (await repository.ReadHeadAsync(TestContext.Current.CancellationToken))
                .Should()
                .Be(new GitHead("main", "", false, true));
            var first = new string('a', 40);
            var second = new string('b', 64);
            var main = Path.Combine(git, "refs/heads/main");
            await File.WriteAllTextAsync(main, first, TestContext.Current.CancellationToken);
            (await repository.ReadHeadAsync(TestContext.Current.CancellationToken)).Commit.Should().Be(first);
            await File.WriteAllTextAsync(main, second, TestContext.Current.CancellationToken);
            (await repository.ReadHeadAsync(TestContext.Current.CancellationToken)).Commit.Should().Be(second);
            File.Delete(main);
            await File.WriteAllTextAsync(
                Path.Combine(git, "packed-refs"),
                "# pack-refs\n" + first + " refs/heads/main\n^" + second + "\n",
                TestContext.Current.CancellationToken
            );
            (await repository.ReadHeadAsync(TestContext.Current.CancellationToken)).Commit.Should().Be(first);
            await File.WriteAllTextAsync(main, "ref: refs/heads/other", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(git, "refs/heads/other"),
                second,
                TestContext.Current.CancellationToken
            );
            (await repository.ReadHeadAsync(TestContext.Current.CancellationToken))
                .Should()
                .Be(new GitHead("main", second, false, false));
            await File.WriteAllTextAsync(headFile, first, TestContext.Current.CancellationToken);
            (await repository.ReadHeadAsync(TestContext.Current.CancellationToken))
                .Should()
                .Be(new GitHead("", first, true, false));
            await File.WriteAllTextAsync(headFile, "ref: ../outside", TestContext.Current.CancellationToken);
            await FluentActions
                .Awaiting(() => repository.ReadHeadAsync(TestContext.Current.CancellationToken))
                .Should()
                .ThrowAsync<InvalidDataException>();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LinkedWorktreeReadsBranchRefsFromCommonDirectoryAndBoundsSymbolicCycles()
    {
        var directory = Directory.CreateTempSubdirectory("harness-linked-head-").FullName;
        try
        {
            var common = Path.Combine(directory, ".git");
            var worktree = Path.GetFullPath(Path.Combine(common, "worktrees/linked"));
            Directory.CreateDirectory(worktree);
            Directory.CreateDirectory(Path.Combine(common, "refs/heads"));
            var repository = new GitRepository(directory, worktree, common, true);
            await File.WriteAllTextAsync(
                Path.Combine(worktree, "HEAD"),
                "ref: refs/heads/main",
                TestContext.Current.CancellationToken
            );
            await File.WriteAllTextAsync(
                Path.Combine(common, "refs/heads/main"),
                new string('c', 40),
                TestContext.Current.CancellationToken
            );
            (await repository.ReadHeadAsync(TestContext.Current.CancellationToken))
                .Commit.Should()
                .Be(new string('c', 40));
            await File.WriteAllTextAsync(
                Path.Combine(common, "refs/heads/main"),
                "ref: refs/heads/main",
                TestContext.Current.CancellationToken
            );
            await FluentActions
                .Awaiting(() => repository.ReadHeadAsync(TestContext.Current.CancellationToken))
                .Should()
                .ThrowAsync<InvalidDataException>()
                .WithMessage("*Too many*");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
