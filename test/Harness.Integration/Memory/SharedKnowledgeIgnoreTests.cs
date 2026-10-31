using System.Text.Json;
using AwesomeAssertions;
using Harness.Infrastructure.Memory;
using Harness.Integration.Sync;
using Harness.Integration.Web;
using Xunit;

namespace Harness.Integration.Memory;

/// <summary>A .gitignore that hides the shared knowledge file from Git, found where the author would notice.</summary>
public sealed class SharedKnowledgeIgnoreTests
{
    private const string Rule = ".gitignore:2:.harness/";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SharingAndDoctorSayWhenGitIgnoresTheFileAndHowToFixTheRule()
    {
        // the fixture's .gitignore ignores the whole .harness folder
        using var fixture = await SyncFixture.CreateAsync();
        (await DoctorAsync(fixture)).Should().NotContainKey("knowledge", "nothing is shared yet");
        string id;
        using (var database = fixture.Database())
        {
            id = new MemoryStore(database)
                .Put(
                    new()
                    {
                        Type = "decision",
                        Content = "The team shares knowledge through Git",
                        Source = new("user", "test", "test"),
                    },
                    Token
                )
                .Memory!.Id;
        }

        var shared = await fixture.CliAsync("memory", "share", id);
        shared.Code.Should().Be(0, shared.Error);
        shared.Output.Should().Contain($"Warning: Git ignores .harness/knowledge.toml (rule {Rule})");
        shared.Output.Should().Contain("Fix: replace a '.harness/' rule in .gitignore with '.harness/*'");

        var check = (await DoctorAsync(fixture))["knowledge"];
        check.GetProperty("level").GetString().Should().Be("warn");
        check.GetProperty("detail").GetString().Should().Contain(Rule);
        check.GetProperty("fix").GetString().Should().Be(SharedKnowledgeSync.IgnoreFix);

        await using (var server = await UiFixture.StartAsync(fixture))
        {
            var health = await UiTests.ReadAsync(
                server.Client,
                $"/api/projects/{UiWriteTests.ProjectId(fixture)}/health"
            );
            health["checks"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "knowledge")!["fix"]!
                .GetValue<string>()
                .Should()
                .Be(SharedKnowledgeSync.IgnoreFix);
            var root = $"/api/projects/{UiWriteTests.ProjectId(fixture)}/memories/{id}";
            await UiWriteTests.PostAsync(server.Client, root + "/withdraw", "{}");
            var again = await UiWriteTests.PostAsync(server.Client, root + "/share", "{}");
            again["warning"]!.GetValue<string>().Should().Contain(Rule);
            again["fix"]!.GetValue<string>().Should().Be(SharedKnowledgeSync.IgnoreFix);
        }

        fixture.Write(".gitignore", "*.log\n.harness/*\n!.harness/knowledge.toml\n");
        (await DoctorAsync(fixture))["knowledge"].GetProperty("level").GetString().Should().Be("ok");
        var json = await fixture.CliAsync("memory", "share", id, "--json");
        json.Code.Should().Be(1, "it is shared already");
        (await fixture.CliAsync("memory", "withdraw", id)).Code.Should().Be(0);
        var clean = await fixture.CliAsync("memory", "share", id, "--json");
        using var parsed = JsonDocument.Parse(clean.Output);
        parsed.RootElement.GetProperty("warning").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static async Task<Dictionary<string, JsonElement>> DoctorAsync(SyncFixture fixture)
    {
        var doctor = await fixture.CliAsync("doctor", "--json");
        using var json = JsonDocument.Parse(doctor.Output);
        return json
            .RootElement.GetProperty("checks")
            .EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.Clone());
    }
}
