using AwesomeAssertions;
using Harness.Scm;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class ScmMatcherTests
{
    private static readonly ScmEndpoint GitlabCom = Endpoint("gitlab", ScmKind.Gitlab, "https://gitlab.com");
    private static readonly ScmEndpoint Forgejo = Endpoint("forge", ScmKind.Forgejo, "https://git.example.org");

    private static ScmEndpoint Endpoint(string name, ScmKind kind, string url)
    {
        ScmEndpoint.TryNormalizeUrl(url, out var normalized, out var error).Should().BeTrue(error);
        return new ScmEndpoint(name, kind, normalized);
    }

    [Theory]
    [InlineData("https://gitlab.com/group/repo.git", "https", "gitlab.com", null, "group/repo")]
    [InlineData("https://gitlab.com/group/repo", "https", "gitlab.com", null, "group/repo")]
    [InlineData("https://gitlab.com/group/repo/", "https", "gitlab.com", null, "group/repo")]
    [InlineData("http://host:8080/a/b/c.git", "http", "host", 8080, "a/b/c")]
    [InlineData("https://user@GitLab.COM/Group/Repo.git", "https", "gitlab.com", null, "Group/Repo")]
    [InlineData("ssh://git@host:2222/group/sub/repo.git", "ssh", "host", 2222, "group/sub/repo")]
    [InlineData("ssh://host/group/repo", "ssh", "host", null, "group/repo")]
    [InlineData("git+ssh://git@host/group/repo.git", "ssh", "host", null, "group/repo")]
    [InlineData("git@gitlab.com:group/sub/repo.git", "ssh", "gitlab.com", null, "group/sub/repo")]
    [InlineData("gitlab.com:group/repo", "ssh", "gitlab.com", null, "group/repo")]
    [InlineData("git@host:/group/repo.git/", "ssh", "host", null, "group/repo")]
    [InlineData("git://host/group/repo.git", "git", "host", null, "group/repo")]
    public void RemoteUrlsOfEveryFormSplitIntoHostPortAndPath(
        string address,
        string scheme,
        string host,
        int? port,
        string path
    )
    {
        ScmRemoteUrl.TryParse(address, out var remote).Should().BeTrue();
        remote!.Scheme.Should().Be(scheme);
        remote.Host.Should().Be(host);
        remote.Port.Should().Be(port);
        string.Join('/', remote.Path).Should().Be(path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\repos\project")]
    [InlineData("C:/repos/project")]
    [InlineData("/srv/git/project.git")]
    [InlineData("../project")]
    [InlineData("file:///srv/git/project.git")]
    [InlineData("https://host")]
    public void LocalPathsAndUrlsWithoutAProjectAreNotRemotes(string address) =>
        ScmRemoteUrl.TryParse(address, out _).Should().BeFalse();

    [Theory]
    [InlineData("https://gitlab.com/group/repo.git", "group/repo")]
    [InlineData("git@gitlab.com:group/sub/subsub/repo.git", "group/sub/subsub/repo")]
    [InlineData("ssh://git@gitlab.com:2222/group/sub/repo.git", "group/sub/repo")]
    [InlineData("https://GITLAB.com/Group/Repo", "Group/Repo")]
    public void GitlabRemotesMatchWithTheirFullNamespacePath(string address, string project)
    {
        var result = ScmMatcher.Match([GitlabCom, Forgejo], address);

        result.Status.Should().Be(ScmMatchStatus.Matched);
        result.Match!.Endpoint.Should().Be(GitlabCom);
        result.Match.Project.Should().Be(project);
    }

    [Fact]
    public void ForgejoRemotesNeedExactlyOwnerAndRepository()
    {
        ScmMatcher.Match([Forgejo], "git@git.example.org:owner/repo.git").Match!.Project.Should().Be("owner/repo");
        ScmMatcher
            .Match([Forgejo], "https://git.example.org/owner/sub/repo")
            .Status.Should()
            .Be(ScmMatchStatus.NoMatch);
        ScmMatcher.Match([Forgejo], "https://git.example.org/repo").Status.Should().Be(ScmMatchStatus.NoMatch);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("git@GitHub.com:owner/repo")]
    public void GithubRemotesMatchAsOwnerAndRepository(string address)
    {
        var github = Endpoint("github", ScmKind.Github, "https://github.com");

        var result = ScmMatcher.Match([GitlabCom, Forgejo, github], address);

        result.Status.Should().Be(ScmMatchStatus.Matched);
        result.Match!.Endpoint.Should().Be(github);
        result.Match.Project.Should().Be("owner/repo");
        ScmMatcher.Match([github], "https://github.com/owner/repo/tree").Status.Should().Be(ScmMatchStatus.NoMatch);
    }

    [Fact]
    public void GithubEnterpriseHostsMatchByTheirConfiguredBaseUrl()
    {
        var enterprise = Endpoint("ghe", ScmKind.Github, "https://ghe.example.com");
        var dotCom = Endpoint("github", ScmKind.Github, "https://github.com");

        ScmMatcher
            .Match([dotCom, enterprise], "git@ghe.example.com:team/service.git")
            .Match!.Endpoint.Should()
            .Be(enterprise);
        ScmMatcher.Match([enterprise], "https://github.com/team/service").Status.Should().Be(ScmMatchStatus.NoMatch);
    }

    [Fact]
    public void InstancesUnderASubPathMatchHttpRemotesOnlyBelowItAndStripIt()
    {
        var sub = Endpoint("corp", ScmKind.Gitlab, "https://corp.example.com/gitlab/");

        var http = ScmMatcher.Match([sub], "https://corp.example.com/gitlab/team/repo.git");
        http.Match!.Project.Should().Be("team/repo");
        sub.BasePath.Should().Equal("gitlab");

        ScmMatcher.Match([sub], "https://corp.example.com/team/repo.git").Status.Should().Be(ScmMatchStatus.NoMatch);
        // GitLab's ssh clone URLs leave the relative URL root out
        ScmMatcher.Match([sub], "git@corp.example.com:team/repo.git").Match!.Project.Should().Be("team/repo");
    }

    [Fact]
    public void TheLongestBasePathWins()
    {
        var root = Endpoint("root", ScmKind.Gitlab, "https://host");
        var sub = Endpoint("sub", ScmKind.Gitlab, "https://host/gitlab");

        var result = ScmMatcher.Match([root, sub], "https://host/gitlab/team/repo");

        result.Match!.Endpoint.Should().Be(sub);
        result.Match.Project.Should().Be("team/repo");
        ScmMatcher.Match([root, sub], "https://host/team/repo").Match!.Endpoint.Should().Be(root);
    }

    [Fact]
    public void HttpRemotesMustUseTheSamePortWhileSshRemotesMayUseAny()
    {
        var custom = Endpoint("custom", ScmKind.Forgejo, "http://host:3000");

        ScmMatcher.Match([custom], "http://host:3000/o/r.git").Status.Should().Be(ScmMatchStatus.Matched);
        ScmMatcher.Match([custom], "http://host/o/r.git").Status.Should().Be(ScmMatchStatus.NoMatch);
        ScmMatcher.Match([custom], "ssh://git@host:2222/o/r.git").Status.Should().Be(ScmMatchStatus.Matched);
        ScmMatcher.Match([GitlabCom], "https://gitlab.com:443/group/repo").Status.Should().Be(ScmMatchStatus.Matched);
        ScmMatcher.Match([GitlabCom], "https://gitlab.com:8443/group/repo").Status.Should().Be(ScmMatchStatus.NoMatch);
    }

    [Fact]
    public void UnknownHostsAndUnparsableRemotesDoNotMatchAndSayWhy()
    {
        var other = ScmMatcher.Match([GitlabCom, Forgejo], "git@github.com:owner/repo.git");
        other.Status.Should().Be(ScmMatchStatus.NoMatch);
        other.Reason.Should().Contain("github.com").And.Contain("gitlab https://gitlab.com/");

        ScmMatcher.Match([GitlabCom], @"C:\repo").Status.Should().Be(ScmMatchStatus.NoMatch);
        ScmMatcher.Match([], "https://gitlab.com/g/r").Reason.Should().Be("No SCM is configured.");
    }

    [Fact]
    public void EquallyGoodMatchesAreAmbiguousRatherThanGuessed()
    {
        var first = Endpoint("one", ScmKind.Gitlab, "https://gitlab.com");
        var second = Endpoint("two", ScmKind.Gitlab, "https://gitlab.com/");

        var result = ScmMatcher.Match([first, second], "https://gitlab.com/group/repo");

        result.Status.Should().Be(ScmMatchStatus.Ambiguous);
        result.Candidates.Should().HaveCount(2);
        result.Reason.Should().Contain("one").And.Contain("two");
    }

    [Theory]
    [InlineData("https://gitlab.example.com/", "https://gitlab.example.com/")]
    [InlineData(" https://host/gitlab/ ", "https://host/gitlab")]
    [InlineData("http://host:3000", "http://host:3000/")]
    public void BaseUrlsAreNormalised(string input, string expected)
    {
        ScmEndpoint.TryNormalizeUrl(input, out var url, out _).Should().BeTrue();
        url.AbsoluteUri.Should().Be(expected);
    }

    [Theory]
    [InlineData("gitlab.example.com")]
    [InlineData("ftp://host")]
    [InlineData("https://host/?x=1")]
    [InlineData("https://user:pw@host")]
    public void BaseUrlsMustBePlainHttpAddresses(string input) =>
        ScmEndpoint.TryNormalizeUrl(input, out _, out var error).Should().BeFalse(error);

    private static ScmEndpoint Aliased(string name, ScmKind kind, string url, params string[] aliases)
    {
        var endpoint = Endpoint(name, kind, url);
        return endpoint with { Aliases = ScmEntryRules.Aliases(aliases, endpoint.BaseUrl) };
    }

    [Theory]
    [InlineData("https://git-alias.example.com/group/sub/repo.git", "group/sub/repo")]
    [InlineData("git@git-alias.example.com:group/repo.git", "group/repo")]
    [InlineData("ssh://git@git-alias.example.com:2222/group/repo.git", "group/repo")]
    [InlineData("git@ssh-alias:group/repo.git", "group/repo")]
    [InlineData("ssh://ssh-alias/group/repo", "group/repo")]
    [InlineData("https://ssh-alias/gitlab/group/repo", "group/repo")]
    [InlineData("https://gitlab.example.com/gitlab/group/repo", "group/repo")]
    public void RemotesMatchThroughAnAliasWithTheProjectPathTheUrlWouldGive(string address, string project)
    {
        var work = Aliased(
            "work",
            ScmKind.Gitlab,
            "https://gitlab.example.com/gitlab",
            "https://git-alias.example.com",
            "ssh-alias"
        );

        var result = ScmMatcher.Match([work, GitlabCom], address);

        result.Status.Should().Be(ScmMatchStatus.Matched, result.Reason);
        result.Match!.Endpoint.Should().Be(work);
        result.Match.Project.Should().Be(project);
    }

    [Theory]
    [InlineData("https://git-alias.example.com:8443/group/repo")]
    [InlineData("https://other.example.com/group/repo")]
    [InlineData("git@other-alias:group/repo.git")]
    [InlineData("https://gitlab.example.com/group/repo")]
    public void RemotesThatNoUrlOrAliasServesStayUnmatched(string address)
    {
        var work = Aliased(
            "work",
            ScmKind.Gitlab,
            "https://gitlab.example.com/gitlab",
            "https://git-alias.example.com",
            "ssh-alias"
        );

        var result = ScmMatcher.Match([work], address);

        result.Status.Should().Be(ScmMatchStatus.NoMatch);
        result.Reason.Should().Contain("aliases https://git-alias.example.com, ssh-alias");
    }

    [Theory]
    [InlineData("https://proxy.example.com/scm/group/repo.git", ScmMatchStatus.Matched)]
    [InlineData("https://proxy.example.com/group/repo.git", ScmMatchStatus.NoMatch)]
    [InlineData("git@proxy.example.com:scm/group/repo.git", ScmMatchStatus.Matched)]
    [InlineData("git@proxy.example.com:group/repo.git", ScmMatchStatus.Matched)]
    [InlineData("ssh://git@ssh.example.com:2222/git/group/repo.git", ScmMatchStatus.Matched)]
    [InlineData("ssh://git@ssh.example.com:2222/group/repo.git", ScmMatchStatus.NoMatch)]
    [InlineData("ssh://git@ssh.example.com/git/group/repo.git", ScmMatchStatus.NoMatch)]
    [InlineData("https://ssh.example.com/git/group/repo.git", ScmMatchStatus.NoMatch)]
    public void AnAliasPathPrefixStandsForTheUrlsBasePath(string address, ScmMatchStatus status)
    {
        var forge = Aliased(
            "forge",
            ScmKind.Forgejo,
            "https://git.example.org/forgejo",
            "https://proxy.example.com/scm/",
            "ssh://git@ssh.example.com:2222/git"
        );

        var result = ScmMatcher.Match([forge], address);

        result.Status.Should().Be(status, result.Reason);
        if (status is ScmMatchStatus.Matched)
        {
            result.Match!.Project.Should().Be("group/repo");
        }
    }

    [Fact]
    public void AnAliasThatTwoScmsShareIsAmbiguous()
    {
        var one = Aliased("one", ScmKind.Gitlab, "https://gitlab-a.example.com", "shared-alias");
        var two = Aliased("two", ScmKind.Gitlab, "https://gitlab-b.example.com", "https://shared-alias");

        var result = ScmMatcher.Match([one, two], "git@shared-alias:group/repo.git");

        result.Status.Should().Be(ScmMatchStatus.Ambiguous);
        result.Candidates.Select(endpoint => endpoint.Name).Should().Equal("one", "two");
    }

    [Theory]
    [InlineData(" GIT-Alias.Example.com ", "git-alias.example.com")]
    [InlineData("https://Git-Alias.example.com/", "https://git-alias.example.com")]
    [InlineData("https://git-alias.example.com:443/scm/", "https://git-alias.example.com/scm")]
    [InlineData("http://git-alias:8080", "http://git-alias:8080")]
    [InlineData("ssh://git@ssh-alias:22/", "ssh://ssh-alias")]
    [InlineData("ssh://git@ssh-alias:2222/git", "ssh://ssh-alias:2222/git")]
    [InlineData("git+ssh://ssh-alias", "ssh://ssh-alias")]
    public void AliasesAreNormalised(string input, string expected)
    {
        ScmAlias.TryParse(input, out var alias, out var error).Should().BeTrue(error);
        alias!.Text.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://host")]
    [InlineData("https://host/?x=1")]
    [InlineData("https://user:secret@host")]
    [InlineData("git@host:group")]
    [InlineData("host name")]
    public void AliasesMustBeHostNamesOrBaseUrls(string input)
    {
        ScmAlias.TryParse(input, out _, out var error).Should().BeFalse();
        error.Should().NotBeEmpty().And.NotContain("secret");
    }
}
