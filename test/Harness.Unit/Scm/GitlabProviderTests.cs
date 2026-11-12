using System.Net;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Scm;
using Harness.Scm.Gitlab;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class GitlabProviderTests
{
    private const string Secret = "glpat-GitlabSecret01234567";
    private const string Project = "group/sub/repo";
    private const string Api = "/gitlab/api/v4";
    private const string ProjectPath = Api + "/projects/group%2Fsub%2Frepo";
    private const string RequestPath = ProjectPath + "/merge_requests/7";

    private const string RequestJson = """
        {"id":101,"iid":7,"title":"Add parser","description":"Adds the parser","state":"opened",
         "author":{"id":1,"username":"alice","name":"Alice"},"source_branch":"feature/parser","target_branch":"main",
         "sha":"head1","diff_refs":{"base_sha":"base1","head_sha":"head1","start_sha":"start1"},
         "web_url":"https://gl.example.com/gitlab/group/sub/repo/-/merge_requests/7","draft":false,
         "work_in_progress":false,"created_at":"2026-11-01T10:00:00Z","updated_at":"2026-11-02T10:00:00Z",
         "merged_at":null,"labels":["backend"],"reviewers":[{"id":2,"username":"bob","name":"Bob"}],
         "detailed_merge_status":"mergeable"}
        """;

    private const string DiffsJson = """
        [{"old_path":"src/parser.cs","new_path":"src/parser.cs","new_file":false,"renamed_file":false,
          "deleted_file":false,"diff":"@@ -1,3 +1,4 @@\n line one\n-line two\n+line 2\n+line 2b\n line three\n"},
         {"old_path":"old.txt","new_path":"new.txt","new_file":false,"renamed_file":true,"deleted_file":false,
          "diff":""}]
        """;

    private static readonly ScmLimits Limits = new(TimeSpan.FromSeconds(5), 3, 2, TimeSpan.FromSeconds(1));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IScmClient Connect(FakeScmServer server, ScmLimits? limits = null)
    {
        ScmEndpoint.TryNormalizeUrl("https://gl.example.com/gitlab", out var url, out _);
        return new GitlabProvider(server, limits ?? Limits).Connect(
            new ScmConnection(new ScmEndpoint("work", ScmKind.Gitlab, url), Secret)
        );
    }

    [Fact]
    public async Task ConnectionTestsReadTheVersionAndUserWithThePrivateTokenHeader()
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", Api + "/version", """{"version":"17.5.1-ee","revision":"abc"}""")
            .Json("GET", Api + "/user", """{"id":1,"username":"alice","name":"Alice"}""");
        using var client = Connect(server);

        var test = await client.TestConnectionAsync(Token);

        test.Ok.Should().BeTrue();
        test.Version.Should().Be("17.5.1-ee");
        test.User.Should().Be(new ScmUser("alice", "Alice"));
        server.Requests.Should().AllSatisfy(request => request.Headers["PRIVATE-TOKEN"].Should().Be(Secret));
        server.Requests.Select(request => request.Path).Should().Equal(Api + "/version", Api + "/user");
    }

    [Fact]
    public async Task ConnectionFailuresAreCategorisedWithoutTheToken()
    {
        using var unauthorized = new FakeScmServer();
        unauthorized.On(
            "GET",
            Api + "/version",
            new FakeResponse(HttpStatusCode.Unauthorized, $$"""{"message":"401 Unauthorized: token {{Secret}}"}""")
        );
        using (var client = Connect(unauthorized))
        {
            var test = await client.TestConnectionAsync(Token);
            test.Failure.Should().Be(ScmErrorKind.Unauthorized);
            test.Message.Should().Contain("401").And.NotContain(Secret);
            test.Hint.Should().Contain("token");
        }

        using var forbidden = new FakeScmServer();

        forbidden
            .Json("GET", Api + "/version", """{"version":"17.0.0"}""")
            .On("GET", Api + "/user", new FakeResponse(HttpStatusCode.Forbidden, """{"message":"403 Forbidden"}"""));
        using (var client = Connect(forbidden))
        {
            (await client.TestConnectionAsync(Token)).Failure.Should().Be(ScmErrorKind.Forbidden);
        }

        using var empty = new FakeScmServer();
        using (var client = Connect(empty))
        {
            var missing = await client.TestConnectionAsync(Token);
            missing.Failure.Should().Be(ScmErrorKind.WrongServer);
            missing.Message.Should().Contain("No GitLab API v4");
        }

        using var html = new FakeScmServer();

        html.On("GET", Api + "/version", new FakeResponse(HttpStatusCode.OK, "<html>sign in</html>", "text/html"));
        using (var client = Connect(html))
        {
            var wrong = await client.TestConnectionAsync(Token);
            wrong.Failure.Should().Be(ScmErrorKind.WrongServer);
            wrong.Message.Should().Contain("text/html");
        }

        using var tls = new FakeScmServer
        {
            Fail = _ => new HttpRequestException(
                HttpRequestError.SecureConnectionError,
                "The SSL connection could not be established",
                new AuthenticationException("untrusted root")
            ),
        };
        using (var client = Connect(tls))
        {
            (await client.TestConnectionAsync(Token)).Failure.Should().Be(ScmErrorKind.Tls);
        }

        using var down = new FakeScmServer
        {
            Fail = _ => new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."),
        };
        using (var client = Connect(down))
        {
            (await client.TestConnectionAsync(Token)).Failure.Should().Be(ScmErrorKind.Unreachable);
        }
    }

    [Fact]
    public async Task ProjectsFollowPagesUpToTheLimitAndFilterByMembershipVisibilityAndSearch()
    {
        using var server = new FakeScmServer();
        server.On(
            "GET",
            Api + "/projects",
            new FakeResponse(
                HttpStatusCode.OK,
                """
                [{"id":1,"path_with_namespace":"group/a","name":"a","visibility":"private","web_url":"https://gl.example.com/gitlab/group/a","default_branch":"main"},
                 {"id":2,"path_with_namespace":"group/b","name":"b","visibility":"internal"}]
                """,
                Headers: new() { ["X-Next-Page"] = "2" }
            ),
            new FakeResponse(
                HttpStatusCode.OK,
                """[{"id":3,"path_with_namespace":"group/sub/c","name":"c","visibility":"public","description":"C"}]""",
                Headers: new() { ["X-Next-Page"] = "" }
            )
        );
        using var client = Connect(server);

        var projects = await client.ListProjectsAsync("my repo", ScmVisibility.Internal, Token);

        projects.Select(project => project.Path).Should().Equal("group/a", "group/b", "group/sub/c");
        projects[0].WebUrl!.AbsoluteUri.Should().Be("https://gl.example.com/gitlab/group/a");
        projects[1].Visibility.Should().Be(ScmVisibility.Internal);
        projects[2].Description.Should().Be("C");
        server.Requests.Should().HaveCount(2);
        server
            .Requests[0]
            .Query.Should()
            .Contain("membership=true")
            .And.Contain("visibility=internal")
            .And.Contain("search=my%20repo")
            .And.Contain("per_page=2")
            .And.Contain("page=1");
        server.Requests[1].Query.Should().Contain("page=2");
    }

    [Fact]
    public async Task PagingStopsAtTheConfiguredPageCount()
    {
        var full = """[{"id":1,"path_with_namespace":"g/a"},{"id":2,"path_with_namespace":"g/b"}]""";
        using var server = new FakeScmServer();
        var page = new FakeResponse(HttpStatusCode.OK, full, Headers: new() { ["X-Next-Page"] = "9" });
        server.On("GET", Api + "/projects", page, page, page, page);
        using var client = Connect(server);

        (await client.ListProjectsAsync(null, ScmVisibility.Private, Token)).Should().HaveCount(6);
        server.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task MergeRequestsAreListedByStateAndReadByIidOnTheEncodedProjectPath()
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", ProjectPath + "/merge_requests", "[" + RequestJson + "]")
            .Json("GET", RequestPath, RequestJson)
            .Json(
                "GET",
                RequestPath + "/approvals",
                """{"approved":true,"approved_by":[{"user":{"id":2,"username":"bob","name":"Bob"}}]}"""
            );
        using var client = Connect(server);

        var list = await client.ListMergeRequestsAsync(Project, MergeRequestState.Merged, Token);
        list.Should().ContainSingle().Which.Approved.Should().BeNull();
        server.Requests[0].Query.Should().Contain("state=merged");
        server.Requests[0].Uri.AbsoluteUri.Should().Contain("projects/group%2Fsub%2Frepo/merge_requests");

        var details = await client.GetMergeRequestAsync(Project, 7, Token);
        details.Id.Should().Be(101);
        details.Number.Should().Be(7);
        details.Title.Should().Be("Add parser");
        details.State.Should().Be(MergeRequestState.Opened);
        details.Author.Should().Be(new ScmUser("alice", "Alice"));
        (details.SourceBranch, details.TargetBranch).Should().Be(("feature/parser", "main"));
        (details.HeadSha, details.BaseSha, details.StartSha).Should().Be(("head1", "base1", "start1"));
        details.WebUrl!.AbsoluteUri.Should().EndWith("/-/merge_requests/7");
        details.Approved.Should().BeTrue();
        details.ApprovedBy.Should().Equal(new ScmUser("bob", "Bob"));
        details.Reviewers.Should().Equal(new ScmUser("bob", "Bob"));
        details.Labels.Should().Equal("backend");
        details.MergeStatus.Should().Be("mergeable");
    }

    [Fact]
    public async Task ApprovalsTheTokenCannotReadLeaveTheApprovalStateUnknown()
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", RequestPath, RequestJson)
            .On("GET", RequestPath + "/approvals", new FakeResponse(HttpStatusCode.Forbidden, "{}"));
        using var client = Connect(server);

        (await client.GetMergeRequestAsync(Project, 7, Token)).Approved.Should().BeNull();
    }

    [Fact]
    public async Task CommentsFlattenDiscussionsOldestFirstWithSystemNotesAndDiffPositions()
    {
        using var server = new FakeScmServer();
        server.Json(
            "GET",
            RequestPath + "/discussions",
            """
            [{"id":"d1","individual_note":true,"notes":[{"id":11,"body":"Looks good","author":{"id":2,"username":"bob"},"created_at":"2026-11-01T11:00:00Z","system":false}]},
             {"id":"d2","individual_note":false,"notes":[{"id":12,"body":"Rename this","author":{"id":2,"username":"bob"},"created_at":"2026-11-01T12:00:00Z","system":false,"type":"DiffNote","position":{"new_path":"src/parser.cs","old_path":"src/parser.cs","new_line":2,"old_line":null}}]},
             {"id":"d3","individual_note":true,"notes":[{"id":13,"body":"added 1 commit","created_at":"2026-11-01T09:00:00Z","system":true}]}]
            """,
            new() { ["X-Next-Page"] = "" }
        );
        using var client = Connect(server);

        var comments = await client.ListCommentsAsync(Project, 7, Token);

        comments.Select(comment => comment.Id).Should().Equal("13", "11", "12");
        comments[0].System.Should().BeTrue();
        comments[1].Position.Should().BeNull();
        comments[1].ThreadId.Should().BeNull();
        comments[2].ThreadId.Should().Be("d2");
        comments[2].Position.Should().Be(new ScmDiffPosition("src/parser.cs", "src/parser.cs", 2, null));
    }

    [Fact]
    public async Task GeneralCommentsArePostedAsNotes()
    {
        using var server = new FakeScmServer();
        server.Json(
            "POST",
            RequestPath + "/notes",
            """{"id":21,"body":"Summary: LGTM","author":{"id":1,"username":"alice"},"created_at":"2026-11-03T10:00:00Z","system":false}"""
        );
        using var client = Connect(server);

        var comment = await client.AddCommentAsync(Project, 7, "Summary: LGTM", Token);

        comment.Id.Should().Be("21");
        JsonNode.Parse(server.Single("POST", RequestPath + "/notes").Body)!["body"]!
            .GetValue<string>()
            .Should()
            .Be("Summary: LGTM");
    }

    [Theory]
    [InlineData(2, DiffLineType.New, null, 2)]
    [InlineData(4, DiffLineType.New, 3, 4)]
    [InlineData(2, DiffLineType.Old, 2, null)]
    [InlineData(1, DiffLineType.Old, 1, 1)]
    public async Task DiffCommentsStartADiscussionPositionedOnTheMergeRequestsDiffRefs(
        int line,
        DiffLineType type,
        int? oldLine,
        int? newLine
    )
    {
        using var server = new FakeScmServer();
        server
            .Json("GET", RequestPath, RequestJson)
            .Json("GET", RequestPath + "/diffs", DiffsJson)
            .Json(
                "POST",
                RequestPath + "/discussions",
                """{"id":"d9","individual_note":false,"notes":[{"id":31,"body":"Rename","system":false,"position":{"new_path":"src/parser.cs","old_path":"src/parser.cs","new_line":2}}]}"""
            );
        using var client = Connect(server);

        var comment = await client.AddDiffCommentAsync(
            Project,
            7,
            new ScmDiffComment("Rename", "src/parser.cs", line, type),
            Token
        );

        comment.Id.Should().Be("31");
        comment.ThreadId.Should().Be("d9");
        var body = JsonNode.Parse(server.Single("POST", RequestPath + "/discussions").Body)!;
        body["body"]!.GetValue<string>().Should().Be("Rename");
        var position = body["position"]!;
        position["position_type"]!.GetValue<string>().Should().Be("text");
        position["base_sha"]!.GetValue<string>().Should().Be("base1");
        position["start_sha"]!.GetValue<string>().Should().Be("start1");
        position["head_sha"]!.GetValue<string>().Should().Be("head1");
        position["old_path"]!.GetValue<string>().Should().Be("src/parser.cs");
        position["new_path"]!.GetValue<string>().Should().Be("src/parser.cs");
        position["old_line"]?.GetValue<int>().Should().Be(oldLine);
        position["new_line"]?.GetValue<int>().Should().Be(newLine);
        if (oldLine is null)
        {
            position.AsObject().Should().NotContainKey("old_line");
        }
    }

    [Fact]
    public async Task DiffCommentsOutsideTheDiffAreRefusedBeforePosting()
    {
        using var server = new FakeScmServer();
        server.Json("GET", RequestPath, RequestJson).Json("GET", RequestPath + "/diffs", DiffsJson);
        using var client = Connect(server);

        var outside = () =>
            client.AddDiffCommentAsync(
                Project,
                7,
                new ScmDiffComment("x", "src/parser.cs", 40, DiffLineType.New),
                Token
            );
        (await outside.Should().ThrowAsync<ScmException>()).Which.Kind.Should().Be(ScmErrorKind.Invalid);

        var unchanged = () =>
            client.AddDiffCommentAsync(Project, 7, new ScmDiffComment("x", "README.md", 1, DiffLineType.New), Token);
        (await unchanged.Should().ThrowAsync<ScmException>()).Which.Message.Should().Contain("not changed");

        server.Requests.Should().NotContain(request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task DiffsAreBoundedPerFileAndFallBackToChangesOnOlderServers()
    {
        using var server = new FakeScmServer();
        server.Json(
            "GET",
            RequestPath + "/changes",
            """
            {"changes":[
              {"old_path":"a.txt","new_path":"a.txt","diff":"@@ -1 +1 @@\n-a\n+b\n"},
              {"old_path":"big.txt","new_path":"big.txt","diff":"@@ -1,3 +1,3 @@\n-1\n-2\n+3\n+4\n"},
              {"old_path":"huge.bin","new_path":"huge.bin","diff":"","too_large":true},
              {"old_path":"c.txt","new_path":"c.txt","new_file":true,"diff":"@@ -0,0 +1 @@\n+c\n"}],
             "overflow":false}
            """
        );
        using var client = Connect(server);

        var diff = await client.GetDiffAsync(Project, 7, 40, Token);

        server
            .Requests.Select(request => request.Path)
            .Should()
            .Equal(RequestPath + "/diffs", RequestPath + "/changes");
        diff.Truncated.Should().BeTrue();
        diff.Files.Select(file => file.NewPath).Should().Equal("a.txt", "big.txt", "huge.bin", "c.txt");
        diff.Files[0].Truncated.Should().BeFalse();
        diff.Files[1].Truncated.Should().BeTrue();
        diff.Files[1].Diff.Should().StartWith("@@ -1,3 +1,3 @@\n").And.NotContain("+4");
        diff.Files[2].Truncated.Should().BeTrue();
        diff.Files[3].Diff.Should().BeEmpty();
        diff.Files[3].NewFile.Should().BeTrue();
        diff.OmittedFiles.Should().Be(1);
    }

    [Fact]
    public async Task TitleDescriptionAndApprovalAreWritten()
    {
        using var server = new FakeScmServer();
        server
            .Json("PUT", RequestPath, RequestJson.Replace("Add parser", "New title", StringComparison.Ordinal))
            .Json(
                "POST",
                RequestPath + "/approve",
                """{"approved":false,"approved_by":[{"user":{"id":1,"username":"alice"}}]}"""
            );
        using var client = Connect(server);

        (await client.SetTitleAsync(Project, 7, "New title", Token)).Title.Should().Be("New title");
        await client.SetDescriptionAsync(Project, 7, "Body", Token);
        var approval = await client.ApproveAsync(Project, 7, Token);

        var puts = server
            .Requests.Where(request => request.Method == HttpMethod.Put)
            .Select(request => request.Body)
            .ToList();
        puts.Should().Equal("""{"title":"New title"}""", """{"description":"Body"}""");
        server.Single("POST", RequestPath + "/approve").Body.Should().Be("{}");
        approval.Approved.Should().BeFalse();
        approval.ApprovedBy.Should().Equal(new ScmUser("alice"));
    }

    [Fact]
    public async Task RateLimitsAreRetriedOnceWithinTheBoundedWait()
    {
        var limited = new FakeResponse(
            HttpStatusCode.TooManyRequests,
            """{"message":"Retry later"}""",
            Headers: new() { ["Retry-After"] = "0" }
        );
        using var server = new FakeScmServer();
        server.On("GET", RequestPath, limited, new FakeResponse(HttpStatusCode.OK, RequestJson), limited, limited);
        server.On("GET", RequestPath + "/approvals", new FakeResponse(HttpStatusCode.NotFound, "{}"));
        using var client = Connect(server);

        (await client.GetMergeRequestAsync(Project, 7, Token)).Number.Should().Be(7);

        var again = () => client.GetMergeRequestAsync(Project, 7, Token);
        (await again.Should().ThrowAsync<ScmException>()).Which.Kind.Should().Be(ScmErrorKind.RateLimited);

        using var slow = new FakeScmServer();

        slow.On(
            "GET",
            RequestPath,
            limited with
            {
                Headers = new() { ["Retry-After"] = "120" },
            },
            new FakeResponse(HttpStatusCode.OK, RequestJson)
        );
        using var patient = Connect(slow);
        var waitTooLong = () => patient.GetMergeRequestAsync(Project, 7, Token);
        (await waitTooLong.Should().ThrowAsync<ScmException>()).Which.Kind.Should().Be(ScmErrorKind.RateLimited);
        slow.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task SlowServersTimeOutAndCallersCanCancel()
    {
        using var server = new FakeScmServer();
        server.On(
            "GET",
            RequestPath,
            new FakeResponse(HttpStatusCode.OK, RequestJson, Delay: TimeSpan.FromSeconds(30))
        );
        using var client = Connect(server, Limits with { Timeout = TimeSpan.FromMilliseconds(100) });

        var timeout = () => client.GetMergeRequestAsync(Project, 7, Token);
        (await timeout.Should().ThrowAsync<ScmException>()).Which.Kind.Should().Be(ScmErrorKind.Unreachable);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var patient = Connect(server);
        var cancelled = () => patient.GetMergeRequestAsync(Project, 7, cancel.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ServerErrorsNameTheCallAndStatusButNeverTheToken()
    {
        using var server = new FakeScmServer();
        server.On(
            "GET",
            RequestPath,
            new FakeResponse(HttpStatusCode.NotFound, $$"""{"message":"404 Project Not Found for {{Secret}}"}""")
        );
        using var client = Connect(server);

        var missing = () => client.GetMergeRequestAsync(Project, 7, Token);
        var failure = (await missing.Should().ThrowAsync<ScmException>()).Which;
        failure.Kind.Should().Be(ScmErrorKind.NotFound);
        failure
            .Message.Should()
            .Contain("GET")
            .And.Contain("merge_requests/7")
            .And.Contain("404")
            .And.NotContain(Secret);
    }

    [Fact]
    public void OnlyGitlabConnectionsAreAccepted()
    {
        ScmEndpoint.TryNormalizeUrl("https://git.example.org", out var url, out _);
        var forgejo = new ScmConnection(new ScmEndpoint("f", ScmKind.Forgejo, url), Secret);

        FluentActions.Invoking(() => new GitlabProvider().Connect(forgejo)).Should().Throw<ArgumentException>();
        new GitlabProvider().Kind.Should().Be(ScmKind.Gitlab);
    }
}
