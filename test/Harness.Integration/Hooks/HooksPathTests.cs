using AwesomeAssertions;
using Harness.Infrastructure.Clients;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Hooks;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Hooks;

public sealed class HooksPathTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The hooks folder as harness found it before TASK-0101: git config --get, run each time.</summary>
    private static async Task<string> GitsAnswerAsync(GitRepository repository)
    {
        var configured = await SetupQuery.QueryAsync(
            "git",
            ["-C", repository.Root, "config", "--get", "core.hooksPath"],
            Token
        );
        return configured.Length is 0
            ? Path.Combine(repository.CommonDir, "hooks")
            : Path.GetFullPath(configured, repository.Root);
    }

    /// <summary>The folder GitHooks gives, which must be git's, and the git processes it started.</summary>
    private static async Task<(string Folder, int Starts)> HarnessAnswerAsync(GitRepository repository)
    {
        GitHooks? hooks = null;
        var starts = await SyncGitStartsTests.CountAsync(
            repository.Root,
            async () => hooks = await GitHooks.ForAsync(repository, Token)
        );
        hooks!.DirectoryPath.Should().Be(await GitsAnswerAsync(repository));
        return (hooks.DirectoryPath, starts.Count);
    }

    [Fact]
    public async Task TheHooksFolderIsGitsWhereverItIsSetAndReadAgainOnlyWhenItsConfigurationChanges()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var repository = await GitRepository.DiscoverAsync(fixture.Root, Token);
        // relative folders resolve against the physical work tree (macOS: /private/var, not /var)
        var root = repository.Root;

        // unset: the repository's hooks folder; git is asked once, then the answer is kept
        (await HarnessAnswerAsync(repository)).Should().Be((Path.Combine(repository.CommonDir, "hooks"), 1));
        (await HarnessAnswerAsync(repository)).Starts.Should().Be(0);

        // relative to the work tree
        await fixture.GitAsync("config", "core.hooksPath", "githooks");
        (await HarnessAnswerAsync(repository)).Should().Be((Path.Combine(root, "githooks"), 1));
        (await HarnessAnswerAsync(repository)).Starts.Should().Be(0);

        // absolute
        var absolute = Path.Combine(fixture.DirectoryRoot, "shared-hooks");
        await fixture.GitAsync("config", "core.hooksPath", absolute);
        (await HarnessAnswerAsync(repository)).Folder.Should().Be(absolute);

        // from an included file, which is watched as well
        await fixture.GitAsync("config", "--unset", "core.hooksPath");
        var included = Path.Combine(fixture.DirectoryRoot, "included.cfg");
        await File.WriteAllTextAsync(included, "[core]\n\thooksPath = from-include\n", Token);
        await fixture.GitAsync("config", "include.path", included);
        (await HarnessAnswerAsync(repository)).Folder.Should().Be(Path.Combine(root, "from-include"));
        (await HarnessAnswerAsync(repository)).Starts.Should().Be(0);
        await File.WriteAllTextAsync(included, "[core]\n\thooksPath = changed-in-include\n", Token);
        (await HarnessAnswerAsync(repository)).Should().Be((Path.Combine(root, "changed-in-include"), 1));
    }

    [Fact]
    public async Task ALinkedWorktreesOwnConfigurationGivesItsOwnHooksFolder()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var linked = Path.Combine(fixture.DirectoryRoot, "linked");
        await fixture.GitAsync("worktree", "add", "-q", linked, "-b", "side");
        await fixture.GitAsync("config", "extensions.worktreeConfig", "true");
        var main = await GitRepository.DiscoverAsync(fixture.Root, Token);
        var worktree = await GitRepository.DiscoverAsync(linked, Token);
        (await HarnessAnswerAsync(worktree)).Folder.Should().Be(Path.Combine(main.CommonDir, "hooks"));

        // set in the linked worktree's config.worktree only
        await fixture.GitAsync("-C", linked, "config", "--worktree", "core.hooksPath", "worktree-hooks");
        (await HarnessAnswerAsync(worktree)).Should().Be((Path.Combine(worktree.Root, "worktree-hooks"), 1));
        (await HarnessAnswerAsync(main)).Folder.Should().Be(Path.Combine(main.CommonDir, "hooks"));
    }

    [Fact]
    public void TheLastValueOfAKeyIsGitsAnswerAndEveryFileItCameFromIsWatched()
    {
        const string Output =
            "file:C:/Program Files/Git/etc/gitconfig\0core.autocrlf\ntrue\0"
            + "file:.git/config\0core.hookspath\nfirst\0"
            + "file:/home/u/inc.cfg\0core.hookspath\nsecond\0"
            + "file:.git/config\0core.bare\0";
        var (value, files) = GitRepository.ParseConfigList(Output, "core.hookspath");
        value.Should().Be("second");
        files.Should().Equal("C:/Program Files/Git/etc/gitconfig", ".git/config", "/home/u/inc.cfg", ".git/config");
    }
}
