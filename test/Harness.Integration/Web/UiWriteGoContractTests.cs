using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Harness.Core.Tasks;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Web;

[Collection("Provider processes")]
public sealed partial class UiWriteGoContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AuthorWriteAndReviewResponsesReplayTheFrozenGoContract()
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_UI_ORACLE");
        if (string.IsNullOrEmpty(oracle) || !File.Exists(oracle))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_UI_ORACLE to the frozen harness executable.");
        }
        var expected = await ReplayAsync(oracle);
        var actual = await ReplayAsync(null);
        for (var i = 0; i < expected.Count; i++)
        {
            JsonNode
                .DeepEquals(expected[i], actual[i])
                .Should()
                .BeTrue($"write step {i}: Go {expected[i]} / C# {actual[i]}");
        }
    }

    private static async Task<JsonArray> ReplayAsync(string? oracle)
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var database = fixture.Database();
        var memories = new MemoryStore(database);
        var original = memories
            .Put(new() { Type = "goal", Content = "Optimize the repeated MCP calls" }, Token)
            .Memory!;
        var promotion = memories
            .Put(new() { Type = "convention", Content = "Use shared code conventions" }, Token)
            .Memory!;
        memories.Put(new() { Type = "decision", Content = "Template payload enums are stored as integers" }, Token);
        var proposal = memories
            .Propose(
                new(
                    "decision",
                    "Template payload enums are stored as names",
                    Source: new("agent", "contract", "agent:test")
                ),
                Token
            )
            .Proposal!;
        var rejected = memories.Propose(new("convention", "Reject the proposed convention"), Token).Proposal!;
        var tasks = new TaskStore(database);
        var definition = new TaskDefinition
        {
            Title = "Proposed task",
            Goal = "Review task suggestions",
            Acceptance = ["Author approves"],
        };
        var taskProposal = tasks.Propose(definition, "A useful task", "agent:test", Token);
        var rejectedTask = tasks.Propose(
            definition with
            {
                Title = "Rejected task",
            },
            "Test rejection",
            "agent:test",
            Token
        );
        fixture.Write(
            "src/Domain/Domain.csproj",
            """<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../Dal/Dal.csproj" /></ItemGroup></Project>"""
        );
        fixture.Write(
            "src/Dal/Dal.csproj",
            """<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"""
        );
        memories.Put(
            new()
            {
                Type = "constraint",
                Content = "forbid: Domain -> Dal",
                Force = true,
            },
            Token
        );
        await fixture.RunAsync();
        var sets = new FindingSets(
            fixture.Root,
            UiWriteTests.ProjectId(fixture),
            database,
            fixture.Lifecycle.GlobalConfigurationPath,
            fixture.Lifecycle.Registry
        );
        var finding = (await sets.ReadAsync("project", Token))
            .Groups.Single()
            .Findings.First(f => f.Kind is "boundary");
        var findingProposal = await sets.ProposeAsync(
            new("by_design", finding.Key, "project", Reason: "Agent proposes a deliberate boundary"),
            "agent:test",
            Token
        );
        var findingReject = await sets.ProposeAsync(
            new(
                "task",
                "",
                "project",
                Title: "Agent observation",
                Reason: "Could improve this area",
                Evidence: ["src/Domain/Domain.csproj:1"]
            ),
            "agent:test",
            Token
        );
        await using var server = await UiFixture.StartAsync(fixture, oracle);
        var id = UiWriteTests.ProjectId(fixture);
        var root = $"/api/projects/{id}";
        JsonArray results = [];
        async Task<JsonNode> WriteAsync(string path, string json)
        {
            var result = await UiWriteTests.PostAsync(server.Client, path, json);
            results.Add(result.DeepClone());
            return result;
        }
        var correction = await WriteAsync(
            $"{root}/memories/{original.Id}/supersede",
            """{"content":"Optimize all repeated MCP calls","reason":"Clarified by the author"}"""
        );
        await WriteAsync(
            $"{root}/memories/{correction["memory"]!["id"]!.GetValue<string>()}/retire",
            """{"reason":"Replaced by a wider policy"}"""
        );
        await WriteAsync($"{root}/proposals/{proposal.Id}/approve", """{}""");
        await WriteAsync(
            $"{root}/proposals/{proposal.Id}/approve",
            """{"keep_both":true,"note":"Both decisions apply"}"""
        );
        await WriteAsync($"{root}/proposals/{rejected.Id}/reject", """{"note":"Not accepted"}""");
        await WriteAsync(
            root + "/tasks",
            """{"prefix":"UI","title":"Manual task","goal":"Author creates work","acceptance":["Created"],"scope":[" src "]}"""
        );
        await WriteAsync(
            $"{root}/task-proposals/{taskProposal.Id}/approve",
            """{"task":{"prefix":"UI","title":"Edited proposal","goal":"Author reviewed work","acceptance":["Approved"]}}"""
        );
        await WriteAsync($"{root}/task-proposals/{rejectedTask.Id}/reject", """{"note":"Out of scope"}""");
        await WriteAsync(
            "/api/workspaces",
            new JsonObject
            {
                ["name"] = "review",
                ["title"] = "Review space",
                ["project_ids"] = new JsonArray(id),
            }.ToJsonString()
        );
        await WriteAsync("/api/workspaces/review/rename", """{"name":"reviewed","title":"Reviewed space"}""");
        var wider = await WriteAsync(
            "/api/knowledge/memories",
            """{"level":"workspace:reviewed","type":"goal","content":"Share reviewed knowledge"}"""
        );
        var widerId = wider["memory"]!["id"]!.GetValue<string>();
        var edited = await WriteAsync(
            $"/api/knowledge/memories/{widerId}/supersede",
            """{"level":"workspace:reviewed","content":"Share knowledge reviewed by the author","reason":"Clarified ownership"}"""
        );
        await WriteAsync(
            $"/api/knowledge/memories/{edited["memory"]!["id"]!.GetValue<string>()}/retire",
            """{"level":"workspace:reviewed","reason":"Finished"}"""
        );
        await WriteAsync($"{root}/memories/{promotion.Id}/promote", """{"to":"workspace:reviewed","dry_run":true}""");
        await WriteAsync($"{root}/memories/{promotion.Id}/promote", """{"to":"workspace:reviewed"}""");
        await WriteAsync($"{root}/rename", """{"name":"reviewed-project","title":"Reviewed project"}""");
        await WriteAsync("/api/workspaces/reviewed/leave", new JsonObject { ["project_id"] = id }.ToJsonString());
        await WriteAsync(
            "/api/workspaces/reviewed/join",
            new JsonObject { ["project_ids"] = new JsonArray(id) }.ToJsonString()
        );
        await WriteAsync("/api/workspaces/reviewed/delete", """{}""");
        var triage = new JsonObject
        {
            ["key"] = finding.Key,
            ["reason"] = "Deliberate compatibility boundary",
        }.ToJsonString();
        await WriteAsync(root + "/findings/mark", triage);
        await WriteAsync(root + "/findings/unmark", triage);
        await WriteAsync(
            $"{root}/arch-proposals/{findingProposal.Proposal.Id}/approve",
            """{"body":"Reviewed by author"}"""
        );
        await WriteAsync(root + "/findings/task", triage);
        await WriteAsync($"{root}/arch-proposals/{findingReject.Proposal.Id}/reject", """{"note":"Not now"}""");
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        Normalize(results, ids);
        return results;
    }

    private static void Normalize(JsonNode node, Dictionary<string, string> ids)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (
                    (key.EndsWith("_at", StringComparison.Ordinal) || key is "valid_from" or "valid_until")
                    && value is not null
                )
                {
                    obj[key] = "<time>";
                }
                else if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    obj[key] = NormalizeText(text, ids);
                }
                else if (value is not null)
                {
                    Normalize(value, ids);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    array[i] = NormalizeText(text, ids);
                }
                else if (array[i] is { } item)
                {
                    Normalize(item, ids);
                }
            }
        }
    }

    private static string NormalizeText(string text, Dictionary<string, string> ids) =>
        Identifier()
            .Replace(
                text,
                match =>
                {
                    if (!ids.TryGetValue(match.Value, out var replacement))
                    {
                        replacement = "<id:" + ids.Count + ">";
                        ids.Add(match.Value, replacement);
                    }
                    return replacement;
                }
            );

    [GeneratedRegex(@"\b(?:mem|prop|tpr|prj|apr)_[0-9a-f]{16,32}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
