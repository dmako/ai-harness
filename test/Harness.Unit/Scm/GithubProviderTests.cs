using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Scm;
using Harness.Scm.Github;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class GithubProviderTests
{
    private const string Secret = "ghp_GithubSecret0123456789abcdef";
    private const string Project = "owner/repo";
    private const string RepoPath = "/repos/owner/repo";
    private const string PullPath = RepoPath + "/pulls/3";
    private const string DiffType = "application/vnd.github.diff";

    private const string PullJson = """
        {"id":501,"number":3,"title":"Fix login","body":"Fixes #2","state":"open",
         "user":{"id":1,"login":"alice","name":"Alice"},"head":{"ref":"fix/login","sha":"headsha"},
         "base":{"ref":"main","sha":"basesha"},
         "html_url":"https://github.com/owner/repo/pull/3","draft":true,
         "created_at":"2026-11-01T08:00:00Z","updated_at":"2026-11-02T08:00:00Z","merged_at":null,
         "labels":[{"name":"bug"}],"requested_reviewers":[{"id":2,"login":"bob"}],"mergeable":false}
        """;

    private const string ReviewsJson = """
        [{"id":71,"user":{"id":2,"login":"bob"},"state":"CHANGES_REQUESTED","body":"Please fix","submitted_at":"2026-11-01T10:00:00Z"},
         {"id":72,"user":{"id":2,"login":"bob"},"state":"APPROVED","body":"","submitted_at":"2026-11-02T10:00:00Z"},
         {"id":73,"user":{"id":3,"login":"carol"},"state":"APPROVED","body":"","submitted_at":"2026-11-02T11:00:00Z"},
         {"id":74,"user":{"id":3,"login":"carol"},"state":"DISMISSED","body":"","submitted_at":"2026-11-02T12:00:00Z"},
         {"id":75,"user":{"id":4,"login":"dave"},"state":"COMMENTED","body":"","submitted_at":"2026-11-02T13:00:00Z"}]
        """;

    private const string Patch = """
        diff --git a/src/login.go b/src/login.go
        index 1111111..2222222 100644
        --- a/src/login.go
        +++ b/src/login.go
        @@ -1,3 +1,3 @@
         package auth
        -func Login() {}
        +func Login() error { return nil }
         // end
        diff --git a/docs/a.md b/docs/b.md
        similarity index 100%
        rename from docs/a.md
        rename to docs/b.md

        """;

    private static readonly ScmLimits Limits = new(TimeSpan.FromSeconds(5), 3, 2, TimeSpan.FromSeconds(1));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ScmEndpoint Endpoint(string url)
    {
        ScmEndpoint.TryNormalizeUrl(url, out var normalized, out var error).Should().BeTrue(error);
        return new ScmEndpoint("github", ScmKind.Github, normalized);
    }

    private static IScmClient Connect(FakeScmServer server, string url = "https://github.com") =>
        new GithubProvider(server, Limits).Connect(new ScmConnection(Endpoint(url), Secret));

    private static FakeResponse Diff(string lineEnding = "\n") =>
        new(HttpStatusCode.OK, Patch.ReplaceLineEndings(lineEnding), DiffType);

    [Theory]
    [InlineData("https://github.com", "https://api.github.com/")]
    [InlineData("https://www.github.com", "https://api.github.com/")]
    [InlineData("https://API.github.com", "https://api.github.com/")]
    [InlineData("https://octo.ghe.com", "https://api.octo.ghe.com/")]
    [InlineData("https://ghe.example.com", "https://ghe.example.com/api/v3/")]
    [InlineData("http://host:8080/github", "http://host:8080/github/api/v3/")]
    public void TheApiLivesOnApiGithubComOrUnderApiV3OnEnterpriseServer(string server, string expected) =>
        GithubProvider.ApiRoot(Endpoint(server)).AbsoluteUri.Should().Be(expected);

    [Fact]
    public async Task ConnectionTestsReadMetaAndTheUserWithABearerTokenOnTheApiHost()
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", "/meta", """{"verifiable_password_authentication":true}""")
            .Json("GET", "/user", """{"id":1,"login":"alice","name":"Alice"}""");
        using var client = Connect(server);

        var test = await client.TestConnectionAsync(Token);

        test.Ok.Should().BeTrue();
        test.Version.Should().Be("github.com");
        test.User.Should().Be(new ScmUser("alice", "Alice"));
        server.Requests.Should().AllSatisfy(request => request.Uri.Host.Should().Be("api.github.com"));
        server
            .Requests.Should()
            .AllSatisfy(request => request.Headers["Authorization"].Should().Be("Bearer " + Secret));
        server.Requests[0].Headers["X-GitHub-Api-Version"].Should().Be("2022-11-28");
    }

    [Fact]
    public async Task EnterpriseServerReportsItsInstalledVersion()
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", "/api/v3/meta", """{"verifiable_password_authentication":true,"installed_version":"3.15.2"}""")
            .Json("GET", "/api/v3/user", """{"id":1,"login":"alice"}""");
        using var client = Connect(server, "https://ghe.example.com");

        var test = await client.TestConnectionAsync(Token);

        test.Version.Should().Be("3.15.2");
        test.User.Should().Be(new ScmUser("alice"));
        server.Requests[0].Uri.AbsoluteUri.Should().Be("https://ghe.example.com/api/v3/meta");
    }

    [Fact]
    public async Task ConnectionFailuresAreCategorised()
    {
        using var gitlab = new FakeScmServer();
        using (var client = Connect(gitlab, "https://gitlab.example.com"))
        {
            var wrong = await client.TestConnectionAsync(Token);
            wrong.Failure.Should().Be(ScmErrorKind.WrongServer);
            wrong.Message.Should().Contain("No GitHub REST API");
        }

        using var otherJson = new FakeScmServer();
        otherJson.Json("GET", "/api/v3/meta", """{"version":"1"}""");
        using (var client = Connect(otherJson, "https://other.example.com"))
        {
            (await client.TestConnectionAsync(Token)).Failure.Should().Be(ScmErrorKind.WrongServer);
        }

        using var html = new FakeScmServer();
        html.On("GET", "/api/v3/meta", new FakeResponse(HttpStatusCode.OK, "<html>login</html>", "text/html"));
        using (var client = Connect(html, "https://proxy.example.com"))
        {
            (await client.TestConnectionAsync(Token)).Failure.Should().Be(ScmErrorKind.WrongServer);
        }

        using var unauthorized = new FakeScmServer();
        unauthorized
            .Json("GET", "/meta", """{"verifiable_password_authentication":true}""")
            .On(
                "GET",
                "/user",
                new FakeResponse(HttpStatusCode.Unauthorized, $$"""{"message":"Bad credentials {{Secret}}"}""")
            );
        using (var client = Connect(unauthorized))
        {
            var test = await client.TestConnectionAsync(Token);
            test.Failure.Should().Be(ScmErrorKind.Unauthorized);
            test.Message.Should().Contain("Bad credentials").And.NotContain(Secret);
        }

        using var down = new FakeScmServer { Fail = _ => new HttpRequestException("Connection refused") };
        using (var client = Connect(down))
        {
            (await client.TestConnectionAsync(Token)).Failure.Should().Be(ScmErrorKind.Unreachable);
        }
    }

    [Fact]
    public async Task RateLimitsAreToldApartFromMissingPermissions()
    {
        var reset = DateTimeOffset
            .UtcNow.AddHours(1)
            .ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var exhausted = new FakeScmServer();
        exhausted.On(
            "GET",
            PullPath,
            new FakeResponse(
                HttpStatusCode.Forbidden,
                """{"message":"API rate limit exceeded"}""",
                Headers: new() { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = reset }
            )
        );
        using (var client = Connect(exhausted))
        {
            var limited = () => client.GetMergeRequestAsync(Project, 3, Token);
            (await limited.Should().ThrowAsync<ScmException>()).Which.Kind.Should().Be(ScmErrorKind.RateLimited);
        }

        // the reset is an hour away: nothing is retried
        exhausted.Requests.Should().ContainSingle();

        using var forbidden = new FakeScmServer();
        forbidden.On(
            "GET",
            PullPath,
            new FakeResponse(
                HttpStatusCode.Forbidden,
                """{"message":"Resource not accessible by personal access token"}""",
                Headers: new() { ["x-ratelimit-remaining"] = "4999" }
            )
        );
        using (var client = Connect(forbidden))
        {
            var denied = () => client.GetMergeRequestAsync(Project, 3, Token);
            (await denied.Should().ThrowAsync<ScmException>()).Which.Kind.Should().Be(ScmErrorKind.Forbidden);
        }

        using var secondary = new FakeScmServer();
        secondary
            .On(
                "GET",
                PullPath,
                new FakeResponse(
                    HttpStatusCode.Forbidden,
                    """{"message":"You have exceeded a secondary rate limit"}""",
                    Headers: new() { ["Retry-After"] = "0" }
                ),
                new FakeResponse(HttpStatusCode.OK, PullJson)
            )
            .Json("GET", PullPath + "/reviews", "[]");
        using (var client = Connect(secondary))
        {
            (await client.GetMergeRequestAsync(Project, 3, Token)).Number.Should().Be(3);
        }

        secondary.Requests.Count(request => request.Path == PullPath).Should().Be(2);
    }

    [Fact]
    public async Task ProjectsFollowTheLinkHeaderAndAreFilteredByVisibility()
    {
        static FakeScmServer Server()
        {
            var server = new FakeScmServer();
            server
                .On(
                    "GET",
                    "/user/repos",
                    new FakeResponse(
                        HttpStatusCode.OK,
                        """[{"id":1,"full_name":"owner/a","name":"a","private":true,"visibility":"private","html_url":"https://github.com/owner/a","default_branch":"main"},{"id":2,"full_name":"corp/b","name":"b","private":true,"visibility":"internal"}]""",
                        Headers: new()
                        {
                            ["Link"] =
                                """<https://api.github.com/user/repos?page=2>; rel="next", <https://api.github.com/user/repos?page=2>; rel="last" """,
                        }
                    ),
                    new FakeResponse(
                        HttpStatusCode.OK,
                        """[{"id":3,"full_name":"owner/c","name":"c","private":false,"description":"C"},{"id":4,"full_name":"me/d","name":"d","private":true}]""",
                        Headers: new()
                        {
                            ["Link"] =
                                """<https://api.github.com/user/repos?page=1>; rel="prev", <https://api.github.com/user/repos?page=1>; rel="first" """,
                        }
                    )
                )
                .Json(
                    "GET",
                    "/search/repositories",
                    """{"total_count":1,"incomplete_results":false,"items":[{"id":5,"full_name":"other/login-page","name":"login-page","private":false,"visibility":"public"}]}"""
                );
            return server;
        }

        using var personalServer = Server();
        using (var client = Connect(personalServer))
        {
            var personal = await client.ListProjectsAsync(null, ScmVisibility.Private, Token);
            personal.Select(project => project.Path).Should().Equal("owner/a", "me/d");
            personal[0].WebUrl!.AbsoluteUri.Should().Be("https://github.com/owner/a");
            personal[0].DefaultBranch.Should().Be("main");
        }

        // both pages were full; the second names no next page, so paging stops there
        personalServer.Requests.Select(request => request.Path).Should().Equal("/user/repos", "/user/repos");
        personalServer.Requests[0].Query.Should().Contain("per_page=2").And.Contain("page=1");
        personalServer.Requests[1].Query.Should().Contain("page=2");

        using var internalServer = Server();
        using (var client = Connect(internalServer))
        {
            (await client.ListProjectsAsync(null, ScmVisibility.Internal, Token))
                .Select(project => project.Path)
                .Should()
                .Equal("corp/b");
            (await client.ListProjectsAsync(null, ScmVisibility.Public, Token)).Single().Description.Should().Be("C");
        }

        using var searchServer = Server();
        using (var client = Connect(searchServer))
        {
            (await client.ListProjectsAsync("login page", ScmVisibility.Public, Token))
                .Single()
                .Path.Should()
                .Be("other/login-page");
        }

        searchServer.Requests.Should().NotContain(request => request.Path == "/user/repos");
        searchServer.Requests[0].Query.Should().Contain("q=login%20page%20in%3Aname");
    }

    [Fact]
    public async Task MergedAndClosedPullRequestsAreToldApartByMergedAt()
    {
        var merged = PullJson
            .Replace("\"merged_at\":null", "\"merged_at\":\"2026-11-03T08:00:00Z\"", StringComparison.Ordinal)
            .Replace("\"state\":\"open\"", "\"state\":\"closed\"", StringComparison.Ordinal);
        var closed = PullJson
            .Replace("\"state\":\"open\"", "\"state\":\"closed\"", StringComparison.Ordinal)
            .Replace("\"number\":3", "\"number\":4", StringComparison.Ordinal);
        using var server = new FakeScmServer();
        server.Json("GET", RepoPath + "/pulls", "[" + merged + "," + closed + "]");
        using var client = Connect(server);

        var mergedRequest = (await client.ListMergeRequestsAsync(Project, MergeRequestState.Merged, Token)).Single();
        mergedRequest.Number.Should().Be(3);
        mergedRequest
            .MergedAt.Should()
            .Be(DateTimeOffset.Parse("2026-11-03T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        (await client.ListMergeRequestsAsync(Project, MergeRequestState.Closed, Token)).Single().Number.Should().Be(4);
        (await client.ListMergeRequestsAsync(Project, MergeRequestState.Opened, Token)).Should().BeEmpty();
        server
            .Requests[0]
            .Query.Should()
            .Contain("state=closed")
            .And.Contain("sort=updated")
            .And.Contain("direction=desc");
        server.Requests[^1].Query.Should().Contain("state=open");
    }

    [Fact]
    public async Task DetailsCarryApprovalsFromEachUsersLatestReview()
    {
        using var server = new FakeScmServer();
        server.Json("GET", PullPath, PullJson).Json("GET", PullPath + "/reviews", ReviewsJson);
        using var client = Connect(server);

        var details = await client.GetMergeRequestAsync(Project, 3, Token);

        details.Id.Should().Be(501);
        details.Number.Should().Be(3);
        details.Title.Should().Be("Fix login");
        details.Description.Should().Be("Fixes #2");
        details.State.Should().Be(MergeRequestState.Opened);
        details.Author.Should().Be(new ScmUser("alice", "Alice"));
        (details.SourceBranch, details.TargetBranch).Should().Be(("fix/login", "main"));
        (details.HeadSha, details.BaseSha).Should().Be(("headsha", "basesha"));
        details.WebUrl!.AbsoluteUri.Should().Be("https://github.com/owner/repo/pull/3");
        // bob's approval came after his change request; carol's approval was dismissed; dave only commented
        details.Approved.Should().BeNull();
        details.ApprovedBy.Should().Equal(new ScmUser("bob"));
        details.Reviewers.Should().Equal(new ScmUser("bob"));
        details.Labels.Should().Equal("bug");
        details.MergeStatus.Should().Be("conflict");
        details.Draft.Should().BeTrue();
    }

    [Fact]
    public async Task CommentsJoinIssueCommentsReviewBodiesAndReviewCommentsOldestFirst()
    {
        using var server = new FakeScmServer();
        server
            .Json(
                "GET",
                RepoPath + "/issues/3/comments",
                """[{"id":91,"body":"Thanks","user":{"id":1,"login":"alice"},"created_at":"2026-11-01T09:00:00Z","html_url":"https://github.com/owner/repo/pull/3#issuecomment-91"}]"""
            )
            .Json("GET", PullPath + "/reviews", ReviewsJson)
            .Json(
                "GET",
                PullPath + "/comments",
                """
                [{"id":81,"body":"typo","user":{"id":2,"login":"bob"},"path":"src/login.go","line":2,"original_line":2,"side":"RIGHT","created_at":"2026-11-01T10:00:00Z","pull_request_review_id":71},
                 {"id":82,"body":"why removed?","user":{"id":2,"login":"bob"},"path":"src/login.go","line":2,"original_line":2,"side":"LEFT","created_at":"2026-11-01T10:01:00Z","pull_request_review_id":71},
                 {"id":83,"body":"fixed","user":{"id":1,"login":"alice"},"path":"src/login.go","line":null,"original_line":2,"side":"RIGHT","created_at":"2026-11-01T11:00:00Z","pull_request_review_id":76,"in_reply_to_id":81}]
                """
            );
        using var client = Connect(server);

        var comments = await client.ListCommentsAsync(Project, 3, Token);

        comments.Select(comment => comment.Id).Should().Equal("91", "review-71", "81", "82", "83");
        comments[0].Position.Should().BeNull();
        comments[0].WebUrl!.AbsoluteUri.Should().EndWith("#issuecomment-91");
        comments[1].Body.Should().Be("[CHANGES_REQUESTED] Please fix");
        comments[2].Position.Should().Be(new ScmDiffPosition("src/login.go", "src/login.go", 2, null));
        comments[3].Position.Should().Be(new ScmDiffPosition("src/login.go", "src/login.go", null, 2));
        // an outdated comment keeps the line it was made on
        comments[4].Position.Should().Be(new ScmDiffPosition("src/login.go", "src/login.go", 2, null));
        comments[2].ThreadId.Should().Be("81");
        comments[4].ThreadId.Should().Be("81");
        comments.Should().AllSatisfy(comment => comment.System.Should().BeFalse());
    }

    [Fact]
    public async Task GeneralCommentsArePostedOnTheIssue()
    {
        using var server = new FakeScmServer();
        server.Json(
            "POST",
            RepoPath + "/issues/3/comments",
            """{"id":92,"body":"Summary","user":{"id":1,"login":"alice"},"created_at":"2026-11-03T09:00:00Z"}"""
        );
        using var client = Connect(server);

        (await client.AddCommentAsync(Project, 3, "Summary", Token)).Id.Should().Be("92");
        server.Single("POST", RepoPath + "/issues/3/comments").Body.Should().Be("""{"body":"Summary"}""");
    }

    [Theory]
    [InlineData(2, DiffLineType.New, "src/login.go", 2, "RIGHT")]
    [InlineData(3, DiffLineType.New, "src/login.go", 3, "RIGHT")]
    [InlineData(2, DiffLineType.Old, "src/login.go", 2, "LEFT")]
    public async Task DiffCommentsAreReviewCommentsOnTheHeadCommitWithASide(
        int line,
        DiffLineType type,
        string path,
        int postedLine,
        string side
    )
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", PullPath, PullJson)
            .On("GET", PullPath + "|" + DiffType, Diff())
            .Json(
                "POST",
                PullPath + "/comments",
                $$"""{"id":85,"body":"Return the error","user":{"id":1,"login":"alice"},"path":"src/login.go","line":{{postedLine}},"side":"{{side}}","created_at":"2026-11-03T10:00:00Z"}"""
            );
        using var client = Connect(server);

        var comment = await client.AddDiffCommentAsync(
            Project,
            3,
            new ScmDiffComment("Return the error", "src/login.go", line, type),
            Token
        );

        comment.Id.Should().Be("85");
        comment.ThreadId.Should().Be("85");
        var body = JsonNode.Parse(server.Single("POST", PullPath + "/comments").Body)!;
        body["body"]!.GetValue<string>().Should().Be("Return the error");
        body["commit_id"]!.GetValue<string>().Should().Be("headsha");
        body["path"]!.GetValue<string>().Should().Be(path);
        body["line"]!.GetValue<int>().Should().Be(postedLine);
        body["side"]!.GetValue<string>().Should().Be(side);
    }

    [Fact]
    public async Task DiffCommentsOutsideTheDiffAreRefusedBeforePosting()
    {
        using var server = new FakeScmServer();
        server.Json("GET", PullPath, PullJson).On("GET", PullPath + "|" + DiffType, Diff());
        using var client = Connect(server);

        var outside = () =>
            client.AddDiffCommentAsync(Project, 3, new ScmDiffComment("x", "src/login.go", 9, DiffLineType.New), Token);
        (await outside.Should().ThrowAsync<ScmException>()).Which.Kind.Should().Be(ScmErrorKind.Invalid);
        var other = () =>
            client.AddDiffCommentAsync(Project, 3, new ScmDiffComment("x", "main.go", 1, DiffLineType.New), Token);
        (await other.Should().ThrowAsync<ScmException>()).Which.Message.Should().Contain("not changed");
        server.Requests.Should().NotContain(request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task DiffsAreThePullRequestInTheDiffMediaTypeSplitPerFileWithinTheBudget()
    {
        using var server = new FakeScmServer();
        server.On("GET", PullPath + "|" + DiffType, Diff("\r\n"));
        using var client = Connect(server);

        var whole = await client.GetDiffAsync(Project, 3, 10_000, Token);
        whole.Truncated.Should().BeFalse();
        whole.Files.Select(file => file.NewPath).Should().Equal("src/login.go", "docs/b.md");
        whole.Files[0].Diff.Should().StartWith("@@ -1,3 +1,3 @@\n package auth\n");
        whole.Files[1].RenamedFile.Should().BeTrue();
        whole.Files[1].OldPath.Should().Be("docs/a.md");
        server.Requests[0].Headers["Accept"].Should().Be(DiffType);

        var cut = await client.GetDiffAsync(Project, 3, 30, Token);
        cut.Truncated.Should().BeTrue();
        cut.Files[0].Truncated.Should().BeTrue();
        cut.Files[0].Diff.Should().Be("@@ -1,3 +1,3 @@\n package auth\n");
    }

    [Fact]
    public async Task TitleAndDescriptionArePatchedAndApprovalIsAnApproveReview()
    {
        using var server = new FakeScmServer();
        server
            .Json("PATCH", PullPath, PullJson.Replace("Fix login", "Fix the login", StringComparison.Ordinal))
            .Json("GET", PullPath, PullJson)
            .Json("POST", PullPath + "/reviews", """{"id":76,"user":{"id":1,"login":"alice"},"state":"APPROVED"}""")
            .Json(
                "GET",
                PullPath + "/reviews",
                """[{"id":76,"user":{"id":1,"login":"alice"},"state":"APPROVED","submitted_at":"2026-11-03T11:00:00Z"}]"""
            );
        using var client = Connect(server);

        (await client.SetTitleAsync(Project, 3, "Fix the login", Token)).Title.Should().Be("Fix the login");
        await client.SetDescriptionAsync(Project, 3, "New body", Token);
        var approval = await client.ApproveAsync(Project, 3, Token);

        server
            .Requests.Where(request => request.Method == HttpMethod.Patch)
            .Select(request => request.Body)
            .Should()
            .Equal("""{"title":"Fix the login"}""", """{"body":"New body"}""");
        server.Single("POST", PullPath + "/reviews").Body.Should().Be("""{"event":"APPROVE","commit_id":"headsha"}""");
        approval.Approved.Should().BeNull();
        approval.ApprovedBy.Should().Equal(new ScmUser("alice"));
    }

    [Fact]
    public async Task ProjectsMustBeOwnerAndRepositoryAndHttpErrorsKeepTheirKind()
    {
        using var server = new FakeScmServer();
        server.On(
            "GET",
            PullPath,
            new FakeResponse(
                HttpStatusCode.NotFound,
                """{"message":"Not Found","documentation_url":"https://docs.github.com"}"""
            )
        );
        using var client = Connect(server);

        var invalid = () => client.GetMergeRequestAsync("group/sub/repo", 3, Token);
        (await invalid.Should().ThrowAsync<ScmException>()).Which.Kind.Should().Be(ScmErrorKind.Invalid);
        var missing = () => client.GetMergeRequestAsync(Project, 3, Token);
        var failure = (await missing.Should().ThrowAsync<ScmException>()).Which;
        failure.Kind.Should().Be(ScmErrorKind.NotFound);
        failure.Message.Should().Contain("Not Found");
        FluentActions
            .Invoking(() =>
                new GithubProvider().Connect(
                    new ScmConnection(new ScmEndpoint("g", ScmKind.Gitlab, new Uri("https://gitlab.com")), Secret)
                )
            )
            .Should()
            .Throw<ArgumentException>();
    }
}
