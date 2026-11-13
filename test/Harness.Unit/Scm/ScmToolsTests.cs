using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Projects;
using Harness.Infrastructure.Scm;
using Harness.Mcp;
using Harness.Scm;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class ScmToolsTests : IDisposable
{
    private const string Secret = "glpat-tools-secret-0123456789";
    private static readonly HarnessRuntime Runtime = new("test", "commit", ".NET test", "windows/amd64");
    private readonly string _directory = Directory.CreateTempSubdirectory("harness-scm-tools-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, true);

    private static readonly ScmUser Alice = new("alice", "Alice");

    private static readonly MergeRequest Request = new(
        101,
        7,
        "Add parser",
        "Adds the parser",
        MergeRequestState.Opened,
        Alice,
        "feature/parser",
        "main",
        "head1",
        "base1",
        "start1",
        new Uri("https://gitlab.example.com/group/repo/-/merge_requests/7"),
        false,
        DateTimeOffset.Parse("2026-11-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-11-02T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        null,
        true,
        [new("bob")],
        [new("bob")],
        ["backend"],
        "mergeable"
    );

    private async Task<FakeSession> SessionAsync(ScmResolution? resolution = null)
    {
        var store = new ScmConfigurationFile(Path.Combine(_directory, "scm.toml"));
        if (store.Find("work") is null)
        {
            await store.AddAsync(new("work", "gitlab", "https://gitlab.example.com", Secret, null), Token);
        }

        var work = store.Find("work")!.Endpoint;
        return new FakeSession(
            store,
            resolution
                ?? new ScmResolution(
                    ScmResolutionStatus.Matched,
                    new ScmMatch(
                        work,
                        "group/repo",
                        new ScmRemoteUrl("ssh", "gitlab.example.com", null, ["group", "repo"])
                    ),
                    "origin",
                    [],
                    false,
                    ""
                )
        );
    }

    private static async Task<(bool Error, JsonElement Json, string Text)> CallAsync(
        FakeSession session,
        string tool,
        string arguments = "{}"
    )
    {
        var result = await HarnessMcpServer.CallAsync(
            new CallToolRequestParams
            {
                Name = tool,
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments),
            },
            session,
            Runtime,
            Token
        );
        var text = ((TextContentBlock)result.Content[0]).Text;
        text.Should().NotContain(Secret);
        return (result.IsError is true, result.StructuredContent ?? default, text);
    }

    [Fact]
    public async Task ProjectsAreListedOnTheProjectsScmWithTheChosenVisibilityAndSearch()
    {
        await using var session = await SessionAsync();

        var (error, json, text) = await CallAsync(
            session,
            "scm_get_projects",
            """{"search":"repo","visibility":"internal"}"""
        );

        error.Should().BeFalse(text);
        session.Client.Calls.Should().Equal("projects repo internal");
        json.GetProperty("scm").GetProperty("name").GetString().Should().Be("work");
        json.GetProperty("scm").GetProperty("type").GetString().Should().Be("gitlab");
        json.GetProperty("count").GetInt32().Should().Be(1);
        json.GetProperty("projects")[0].GetProperty("path").GetString().Should().Be("group/repo");
        session.Client.Connection.Token.Should().Be(Secret);
    }

    [Fact]
    public async Task ProjectsCanBeListedOnANamedScmOrTheOnlyOneBeforeTheProjectMatches()
    {
        await using var session = await SessionAsync(
            new ScmResolution(ScmResolutionStatus.NoMatch, null, null, [], false, "No remote matches")
        );

        (await CallAsync(session, "scm_get_projects")).Error.Should().BeFalse("the only SCM is used");
        session.Client.Calls.Should().Equal("projects  private");
        (await CallAsync(session, "scm_get_projects", """{"scm":"work"}""")).Error.Should().BeFalse();
        var missing = await CallAsync(session, "scm_get_projects", """{"scm":"nope"}""");
        missing.Error.Should().BeTrue();
        missing.Text.Should().Contain("No SCM is named 'nope'").And.Contain("harness doctor");
    }

    [Fact]
    public async Task MergeRequestsAreListedByStateAndReadByNumber()
    {
        await using var session = await SessionAsync();

        var list = await CallAsync(session, "scm_list_merge_requests", """{"state":"merged"}""");
        list.Error.Should().BeFalse(list.Text);
        list.Json.GetProperty("project").GetString().Should().Be("group/repo");
        list.Json.GetProperty("state").GetString().Should().Be("merged");
        var summary = list.Json.GetProperty("merge_requests")[0];
        summary.GetProperty("id").GetInt64().Should().Be(7);
        summary.GetProperty("author").GetString().Should().Be("alice");
        (await CallAsync(session, "scm_list_merge_requests"))
            .Json.GetProperty("state")
            .GetString()
            .Should()
            .Be("opened");

        var details = await CallAsync(session, "scm_get_merge_request_details", """{"merge_request_id":7}""");
        var request = details.Json.GetProperty("merge_request");
        request.GetProperty("global_id").GetInt64().Should().Be(101);
        request.GetProperty("description").GetString().Should().Be("Adds the parser");
        request.GetProperty("head_sha").GetString().Should().Be("head1");
        request.GetProperty("approved").GetBoolean().Should().BeTrue();
        request.GetProperty("approved_by")[0].GetString().Should().Be("bob");
        session.Client.Calls.Should().Equal("list group/repo Merged", "list group/repo Opened", "get group/repo 7");
    }

    [Fact]
    public async Task CommentsAreReadAndWrittenGeneralOrOnADiffLine()
    {
        await using var session = await SessionAsync();

        var comments = await CallAsync(session, "scm_get_merge_request_comments", """{"merge_request_id":7}""");
        comments.Json.GetProperty("count").GetInt32().Should().Be(2);
        comments.Json.GetProperty("comments")[0].GetProperty("kind").GetString().Should().Be("general");
        var inline = comments.Json.GetProperty("comments")[1];
        inline.GetProperty("kind").GetString().Should().Be("diff");
        inline.GetProperty("position").GetProperty("file_path").GetString().Should().Be("src/a.cs");
        inline.GetProperty("position").GetProperty("new_line").GetInt32().Should().Be(3);

        var general = await CallAsync(
            session,
            "scm_add_merge_request_comment",
            """{"merge_request_id":7,"comment":"LGTM overall"}"""
        );
        general.Json.GetProperty("comment").GetProperty("body").GetString().Should().Be("LGTM overall");

        var onLine = await CallAsync(
            session,
            "scm_add_merge_request_diff_comment",
            """{"merge_request_id":7,"comment":"Rename","file_path":"src/a.cs","line_number":3,"line_type":"old"}"""
        );
        onLine.Error.Should().BeFalse(onLine.Text);
        onLine.Json.GetProperty("comment").GetProperty("kind").GetString().Should().Be("diff");
        await CallAsync(
            session,
            "scm_add_merge_request_diff_comment",
            """{"merge_request_id":7,"comment":"x","file_path":"b","line_number":1}"""
        );

        session
            .Client.Calls.Should()
            .Equal(
                "comments group/repo 7",
                "comment group/repo 7 LGTM overall",
                "diff-comment group/repo 7 src/a.cs 3 Old Rename",
                "diff-comment group/repo 7 b 1 New x"
            );
    }

    [Fact]
    public async Task DiffsAreBoundedAndWritesEchoWhatChanged()
    {
        await using var session = await SessionAsync();

        var diff = await CallAsync(session, "scm_get_merge_request_diff", """{"merge_request_id":7}""");
        diff.Json.GetProperty("max_bytes").GetInt32().Should().Be(Harness.Mcp.Scm.ScmTools.DiffBudget);
        diff.Json.GetProperty("truncated").GetBoolean().Should().BeTrue();
        diff.Json.GetProperty("omitted_files").GetInt32().Should().Be(1);
        diff.Json.GetProperty("hint").GetString().Should().Contain("working tree");
        diff.Json.GetProperty("files")[1].GetProperty("truncated").GetBoolean().Should().BeTrue();

        var title = await CallAsync(
            session,
            "scm_set_merge_request_title",
            """{"merge_request_id":7,"title":"Parser"}"""
        );
        title.Json.GetProperty("merge_request").GetProperty("title").GetString().Should().Be("Parser");
        var description = await CallAsync(
            session,
            "scm_set_merge_request_description",
            """{"merge_request_id":7,"description":""}"""
        );
        description.Error.Should().BeFalse("an empty description clears it");
        var approval = await CallAsync(session, "scm_approve_merge_request", """{"merge_request_id":7}""");
        approval.Json.GetProperty("approved").GetBoolean().Should().BeTrue();
        approval.Json.GetProperty("approved_by")[0].GetString().Should().Be("alice");

        session
            .Client.Calls.Should()
            .Equal(
                $"diff group/repo 7 {Harness.Mcp.Scm.ScmTools.DiffBudget}",
                "title group/repo 7 Parser",
                "description group/repo 7 ",
                "approve group/repo 7"
            );
    }

    [Fact]
    public async Task MergeRequestsAreCreatedWithAssigneesReviewersAndWarnings()
    {
        await using var session = await SessionAsync();

        var created = await CallAsync(
            session,
            "scm_create_merge_request",
            """
            {"from":" feature/parser ","to":"main","title":"Add parser","description":"Adds it",
             "assignees":["@alice"],"reviewers":["bob","carol"]}
            """
        );

        created.Error.Should().BeFalse(created.Text);
        session
            .Client.Calls.Should()
            .Equal("create group/repo feature/parser>main Add parser [Adds it] [alice] [bob,carol]");
        created.Json.GetProperty("project").GetString().Should().Be("group/repo");
        created.Json.GetProperty("merge_request_id").GetInt64().Should().Be(8);
        var request = created.Json.GetProperty("merge_request");
        request.GetProperty("id").GetInt64().Should().Be(8);
        request.GetProperty("source_branch").GetString().Should().Be("feature/parser");
        request
            .GetProperty("web_url")
            .GetString()
            .Should()
            .Be("https://gitlab.example.com/group/repo/-/merge_requests/8");
        request.TryGetProperty("merge_status", out _).Should().BeTrue("the same shape as the details tool");
        created.Json.GetProperty("warnings")[0].GetString().Should().Contain("carol");

        var plain = await CallAsync(
            session,
            "scm_create_merge_request",
            """{"from":"fix","to":"main","title":"Fix"}"""
        );
        plain.Error.Should().BeFalse(plain.Text);
        plain.Json.GetProperty("warnings").GetArrayLength().Should().Be(0);
        session.Client.Calls[^1].Should().Be("create group/repo fix>main Fix [] [] []");
    }

    [Theory]
    [InlineData("scm_create_merge_request", """{"from":"a","title":"x"}""", "to")]
    [InlineData("scm_create_merge_request", """{"from":"a","to":"b","title":" "}""", "title must not be empty")]
    [InlineData(
        "scm_create_merge_request",
        """{"from":"main","to":"main","title":"x"}""",
        "invalid: The source and target"
    )]
    [InlineData("scm_create_merge_request", """{"from":"a","to":"b","title":"x","assignees":"bob"}""", "assignees")]
    [InlineData("scm_get_merge_request_details", "{}", "merge_request_id")]
    [InlineData("scm_get_merge_request_details", """{"merge_request_id":"7"}""", "merge_request_id")]
    [InlineData("scm_get_merge_request_details", """{"merge_request_id":0}""", "positive")]
    [InlineData("scm_list_merge_requests", """{"state":"draft"}""", "opened, merged or closed")]
    [InlineData("scm_get_projects", """{"visibility":"secret"}""", "private, internal or public")]
    [InlineData(
        "scm_add_merge_request_diff_comment",
        """{"merge_request_id":7,"comment":"x","file_path":"a","line_number":1,"line_type":"both"}""",
        "new or old"
    )]
    [InlineData(
        "scm_add_merge_request_comment",
        """{"merge_request_id":7,"comment":"  "}""",
        "comment must not be empty"
    )]
    [InlineData("scm_set_merge_request_title", """{"merge_request_id":7,"title":"x","extra":1}""", "extra")]
    public async Task InvalidArgumentsAreToolErrorsBeforeAnyCall(string tool, string arguments, string message)
    {
        await using var session = await SessionAsync();

        var (error, _, text) = await CallAsync(session, tool, arguments);

        error.Should().BeTrue();
        text.Should().Contain(message);
        session.Client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAnScmOrAMatchTheToolsSayWhatToDo()
    {
        await using var none = new FakeSession(
            new ScmConfigurationFile(Path.Combine(_directory, "empty.toml")),
            new ScmResolution(ScmResolutionStatus.NoScm, null, null, [], false, "No SCM is configured")
        );
        var noScm = await CallAsync(none, "scm_list_merge_requests");
        noScm.Error.Should().BeTrue();
        noScm.Text.Should().Contain("harness scm add").And.Contain("Machine › SCM connections");
        (await CallAsync(none, "scm_get_projects")).Text.Should().Contain("harness scm add");

        await using var unmatched = await SessionAsync(
            new ScmResolution(
                ScmResolutionStatus.NoMatch,
                null,
                null,
                [],
                false,
                "No remote (origin x) matches a configured SCM."
            )
        );
        var noMatch = await CallAsync(unmatched, "scm_get_merge_request_details", """{"merge_request_id":7}""");
        noMatch.Error.Should().BeTrue();
        noMatch.Text.Should().Contain("No remote (origin x)").And.Contain("harness doctor").And.Contain("[scm]");
    }

    [Theory]
    [InlineData(ScmErrorKind.Unauthorized, "unauthorized", "harness scm set")]
    [InlineData(ScmErrorKind.Forbidden, "forbidden", "scope")]
    [InlineData(ScmErrorKind.NotFound, "not_found", "project path")]
    [InlineData(ScmErrorKind.Invalid, "invalid", "diff")]
    [InlineData(ScmErrorKind.RateLimited, "rate_limited", "later")]
    public async Task ProviderFailuresBecomeToolErrorsNamingTheKindAndFix(ScmErrorKind kind, string name, string fix)
    {
        await using var session = await SessionAsync();
        session.Client.Fail = new ScmException(kind, "HTTP failure from the server");

        var (error, _, text) = await CallAsync(
            session,
            "scm_add_merge_request_diff_comment",
            """{"merge_request_id":7,"comment":"x","file_path":"a","line_number":99}"""
        );

        error.Should().BeTrue();
        text.Should().StartWith(name + ": HTTP failure").And.Contain("Fix: ").And.Contain(fix);
    }

    [Fact]
    public async Task SessionsWithoutProvidersSaySoAndElevenScmToolsAreAdvertised()
    {
        await using var session = await SessionAsync();
        session.WithoutProviders = true;
        (await CallAsync(session, "scm_list_merge_requests")).Text.Should().Contain("unavailable");

        var schemas = typeof(HarnessMcpServer)
            .Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Harness.Mcp.Schemas.scm_", StringComparison.Ordinal))
            .ToList();
        schemas.Should().HaveCount(11);
        foreach (var resource in schemas)
        {
            using var stream = typeof(HarnessMcpServer).Assembly.GetManifestResourceStream(resource)!;
            using var schema = await JsonDocument.ParseAsync(stream, cancellationToken: Token);
            schema.RootElement.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
            schema
                .RootElement.GetProperty("inputSchema")
                .GetProperty("additionalProperties")
                .GetBoolean()
                .Should()
                .BeFalse();
            schema
                .RootElement.GetProperty("outputSchema")
                .GetProperty("properties")
                .TryGetProperty("scm", out _)
                .Should()
                .BeTrue();
        }
    }

    private sealed class FakeSession(IScmConfiguration store, ScmResolution resolution)
        : IHarnessStateSession,
            IScmSession
    {
        internal FakeClient Client { get; } = new();

        internal bool WithoutProviders { get; set; }

        public IScmConfiguration ScmConfiguration => store;

        public ScmProviderRegistry? ScmProviders => WithoutProviders ? null : new([new FakeProvider(Client)]);

        public Task<ScmResolution> ResolveScmAsync(CancellationToken cancellationToken) => Task.FromResult(resolution);

        public Task<IReadOnlyDictionary<string, object?>> DiagnosticsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task PutAsync(string key, string value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProvider(FakeClient client) : IScmProvider
    {
        public ScmKind Kind => ScmKind.Gitlab;

        public IScmClient Connect(ScmConnection connection)
        {
            client.Connection = connection;
            return client;
        }
    }

    /// <summary>Records each call as text and answers with canned values, or throws <see cref="Fail" />.</summary>
    private sealed class FakeClient : IScmClient
    {
        public List<string> Calls { get; } = [];

        public ScmException? Fail { get; set; }

        public ScmConnection Connection { get; set; } = null!;

        private T Record<T>(string call, T value)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            Calls.Add(call);
            return value;
        }

        private static readonly ScmComment General = new("1", "Looks fine", Alice, null, false, null, null, null);

        private static readonly ScmComment Inline = new(
            "2",
            "Rename",
            Alice,
            null,
            false,
            new ScmDiffPosition("src/a.cs", "src/a.cs", 3, null),
            null,
            "d1"
        );

        public Task<ScmConnectionTest> TestConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ScmConnectionTest.Success("17", Alice));

        public Task<IReadOnlyList<ScmProject>> ListProjectsAsync(
            string? search,
            ScmVisibility visibility,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Record<IReadOnlyList<ScmProject>>(
                    $"projects {search} {ScmNames.Name(visibility)}",
                    [new("5", "group/repo", "repo", null, visibility, null, "main")]
                )
            );

        public Task<ScmProject> GetProjectAsync(string project, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MergeRequest>> ListMergeRequestsAsync(
            string project,
            MergeRequestState state,
            CancellationToken cancellationToken
        ) => Task.FromResult(Record<IReadOnlyList<MergeRequest>>($"list {project} {state}", [Request]));

        public Task<MergeRequest> GetMergeRequestAsync(
            string project,
            long number,
            CancellationToken cancellationToken
        ) => Task.FromResult(Record($"get {project} {number}", Request));

        public Task<IReadOnlyList<ScmComment>> ListCommentsAsync(
            string project,
            long number,
            CancellationToken cancellationToken
        ) => Task.FromResult(Record<IReadOnlyList<ScmComment>>($"comments {project} {number}", [General, Inline]));

        public Task<ScmComment> AddCommentAsync(
            string project,
            long number,
            string body,
            CancellationToken cancellationToken
        ) => Task.FromResult(Record($"comment {project} {number} {body}", General with { Body = body }));

        public Task<ScmComment> AddDiffCommentAsync(
            string project,
            long number,
            ScmDiffComment comment,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Record(
                    $"diff-comment {project} {number} {comment.FilePath} {comment.Line} {comment.LineType} {comment.Body}",
                    Inline with
                    {
                        Body = comment.Body,
                    }
                )
            );

        public Task<ScmMergeRequestDiff> GetDiffAsync(
            string project,
            long number,
            int maxBytes,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Record(
                    $"diff {project} {number} {maxBytes}",
                    new ScmMergeRequestDiff(
                        [
                            new("src/a.cs", "src/a.cs", false, false, false, false, "@@ -1 +1 @@\n-a\n+b\n", false),
                            new("big.bin", "big.bin", false, false, false, true, "", true),
                        ],
                        true,
                        1
                    )
                )
            );

        public Task<MergeRequest> SetTitleAsync(
            string project,
            long number,
            string title,
            CancellationToken cancellationToken
        ) => Task.FromResult(Record($"title {project} {number} {title}", Request with { Title = title }));

        public Task<MergeRequest> SetDescriptionAsync(
            string project,
            long number,
            string description,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Record($"description {project} {number} {description}", Request with { Description = description })
            );

        public Task<ScmApproval> ApproveAsync(string project, long number, CancellationToken cancellationToken) =>
            Task.FromResult(Record($"approve {project} {number}", new ScmApproval(true, [Alice])));

        public Task<ScmCreatedMergeRequest> CreateMergeRequestAsync(
            string project,
            ScmNewMergeRequest request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Record(
                    $"create {project} {request.SourceBranch}>{request.TargetBranch} {request.Title} "
                        + $"[{request.Description}] [{string.Join(",", request.Assignees)}] "
                        + $"[{string.Join(",", request.Reviewers)}]",
                    new ScmCreatedMergeRequest(
                        Request with
                        {
                            Number = 8,
                            Title = request.Title,
                            SourceBranch = request.SourceBranch,
                            TargetBranch = request.TargetBranch,
                            WebUrl = new Uri("https://gitlab.example.com/group/repo/-/merge_requests/8"),
                        },
                        request.Reviewers.Contains("carol")
                            ? ["Could not add carol as reviewer: carol is not a project member."]
                            : []
                    )
                )
            );

        public void Dispose()
        {
            // the fake is shared by the session and outlives each call
        }
    }
}
