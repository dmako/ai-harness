using AwesomeAssertions;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Scm;
using Harness.Scm;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class ScmProjectResolutionTests : IDisposable
{
    private static readonly ScmEndpoint Work = Endpoint("work", ScmKind.Gitlab, "https://gitlab.example.com");
    private static readonly ScmEndpoint Forge = Endpoint("forge", ScmKind.Forgejo, "https://git.example.org");
    private readonly string _directory = Directory.CreateTempSubdirectory("harness-scm-resolve-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, true);

    private static ScmEndpoint Endpoint(string name, ScmKind kind, string url)
    {
        ScmEndpoint.TryNormalizeUrl(url, out var normalized, out _);
        return new ScmEndpoint(name, kind, normalized);
    }

    [Fact]
    public void OriginIsTriedFirstAndTheFirstMatchingRemoteWins()
    {
        ScmRemote[] remotes =
        [
            new("upstream", "https://git.example.org/owner/repo.git"),
            new("origin", "git@gitlab.example.com:group/sub/repo.git"),
        ];

        var result = ScmProjectMatcher.Resolve([Work, Forge], remotes);

        result.Status.Should().Be(ScmResolutionStatus.Matched);
        result.Remote.Should().Be("origin");
        result.Match!.Endpoint.Should().Be(Work);
        result.Match.Project.Should().Be("group/sub/repo");
        result.Overridden.Should().BeFalse();
        result
            .Describe()
            .Should()
            .Contain("name", "work")
            .And.Contain("type", "gitlab")
            .And.Contain("url", "https://gitlab.example.com")
            .And.Contain("project", "group/sub/repo")
            .And.Contain("remote", "origin");
        result.Summary().Should().Be("work (gitlab) group/sub/repo from remote origin");
    }

    [Fact]
    public void OtherRemotesAndPushUrlsAreTriedInOrderWhenOriginMatchesNothing()
    {
        ScmRemote[] remotes =
        [
            new("origin", "git@github.com:me/repo.git"),
            new("mirror", "https://elsewhere.example.net/me/repo.git", ["git@git.example.org:owner/repo.git"]),
            new("work", "https://gitlab.example.com/team/repo"),
        ];

        var result = ScmProjectMatcher.Resolve([Work, Forge], remotes);

        result.Remote.Should().Be("mirror");
        result.Match!.Endpoint.Should().Be(Forge);
        result.Match.Project.Should().Be("owner/repo");
    }

    [Fact]
    public void NoMatchSaysWhichRemotesAndScmsWereSeenWithoutCredentials()
    {
        var result = ScmProjectMatcher.Resolve(
            [Work],
            [new("origin", "https://me:ghp_secret0123456789@github.com/me/repo.git")]
        );

        result.Status.Should().Be(ScmResolutionStatus.NoMatch);
        result
            .Reason.Should()
            .Contain("origin https://github.com/me/repo.git")
            .And.Contain("work https://gitlab.example.com");
        result.Reason.Should().NotContain("ghp_secret");
        string.Join(",", (string[])result.Describe()["remotes"]!).Should().NotContain("ghp_secret");
        ScmRemoteUrls.Safe("git@host:a/b.git").Should().Be("git@host:a/b.git");
    }

    [Fact]
    public void AmbiguousRemotesStopTheSearchInsteadOfGuessing()
    {
        var twin = Endpoint("twin", ScmKind.Gitlab, "https://gitlab.example.com");

        var result = ScmProjectMatcher.Resolve(
            [Work, twin, Forge],
            [new("origin", "https://gitlab.example.com/group/repo"), new("upstream", "https://git.example.org/o/r")]
        );

        result.Status.Should().Be(ScmResolutionStatus.Ambiguous);
        result.Reason.Should().Contain("origin").And.Contain("work").And.Contain("twin").And.Contain("[scm]");
    }

    [Fact]
    public void NothingConfiguredAndNoRemotesAreReported()
    {
        ScmProjectMatcher
            .Resolve([], [new("origin", "https://gitlab.example.com/g/r")])
            .Status.Should()
            .Be(ScmResolutionStatus.NoScm);
        var bare = ScmProjectMatcher.Resolve([Work], []);
        bare.Status.Should().Be(ScmResolutionStatus.NoRemote);
        bare.Reason.Should().Contain("[scm]");
    }

    [Fact]
    public void TheProjectConfigurationCanChooseTheScmProjectAndRemote()
    {
        ScmRemote[] remotes =
        [
            new("origin", "ssh://git@ssh.gitlab.internal:2222/group/repo.git"),
            new("fork", "https://git.example.org/me/repo"),
        ];

        var fixedProject = ScmProjectMatcher.Resolve(
            [Work, Forge],
            remotes,
            new ScmProjectOverride("WORK", "group/repo")
        );
        fixedProject.Status.Should().Be(ScmResolutionStatus.Matched);
        fixedProject.Match!.Endpoint.Should().Be(Work);
        fixedProject.Match.Project.Should().Be("group/repo");
        fixedProject.Overridden.Should().BeTrue();
        fixedProject.Describe().Should().Contain("overridden", true);

        var byRemote = ScmProjectMatcher.Resolve([Work, Forge], remotes, new ScmProjectOverride(Remote: "fork"));
        byRemote.Remote.Should().Be("fork");
        byRemote.Match!.Project.Should().Be("me/repo");

        var onlyForge = ScmProjectMatcher.Resolve(
            [Work, Forge],
            [new("origin", "https://gitlab.example.com/g/r")],
            new ScmProjectOverride("forge")
        );
        onlyForge.Status.Should().Be(ScmResolutionStatus.NoMatch);

        var renamedProject = ScmProjectMatcher.Resolve(
            [Work],
            [new("origin", "https://gitlab.example.com/old/name")],
            new ScmProjectOverride(Project: "new/name")
        );
        renamedProject.Match!.Project.Should().Be("new/name");
    }

    [Fact]
    public void UnknownScmNamesAndRemotesInTheProjectConfigurationAreInvalid()
    {
        var name = ScmProjectMatcher.Resolve(
            [Work],
            [new("origin", "https://gitlab.example.com/g/r")],
            new ScmProjectOverride("nope", "g/r")
        );
        name.Status.Should().Be(ScmResolutionStatus.Invalid);
        name.Reason.Should().Contain("'nope'").And.Contain("configured: work");

        var remote = ScmProjectMatcher.Resolve(
            [Work],
            [new("origin", "https://gitlab.example.com/g/r")],
            new ScmProjectOverride(Remote: "upstream")
        );
        remote.Status.Should().Be(ScmResolutionStatus.Invalid);
        remote.Reason.Should().Contain("'upstream'").And.Contain("remotes: origin");
    }

    [Fact]
    public void GitConfigFilesAreReadWithSubsectionsQuotesEscapesCommentsAndContinuations()
    {
        var entries = GitConfigFile.Parse(
            """
            # comment
            [core]
                bare = false
            [remote "origin"]
                url = git@gitlab.example.com:group/repo.git ; trailing comment
                fetch = +refs/heads/*:refs/remotes/origin/*
            [Remote "with space"]
                URL = "https://host/a b/repo.git"
                pushurl = https://host/\
            push/repo.git
            [url "git@gitlab.example.com:"]
                insteadOf = https://gitlab.example.com/
            [branch.main]
                remote = origin
            flag
            """
        );

        entries
            .Should()
            .ContainEquivalentOf(
                new GitConfigEntry("REMOTE", "origin", "URL", "git@gitlab.example.com:group/repo.git")
            );
        entries
            .Should()
            .ContainEquivalentOf(new GitConfigEntry("REMOTE", "with space", "URL", "https://host/a b/repo.git"));
        entries
            .Should()
            .ContainEquivalentOf(new GitConfigEntry("REMOTE", "with space", "PUSHURL", "https://host/push/repo.git"));
        entries
            .Should()
            .ContainEquivalentOf(
                new GitConfigEntry("URL", "git@gitlab.example.com:", "INSTEADOF", "https://gitlab.example.com/")
            );
        entries.Should().ContainEquivalentOf(new GitConfigEntry("BRANCH", "main", "REMOTE", "origin"));
        entries.Should().ContainEquivalentOf(new GitConfigEntry("BRANCH", "main", "FLAG", "true"));
    }

    [Fact]
    public async Task RemotesAreReadFromTheRepositoryConfigRewrittenByInsteadOfAndCachedUntilAFileChanges()
    {
        var common = Path.Combine(_directory, "repo", ".git");
        var home = Path.Combine(_directory, "home");
        Directory.CreateDirectory(common);
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(
            Path.Combine(home, ".gitconfig"),
            "[url \"git@gitlab.example.com:\"]\n\tinsteadOf = gl:\n[url \"https://git.example.org/\"]\n\tinsteadOf = fj:\n\tinsteadOf = fj:special/\n",
            Token
        );
        var config = Path.Combine(common, "config");
        await File.WriteAllTextAsync(
            config,
            "[remote \"upstream\"]\n\turl = fj:owner/repo.git\n[remote \"origin\"]\n\turl = gl:group/sub/repo.git\n\tpushurl = gl:group/sub/push.git\n",
            Token
        );
        var store = new ScmConfigurationFile(Path.Combine(_directory, "scm.toml"));
        await store.AddAsync(
            new ScmEntryInput("work", "gitlab", "https://gitlab.example.com", "token-0123456789", null),
            Token
        );
        var resolver = new ScmProjectResolver(store, home);

        var remotes = resolver.Remotes(common);
        remotes
            .Select(remote => (remote.Name, remote.Address))
            .Should()
            .Equal(
                ("upstream", "https://git.example.org/owner/repo.git"),
                ("origin", "git@gitlab.example.com:group/sub/repo.git")
            );
        remotes[1].PushAddresses.Should().Equal("git@gitlab.example.com:group/sub/push.git");
        ScmProjectResolver.Rewrite("fj:special/x", [("fj:", "A/"), ("fj:special/", "B/")]).Should().Be("B/x");
        resolver.Remotes(common).Should().BeSameAs(remotes);

        await File.AppendAllTextAsync(config, "[remote \"extra\"]\n\turl = https://example.net/a/b\n", Token);
        File.SetLastWriteTimeUtc(config, DateTime.UtcNow.AddMinutes(1));
        resolver.Remotes(common).Select(remote => remote.Name).Should().Equal("upstream", "origin", "extra");

        var resolution = await resolver.ResolveAsync(Path.Combine(_directory, "not-a-repo"), null, Token);
        resolution.Status.Should().Be(ScmResolutionStatus.NoRemote);
    }

    [Fact]
    public void UnreadableScmFilesAreReportedAsInvalid()
    {
        var path = Path.Combine(_directory, "scm.toml");
        File.WriteAllText(path, "[scm.x\n");

        var result = new ScmProjectResolver(new ScmConfigurationFile(path)).Resolve(null, null);

        result.Status.Should().Be(ScmResolutionStatus.Invalid);
        result.Reason.Should().Contain("not valid TOML");
    }

    [Theory]
    [InlineData("[scm]\nname = \"work\"\nproject = \"group/repo\"\nremote = \"origin\"\n", null)]
    [InlineData("[scm]\nname = \"-bad\"\n", "scm.name")]
    [InlineData("[scm]\nname = \"work\"\nproject = \"repo\"\n", "scm.project")]
    [InlineData("[scm]\nproject = \"group/repo\"\n", "needs scm.name")]
    [InlineData("[scm]\nhost = \"x\"\n", "host")]
    public void TheScmTableIsValidatedWhenConfigurationLoads(string toml, string? error)
    {
        var project = Path.Combine(_directory, "project.toml");
        File.WriteAllText(project, toml);

        var load = () => ConfigurationLoader.LoadFiles("", project);

        if (error is null)
        {
            load().Value.Scm.ToOverride().Should().Be(new ScmProjectOverride("work", "group/repo", "origin"));
        }
        else
        {
            load.Should().Throw<InvalidDataException>().WithMessage($"*{error}*");
        }
    }
}
