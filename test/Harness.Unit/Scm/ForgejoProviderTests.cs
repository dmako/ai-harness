using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Scm;
using Harness.Scm.Forgejo;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class ForgejoProviderTests
{
    private const string Secret = "fj-ForgejoSecret0123456789";
    private const string Project = "owner/repo";
    private const string Api = "/api/v1";
    private const string RepoPath = Api + "/repos/owner/repo";
    private const string PullPath = RepoPath + "/pulls/3";

    private const string PullJson = """
        {"id":501,"number":3,"title":"Fix login","body":"Fixes #2","state":"open",
         "user":{"id":1,"login":"alice","full_name":"Alice"},"head":{"ref":"fix/login","sha":"headsha"},
         "base":{"ref":"main","sha":"basesha"},"merge_base":"mergebase",
         "html_url":"https://git.example.org/owner/repo/pulls/3","draft":false,
         "created_at":"2026-11-01T08:00:00Z","updated_at":"2026-11-02T08:00:00Z","merged_at":null,"merged":false,
         "labels":[{"name":"bug"}],"requested_reviewers":[{"id":2,"login":"bob","full_name":""}],"mergeable":true}
        """;

    private const string ReviewsJson = """
        [{"id":71,"user":{"id":2,"login":"bob"},"state":"REQUEST_CHANGES","body":"Please fix","submitted_at":"2026-11-01T10:00:00Z","comments_count":1},
         {"id":72,"user":{"id":2,"login":"bob"},"state":"APPROVED","body":"","submitted_at":"2026-11-02T10:00:00Z","comments_count":0},
         {"id":73,"user":{"id":3,"login":"carol"},"state":"APPROVED","body":"","submitted_at":"2026-11-02T11:00:00Z","stale":true,"comments_count":0}]
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

    private static IScmClient Connect(FakeScmServer server)
    {
        ScmEndpoint.TryNormalizeUrl("https://git.example.org", out var url, out _);
        return new ForgejoProvider(server, Limits).Connect(
            new ScmConnection(new ScmEndpoint("forge", ScmKind.Forgejo, url), Secret)
        );
    }

    [Fact]
    public async Task ConnectionTestsReadTheVersionAndUserWithATokenAuthorizationHeader()
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", Api + "/version", """{"version":"9.0.1+gitea-1.22.0"}""")
            .Json("GET", Api + "/user", """{"id":1,"login":"alice","full_name":"Alice"}""");
        using var client = Connect(server);

        var test = await client.TestConnectionAsync(Token);

        test.Ok.Should().BeTrue();
        test.Version.Should().Be("9.0.1+gitea-1.22.0");
        test.User.Should().Be(new ScmUser("alice", "Alice"));
        server.Requests.Should().AllSatisfy(request => request.Headers["Authorization"].Should().Be("token " + Secret));
    }

    [Fact]
    public async Task ConnectionFailuresAreCategorised()
    {
        using var gitlab = new FakeScmServer();
        using (var client = Connect(gitlab))
        {
            var wrong = await client.TestConnectionAsync(Token);
            wrong.Failure.Should().Be(ScmErrorKind.WrongServer);
            wrong.Message.Should().Contain("No Forgejo API v1");
        }

        using var unauthorized = new FakeScmServer();
        unauthorized
            .Json("GET", Api + "/version", """{"version":"9.0.1"}""")
            .On(
                "GET",
                Api + "/user",
                new FakeResponse(HttpStatusCode.Unauthorized, $$"""{"message":"token {{Secret}} is invalid"}""")
            );
        using (var client = Connect(unauthorized))
        {
            var test = await client.TestConnectionAsync(Token);
            test.Failure.Should().Be(ScmErrorKind.Unauthorized);
            test.Message.Should().NotContain(Secret);
        }

        using var down = new FakeScmServer { Fail = _ => new HttpRequestException("Connection refused") };
        using (var client = Connect(down))
        {
            (await client.TestConnectionAsync(Token)).Failure.Should().Be(ScmErrorKind.Unreachable);
        }
    }

    [Fact]
    public async Task ProjectsCombineSearchAndTheUsersRepositoriesFilteredByVisibility()
    {
        static FakeScmServer Server()
        {
            var server = new FakeScmServer();
            server
                .On(
                    "GET",
                    Api + "/repos/search",
                    new FakeResponse(
                        HttpStatusCode.OK,
                        """{"ok":true,"data":[{"id":1,"full_name":"owner/a","name":"a","private":true,"html_url":"https://git.example.org/owner/a","default_branch":"main"},{"id":2,"full_name":"owner/b","name":"b","private":false,"internal":true}]}"""
                    ),
                    new FakeResponse(
                        HttpStatusCode.OK,
                        """{"ok":true,"data":[{"id":3,"full_name":"owner/c","name":"c","private":false,"description":"C"}]}"""
                    )
                )
                .Json("GET", Api + "/user/repos", """[{"id":4,"full_name":"me/d","name":"d","private":true}]""");
            return server;
        }

        using var personalServer = Server();
        using (var client = Connect(personalServer))
        {
            var personal = await client.ListProjectsAsync(null, ScmVisibility.Private, Token);
            personal.Select(project => project.Path).Should().Equal("owner/a", "me/d");
            personal[0].WebUrl!.AbsoluteUri.Should().Be("https://git.example.org/owner/a");
        }

        personalServer
            .Requests.Select(request => request.Path)
            .Should()
            .Equal(Api + "/repos/search", Api + "/repos/search", Api + "/user/repos");
        personalServer.Requests[0].Query.Should().Contain("private=true").And.Contain("limit=2").And.Contain("page=1");
        personalServer.Requests[1].Query.Should().Contain("page=2");

        using var internalServer = Server();
        using (var client = Connect(internalServer))
        {
            (await client.ListProjectsAsync(null, ScmVisibility.Internal, Token))
                .Select(project => project.Path)
                .Should()
                .Equal("owner/b");
        }

        using var searchServer = Server();
        using (var client = Connect(searchServer))
        {
            (await client.ListProjectsAsync("login page", ScmVisibility.Public, Token))
                .Single()
                .Description.Should()
                .Be("C");
        }

        searchServer.Requests.Should().NotContain(request => request.Path == Api + "/user/repos");
        searchServer.Requests[0].Query.Should().Contain("q=login%20page");
    }

    [Fact]
    public async Task MergedAndClosedPullRequestsAreToldApartByTheMergedFlag()
    {
        var merged = PullJson
            .Replace("\"merged\":false", "\"merged\":true", StringComparison.Ordinal)
            .Replace("\"state\":\"open\"", "\"state\":\"closed\"", StringComparison.Ordinal);
        var closed = PullJson
            .Replace("\"state\":\"open\"", "\"state\":\"closed\"", StringComparison.Ordinal)
            .Replace("\"number\":3", "\"number\":4", StringComparison.Ordinal);
        using var server = new FakeScmServer();
        server.Json("GET", RepoPath + "/pulls", "[" + merged + "," + closed + "]");
        using var client = Connect(server);

        (await client.ListMergeRequestsAsync(Project, MergeRequestState.Merged, Token)).Single().Number.Should().Be(3);
        (await client.ListMergeRequestsAsync(Project, MergeRequestState.Closed, Token)).Single().Number.Should().Be(4);
        (await client.ListMergeRequestsAsync(Project, MergeRequestState.Opened, Token)).Should().BeEmpty();
        server.Requests.Select(request => request.Query).Should().AllSatisfy(query => query.Should().Contain("state="));
        server.Requests[0].Query.Should().Contain("state=closed");
        server.Requests[^1].Query.Should().Contain("state=open");
    }

    [Fact]
    public async Task DetailsCarryApprovalsFromTheLatestNonStaleReviews()
    {
        using var server = new FakeScmServer();
        server.Json("GET", PullPath, PullJson).Json("GET", PullPath + "/reviews", ReviewsJson);
        using var client = Connect(server);

        var details = await client.GetMergeRequestAsync(Project, 3, Token);

        details.Id.Should().Be(501);
        details.Number.Should().Be(3);
        details.State.Should().Be(MergeRequestState.Opened);
        (details.SourceBranch, details.TargetBranch).Should().Be(("fix/login", "main"));
        (details.HeadSha, details.BaseSha).Should().Be(("headsha", "mergebase"));
        details.Approved.Should().BeNull();
        details.ApprovedBy.Should().Equal(new ScmUser("bob"));
        details.Reviewers.Should().Equal(new ScmUser("bob"));
        details.Labels.Should().Equal("bug");
        details.MergeStatus.Should().Be("mergeable");
        details.Draft.Should().BeFalse();
    }

    [Fact]
    public async Task CommentsJoinIssueCommentsReviewBodiesAndInlineReviewComments()
    {
        using var server = new FakeScmServer();
        server
            .Json(
                "GET",
                RepoPath + "/issues/3/comments",
                """[{"id":91,"body":"Thanks","user":{"id":1,"login":"alice"},"created_at":"2026-11-01T09:00:00Z","html_url":"https://git.example.org/owner/repo/pulls/3#issuecomment-91"}]"""
            )
            .Json("GET", PullPath + "/reviews", ReviewsJson)
            .Json(
                "GET",
                PullPath + "/reviews/71/comments",
                """
                [{"id":81,"body":"typo","user":{"id":2,"login":"bob"},"path":"src/login.go","position":2,"original_position":0,"created_at":"2026-11-01T10:00:00Z","pull_request_review_id":71},
                 {"id":82,"body":"why removed?","user":{"id":2,"login":"bob"},"path":"src/login.go","position":0,"original_position":2,"created_at":"2026-11-01T10:01:00Z","pull_request_review_id":71}]
                """
            );
        using var client = Connect(server);

        var comments = await client.ListCommentsAsync(Project, 3, Token);

        comments.Select(comment => comment.Id).Should().Equal("91", "review-71", "81", "82");
        comments[0].Position.Should().BeNull();
        comments[1].Body.Should().Be("[REQUEST_CHANGES] Please fix");
        comments[2].Position.Should().Be(new ScmDiffPosition("src/login.go", "src/login.go", 2, null));
        comments[3].Position.Should().Be(new ScmDiffPosition("src/login.go", "src/login.go", null, 2));
        comments[2].ThreadId.Should().Be("71");
        server
            .Requests.Should()
            .NotContain(request => request.Path.EndsWith("/reviews/72/comments", StringComparison.Ordinal));
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
    [InlineData(2, DiffLineType.New, "src/login.go", 2L, null)]
    [InlineData(3, DiffLineType.New, "src/login.go", 3L, null)]
    [InlineData(2, DiffLineType.Old, "src/login.go", null, 2L)]
    public async Task DiffCommentsAreCommentReviewsOnTheHeadCommit(
        int line,
        DiffLineType type,
        string path,
        long? newPosition,
        long? oldPosition
    )
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", PullPath, PullJson)
            .On(
                "GET",
                PullPath + ".diff",
                new FakeResponse(HttpStatusCode.OK, Patch.ReplaceLineEndings("\n"), "text/plain")
            )
            .Json(
                "POST",
                PullPath + "/reviews",
                """{"id":75,"user":{"id":1,"login":"alice"},"state":"COMMENT","comments_count":1}"""
            )
            .Json(
                "GET",
                PullPath + "/reviews/75/comments",
                """[{"id":85,"body":"Return the error","user":{"id":1,"login":"alice"},"path":"src/login.go","position":2,"created_at":"2026-11-03T10:00:00Z","pull_request_review_id":75}]"""
            );
        using var client = Connect(server);

        var comment = await client.AddDiffCommentAsync(
            Project,
            3,
            new ScmDiffComment("Return the error", "src/login.go", line, type),
            Token
        );

        comment.Id.Should().Be("85");
        comment.ThreadId.Should().Be("75");
        var body = JsonNode.Parse(server.Single("POST", PullPath + "/reviews").Body)!;
        body["event"]!.GetValue<string>().Should().Be("COMMENT");
        body["commit_id"]!.GetValue<string>().Should().Be("headsha");
        var inline = body["comments"]![0]!;
        inline["path"]!.GetValue<string>().Should().Be(path);
        inline["body"]!.GetValue<string>().Should().Be("Return the error");
        inline["new_position"]?.GetValue<long>().Should().Be(newPosition);
        inline["old_position"]?.GetValue<long>().Should().Be(oldPosition);
    }

    [Fact]
    public async Task DiffCommentsOutsideTheDiffAreRefusedBeforePosting()
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", PullPath, PullJson)
            .On(
                "GET",
                PullPath + ".diff",
                new FakeResponse(HttpStatusCode.OK, Patch.ReplaceLineEndings("\n"), "text/plain")
            );
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
    public async Task DiffsAreTheWholePatchSplitPerFileWithinTheBudget()
    {
        using var server = new FakeScmServer();
        server.On(
            "GET",
            PullPath + ".diff",
            new FakeResponse(HttpStatusCode.OK, Patch.ReplaceLineEndings("\r\n"), "text/plain")
        );
        using var client = Connect(server);

        var whole = await client.GetDiffAsync(Project, 3, 10_000, Token);
        whole.Truncated.Should().BeFalse();
        whole.Files.Select(file => file.NewPath).Should().Equal("src/login.go", "docs/b.md");
        whole.Files[0].Diff.Should().StartWith("@@ -1,3 +1,3 @@\n package auth\n");
        whole.Files[1].RenamedFile.Should().BeTrue();
        whole.Files[1].OldPath.Should().Be("docs/a.md");

        var cut = await client.GetDiffAsync(Project, 3, 30, Token);
        cut.Truncated.Should().BeTrue();
        cut.Files[0].Truncated.Should().BeTrue();
        cut.Files[0].Diff.Should().Be("@@ -1,3 +1,3 @@\n package auth\n");
    }

    [Fact]
    public async Task TitleAndDescriptionArePatchedAndApprovalIsAnApprovedReview()
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
        server
            .Single("POST", PullPath + "/reviews")
            .Body.Should()
            .Be("""{"event":"APPROVED","body":"","commit_id":"headsha"}""");
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
                HttpStatusCode.Forbidden,
                """{"message":"token does not have at least one of required scope(s): [read:repository]"}"""
            )
        );
        using var client = Connect(server);

        var invalid = () => client.GetMergeRequestAsync("group/sub/repo", 3, Token);
        (await invalid.Should().ThrowAsync<ScmException>()).Which.Kind.Should().Be(ScmErrorKind.Invalid);
        var forbidden = () => client.GetMergeRequestAsync(Project, 3, Token);
        var failure = (await forbidden.Should().ThrowAsync<ScmException>()).Which;
        failure.Kind.Should().Be(ScmErrorKind.Forbidden);
        failure.Message.Should().Contain("read:repository");
        FluentActions
            .Invoking(() =>
                new ForgejoProvider().Connect(
                    new ScmConnection(new ScmEndpoint("g", ScmKind.Gitlab, new Uri("https://gitlab.com")), Secret)
                )
            )
            .Should()
            .Throw<ArgumentException>();
    }

    private static ScmNewMergeRequest NewRequest(
        IReadOnlyList<string>? assignees = null,
        IReadOnlyList<string>? reviewers = null
    ) => new("fix/login", "main", "Fix login", "Fixes #2", assignees ?? [], reviewers ?? []);

    [Fact]
    public async Task PullRequestsAreCreatedWithAssigneesThenReviewersAreRequested()
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", Api + "/users/alice", """{"id":1,"login":"alice"}""")
            .Json("GET", Api + "/users/bob", """{"id":2,"login":"bob"}""")
            .On(
                "POST",
                RepoPath + "/pulls",
                new FakeResponse(
                    HttpStatusCode.Created,
                    PullJson.Replace(
                        "\"mergeable\":true",
                        "\"mergeable\":true,\"assignees\":[{\"id\":1,\"login\":\"alice\"}]",
                        StringComparison.Ordinal
                    )
                )
            )
            .On("POST", PullPath + "/requested_reviewers", new FakeResponse(HttpStatusCode.Created, "[]"))
            .Json("GET", PullPath, PullJson);
        using var client = Connect(server);

        var created = await client.CreateMergeRequestAsync(Project, NewRequest(["alice"], ["bob"]), Token);

        created.Warnings.Should().BeEmpty();
        created.MergeRequest.Number.Should().Be(3);
        created.MergeRequest.WebUrl!.AbsoluteUri.Should().Be("https://git.example.org/owner/repo/pulls/3");
        created.MergeRequest.Reviewers.Should().Equal(new ScmUser("bob"));
        var body = JsonNode.Parse(server.Single("POST", RepoPath + "/pulls").Body)!;
        body["head"]!.GetValue<string>().Should().Be("fix/login");
        body["base"]!.GetValue<string>().Should().Be("main");
        body["title"]!.GetValue<string>().Should().Be("Fix login");
        body["body"]!.GetValue<string>().Should().Be("Fixes #2");
        body["assignees"]!.AsArray().Select(name => name!.GetValue<string>()).Should().Equal("alice");
        server.Single("POST", PullPath + "/requested_reviewers").Body.Should().Be("""{"reviewers":["bob"]}""");
    }

    [Fact]
    public async Task RefusedAssigneesAndReviewersBecomeWarningsOnTheCreatedPullRequest()
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", Api + "/users/alice", """{"id":1,"login":"alice"}""")
            .Json("GET", Api + "/users/bob", """{"id":2,"login":"bob"}""")
            .On(
                "POST",
                RepoPath + "/pulls",
                new FakeResponse(
                    HttpStatusCode.UnprocessableEntity,
                    $$"""{"message":"user alice cannot be assigned {{Secret}}"}"""
                ),
                new FakeResponse(HttpStatusCode.Created, PullJson)
            )
            .Json("GET", RepoPath + "/pulls", "[]")
            .Json("GET", RepoPath + "/branches/fix/login", """{"name":"fix/login"}""")
            .Json("GET", RepoPath + "/branches/main", """{"name":"main"}""")
            .On(
                "POST",
                PullPath + "/requested_reviewers",
                new FakeResponse(
                    HttpStatusCode.UnprocessableEntity,
                    """{"message":"reviewer bob is not a collaborator"}"""
                )
            );
        using var client = Connect(server);

        var created = await client.CreateMergeRequestAsync(Project, NewRequest(["alice"], ["bob"]), Token);

        created.MergeRequest.Number.Should().Be(3);
        created.Warnings.Should().HaveCount(2);
        created.Warnings[0].Should().Contain("alice").And.Contain("assignee").And.Contain("cannot be assigned");
        created.Warnings[1].Should().Contain("bob").And.Contain("reviewer").And.Contain("not a collaborator");
        created.Warnings.Should().AllSatisfy(warning => warning.Should().NotContain(Secret));
        var creates = server
            .Requests.Where(request => request.Method == HttpMethod.Post && request.Path == RepoPath + "/pulls")
            .Select(request => JsonNode.Parse(request.Body)!.AsObject())
            .ToList();
        creates.Should().HaveCount(2);
        creates[1].ContainsKey("assignees").Should().BeFalse("the retry leaves the refused assignees out");
    }

    [Fact]
    public async Task UnknownUsersDuplicatesAndMissingBranchesAreRefused()
    {
        using var unknown = new FakeScmServer();
        unknown.Json("GET", Api + "/users/alice", """{"id":1,"login":"alice"}""");
        using (var client = Connect(unknown))
        {
            var create = () => client.CreateMergeRequestAsync(Project, NewRequest(["alice"], ["ghost"]), Token);
            var failure = (await create.Should().ThrowAsync<ScmException>()).Which;
            failure.Kind.Should().Be(ScmErrorKind.Invalid);
            failure.Message.Should().Contain("'ghost'");
            unknown.Requests.Should().NotContain(request => request.Method == HttpMethod.Post);
        }

        using var duplicate = new FakeScmServer();
        duplicate
            .On(
                "POST",
                RepoPath + "/pulls",
                new FakeResponse(
                    HttpStatusCode.Conflict,
                    """{"message":"pull request already exists for these targets"}"""
                )
            )
            .Json("GET", RepoPath + "/pulls", "[" + PullJson + "]");
        using (var client = Connect(duplicate))
        {
            var create = () => client.CreateMergeRequestAsync(Project, NewRequest(), Token);
            var failure = (await create.Should().ThrowAsync<ScmException>()).Which;
            failure.Kind.Should().Be(ScmErrorKind.Invalid);
            failure.Message.Should().Contain("#3").And.Contain("https://git.example.org/owner/repo/pulls/3");
        }

        using var missing = new FakeScmServer();
        missing
            .On("POST", RepoPath + "/pulls", new FakeResponse(HttpStatusCode.NotFound, """{"message":"not found"}"""))
            .Json("GET", RepoPath + "/pulls", "[]")
            .Json("GET", RepoPath + "/branches/fix/login", """{"name":"fix/login"}""");
        using (var client = Connect(missing))
        {
            var create = () => client.CreateMergeRequestAsync(Project, NewRequest(), Token);
            var failure = (await create.Should().ThrowAsync<ScmException>()).Which;
            failure.Kind.Should().Be(ScmErrorKind.Invalid);
            failure.Message.Should().Contain("target branch 'main' does not exist");
        }
    }
}
