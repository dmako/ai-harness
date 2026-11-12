using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Telemetry;
using Harness.Infrastructure.Scm;
using Harness.Infrastructure.Telemetry;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Web;

/// <summary>A project's SCM card endpoints (TASK-0132): read, choose and test, with the scm_ call statistics.</summary>
[Collection("Provider processes")]
public sealed class UiScmProjectTests
{
    private const string Good = "glpat-good-token-0123456789";
    private const string Bad = "glpat-wrong-token-9876543210";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A project whose origin is on the fake GitLab, served there by 'work' (good token) and 'bot' (bad).</summary>
    internal static async Task<(SyncFixture Fixture, FakeGitlabServer Gitlab)> TwoConnectionsAsync()
    {
        var fixture = await SyncFixture.CreateAsync();
        var gitlab = new FakeGitlabServer(
            Good,
            new Dictionary<string, string>
            {
                ["GET /api/v4/projects/group%2Frepo"] =
                    """{"id":5,"path_with_namespace":"group/repo","name":"repo","visibility":"private"}""",
            }
        );
        await fixture.GitAsync("remote", "add", "origin", gitlab.Url + "/group/repo.git");
        var store = ScmConfigurationFile.ForGlobal(Path.Combine(fixture.Global, "config.toml"));
        await store.AddAsync(new("work", "gitlab", gitlab.Url, Good, null), Token);
        await store.AddAsync(new("bot", "gitlab", gitlab.Url, Bad, null), Token);
        using var database = fixture.Database();
        var telemetry = new TelemetryStore(database);
        var now = DateTimeOffset.UtcNow;
        telemetry.RecordTool(
            new ToolObservation(now.AddHours(-2), "scm_get_merge_request_diff", "", "claude-code", 120, 900, false),
            Token
        );
        telemetry.RecordTool(
            new ToolObservation(now.AddHours(-1), "scm_get_merge_request_diff", "", "claude-code", 90, 800, false),
            Token
        );
        telemetry.RecordTool(
            new ToolObservation(
                now.AddMinutes(-30),
                "scm_add_merge_request_comment",
                "",
                "claude-code",
                300,
                100,
                false,
                "forbidden"
            ),
            Token
        );
        telemetry.RecordTool(
            new ToolObservation(now.AddMinutes(-20), "get_context", "", "claude-code", 40, 5000, false),
            Token
        );
        return (fixture, gitlab);
    }

    [Fact]
    public async Task TheCardChoosesAmongConnectionsForOneServerTestsItAndCountsTheScmCalls()
    {
        var (fixture, gitlab) = await TwoConnectionsAsync();
        using var _ = fixture;
        await using var __ = gitlab;
        await using var server = await UiFixture.StartAsync(fixture);
        var id = UiWriteTests.ProjectId(fixture);
        var path = $"/api/projects/{id}/scm";

        var card = JsonNode.Parse(await server.Client.GetStringAsync(path + "?days=7", Token))!;
        card["configured"]!.GetValue<bool>().Should().BeTrue();
        card["scm"]!["status"]!.GetValue<string>().Should().Be("ambiguous");
        card["candidates"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).Should().Equal("work", "bot");
        card["candidates"]![0]!["token"]!.GetValue<string>().Should().StartWith("token ****");
        card.ToJsonString().Should().NotContain(Good).And.NotContain(Bad);
        var stats = card["stats"]!;
        stats["days"]!.GetValue<int>().Should().Be(7);
        stats["tools"]!
            .AsArray()
            .Select(t => (t!["tool"]!.GetValue<string>(), t["calls"]!.GetValue<int>(), t["errors"]!.GetValue<int>()))
            .Should()
            .Equal(("scm_get_merge_request_diff", 2, 0), ("scm_add_merge_request_comment", 1, 1));
        stats["trend"]!["days"]!.AsArray().Sum(d => d!["calls"]!.GetValue<int>()).Should().Be(3);

        var chosen = await UiWriteTests.PostAsync(server.Client, path + "/choice", """{"name":"work"}""");
        chosen["scm"]!["status"]!.GetValue<string>().Should().Be("matched");
        chosen["scm"]!["source"]!.GetValue<string>().Should().Be("machine");
        chosen["choice"]!.GetValue<string>().Should().Be("work");
        chosen["chosen"]!.GetValue<string>().Should().Be("chosen on this machine");
        ScmConfigurationFile
            .ForGlobal(Path.Combine(fixture.Global, "config.toml"))
            .ChoiceFor(Path.Combine(fixture.Root, ".git"))
            .Should()
            .Be("work");

        var tested = (await UiWriteTests.PostAsync(server.Client, path + "/test", "{}"))["test"]!;
        tested["test"]!["ok"]!.GetValue<bool>().Should().BeTrue();
        tested["test"]!["user"]!.GetValue<string>().Should().Be("reviewer");
        tested["project"]!["level"]!.GetValue<string>().Should().Be("ok");
        tested["project"]!["detail"]!.GetValue<string>().Should().Contain("reachable (group/repo)");
        JsonNode.Parse(await server.Client.GetStringAsync(path, Token))!["last_test"]!["name"]!
            .GetValue<string>()
            .Should()
            .Be("work");

        await UiWriteTests.PostAsync(server.Client, path + "/choice", """{"name":"bot"}""");
        var rejected = (await UiWriteTests.PostAsync(server.Client, path + "/test", "{}"))["test"]!;
        rejected["test"]!["failure"]!.GetValue<string>().Should().Be("unauthorized");
        rejected.ToJsonString().Should().NotContain(Bad);

        var automatic = await UiWriteTests.PostAsync(server.Client, path + "/choice", """{"name":""}""");
        automatic["scm"]!["status"]!.GetValue<string>().Should().Be("ambiguous");
        automatic["choice"].Should().BeNull();

        using (var unknown = UiWriteTests.Request(server.Client, path + "/choice", """{"name":"nope"}"""))
        using (var response = await server.Client.SendAsync(unknown, Token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["error"]!
                .GetValue<string>()
                .Should()
                .Contain("No SCM is named 'nope'");
        }

        using (var noTest = UiWriteTests.Request(server.Client, path + "/test", "{}"))
        using (var response = await server.Client.SendAsync(noTest, Token))
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["error"]!
                .GetValue<string>()
                .Should()
                .Contain("matches several SCMs");
        }

        // the write protection of every harness ui write applies
        using (var foreign = UiWriteTests.Request(server.Client, path + "/choice", """{"name":"work"}"""))
        {
            foreign.Headers.Remove("X-Harness-UI");
            using var response = await server.Client.SendAsync(foreign, Token);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        // [scm] name in the project's configuration decides, and the card says so
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".harness"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, ".harness", "config.toml"),
            "[scm]\nname = \"bot\"\n",
            Token
        );
        var configured = JsonNode.Parse(await server.Client.GetStringAsync(path, Token))!;
        configured["config_name"]!.GetValue<string>().Should().Be("bot");
        configured["scm"]!["source"]!.GetValue<string>().Should().Be("config");
        configured["scm"]!["name"]!.GetValue<string>().Should().Be("bot");
    }

    [Fact]
    public async Task WithoutAnyScmTheCardSaysSo()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var server = await UiFixture.StartAsync(fixture);

        var card = JsonNode.Parse(
            await server.Client.GetStringAsync($"/api/projects/{UiWriteTests.ProjectId(fixture)}/scm", Token)
        )!;

        card["configured"]!.GetValue<bool>().Should().BeFalse();
        card["scm"]!["status"]!.GetValue<string>().Should().Be("no_scm");
        card["stats"]!["tools"]!.AsArray().Should().BeEmpty();
    }
}
