using System.Text.Json;
using AwesomeAssertions;
using Harness.Infrastructure.Scm;
using Harness.Infrastructure.Storage;
using Harness.Integration.Providers;
using Harness.Integration.Web;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Integration.Scm;

/// <summary>
/// The scm_ tools through harness mcp as a client starts it: the project's SCM is found from its origin remote, and
/// merge requests are listed, read, diffed and commented on against a GitLab stand-in over HTTP.
/// </summary>
public sealed class ScmMcpProcessTests
{
    private const string Good = "glpat-mcp-process-0123456789";
    private const string Request = "/api/v4/projects/group%2Frepo/merge_requests/7";

    private const string RequestJson = """
        {"id":101,"iid":7,"title":"Add parser","description":"Adds the parser","state":"opened",
         "author":{"id":1,"username":"alice"},"source_branch":"feature/parser","target_branch":"main","sha":"head1",
         "diff_refs":{"base_sha":"base1","head_sha":"head1","start_sha":"start1"},"draft":false}
        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AgentsListReadDiffAndCommentOnTheProjectsMergeRequests()
    {
        await using var gitlab = new FakeGitlabServer(
            Good,
            new Dictionary<string, string>
            {
                ["GET /api/v4/projects/group%2Frepo/merge_requests"] = "[" + RequestJson + "]",
                ["GET " + Request] = RequestJson,
                ["GET " + Request + "/approvals"] = """{"approved":false,"approved_by":[]}""",
                ["GET " + Request + "/diffs"] =
                    """[{"old_path":"src/parser.cs","new_path":"src/parser.cs","diff":"@@ -1,2 +1,3 @@\n line one\n+line two\n line three\n"}]""",
                ["POST " + Request + "/discussions"] =
                    """{"id":"d1","individual_note":false,"notes":[{"id":31,"body":"Name it","system":false,"position":{"new_path":"src/parser.cs","old_path":"src/parser.cs","new_line":2}}]}""",
            }
        );
        var root = Path.Combine(Path.GetTempPath(), "harness-scm-mcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var git = Path.Combine(root, ".git");
            Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
            Directory.CreateDirectory(Path.Combine(git, "objects"));
            await File.WriteAllTextAsync(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n", Token);
            await File.WriteAllTextAsync(
                Path.Combine(git, "config"),
                $"[remote \"origin\"]\n\turl = {gitlab.Url}/group/repo.git\n",
                Token
            );
            using (HarnessDatabase.Open(Path.Combine(root, ".harness", "harness.db"), cancellationToken: Token))
            {
                // an initialized project, as clients find one
            }

            await new ScmConfigurationFile(Path.Combine(root, "config", "scm.toml")).AddAsync(
                new("work", "gitlab", gitlab.Url, Good, null),
                Token
            );
            await using var client = await StartAsync(root);

            var tools = await client.ListToolsAsync(cancellationToken: Token);
            tools.Should().HaveCount(42);
            var scm = tools.Where(tool => tool.Name.StartsWith("scm_", StringComparison.Ordinal)).ToList();
            scm.Select(tool => tool.Name)
                .Should()
                .BeEquivalentTo(
                    "scm_get_projects",
                    "scm_list_merge_requests",
                    "scm_get_merge_request_details",
                    "scm_get_merge_request_comments",
                    "scm_add_merge_request_comment",
                    "scm_add_merge_request_diff_comment",
                    "scm_get_merge_request_diff",
                    "scm_set_merge_request_title",
                    "scm_set_merge_request_description",
                    "scm_approve_merge_request"
                );
            scm.ForEach(ToolSchema.AssertMatches);

            var list = await CallAsync(client, "scm_list_merge_requests", new { });
            list.GetProperty("scm").GetProperty("name").GetString().Should().Be("work");
            list.GetProperty("project").GetString().Should().Be("group/repo");
            list.GetProperty("merge_requests")[0].GetProperty("id").GetInt64().Should().Be(7);

            var details = await CallAsync(client, "scm_get_merge_request_details", new { merge_request_id = 7 });
            details.GetProperty("merge_request").GetProperty("title").GetString().Should().Be("Add parser");
            details.GetProperty("merge_request").GetProperty("approved").GetBoolean().Should().BeFalse();

            var diff = await CallAsync(client, "scm_get_merge_request_diff", new { merge_request_id = 7 });
            diff.GetProperty("files")[0].GetProperty("new_path").GetString().Should().Be("src/parser.cs");
            diff.GetProperty("truncated").GetBoolean().Should().BeFalse();

            var comment = await CallAsync(
                client,
                "scm_add_merge_request_diff_comment",
                new
                {
                    merge_request_id = 7,
                    comment = "Name it",
                    file_path = "src/parser.cs",
                    line_number = 2,
                }
            );
            comment.GetProperty("comment").GetProperty("id").GetString().Should().Be("31");
            var posted = gitlab.Requests.Single(request => request.Method is "POST");
            using (var body = JsonDocument.Parse(posted.Body))
            {
                var position = body.RootElement.GetProperty("position");
                position.GetProperty("new_line").GetInt32().Should().Be(2);
                position.GetProperty("head_sha").GetString().Should().Be("head1");
                position.TryGetProperty("old_line", out _).Should().BeFalse("an added line has no old number");
            }

            gitlab.Requests.Should().AllSatisfy(request => request.Token.Should().Be(Good));

            var outside = await client.CallToolAsync(
                "scm_add_merge_request_diff_comment",
                new Dictionary<string, object?>
                {
                    ["merge_request_id"] = 7,
                    ["comment"] = "x",
                    ["file_path"] = "src/parser.cs",
                    ["line_number"] = 40,
                },
                cancellationToken: Token
            );
            outside.IsError.Should().BeTrue();
            var text = ((TextContentBlock)outside.Content[0]).Text;
            text.Should().StartWith("invalid:").And.Contain("Fix:").And.NotContain(Good);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string name, object args)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args));
        var result = await client.CallToolAsync(name, values, cancellationToken: Token);
        result.IsError.Should().NotBe(true, ((TextContentBlock)result.Content[0]).Text);
        result.StructuredContent.Should().NotBeNull();
        return result.StructuredContent.Value;
    }

    private static Task<McpClient> StartAsync(string root)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        var binary = Path.Combine(
            directory.FullName,
            "src",
            "Harness.Cli",
            "bin",
            Configuration,
            TestDirectory.TargetFramework,
            "harness.dll"
        );
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["HARNESS_CONFIG_DIR"] = Path.Combine(root, "config");
        environment["GIT_CONFIG_GLOBAL"] = Path.Combine(root, "absent.gitconfig");
        return McpClient.CreateAsync(
            new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = "dotnet",
                    Arguments = [binary, "mcp", "--project", root],
                    WorkingDirectory = root,
                    EnvironmentVariables = environment,
                    InheritEnvironmentVariables = false,
                }
            ),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "scm-test", Version = "fixture" },
                ProtocolVersion = "2025-06-18",
            },
            cancellationToken: Token
        );
    }
}
