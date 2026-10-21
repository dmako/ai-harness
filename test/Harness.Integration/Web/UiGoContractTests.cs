using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Core.Tasks;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Tasks;
using Harness.Infrastructure.Workspaces;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Web;

[Collection("Provider processes")]
public sealed class UiGoContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ReadResponsesReplayTheFrozenGoHttpContract()
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_UI_ORACLE");
        if (string.IsNullOrEmpty(oracle) || !File.Exists(oracle))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_UI_ORACLE to the frozen harness executable.");
        }
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write("example.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        fixture.Write("a.cs", "public sealed class Example { }\n");
        await fixture.CommitAsync("UI oracle");
        string memoryId;
        using (var database = fixture.Database())
        {
            memoryId = new MemoryStore(database).Put(new MemoryInput { Type = "goal", Content = "Keep UI reads fast", Source = new("system", "TASK-0027", "fixture") }, Token).Memory!.Id;
            var tasks = new TaskStore(database);
            tasks.ImportMilestones([new("web", "UI port", "Serve embedded read views", ["Runs offline"])], "test", cancellationToken: Token);
            tasks.Import([new TaskDefinition { Id = "TASK-0001", Title = "Serve UI", Milestone = "web", Acceptance = ["Loopback only"] }], "test", cancellationToken: Token);
        }
        var id = fixture.Lifecycle.Registry.Load()["projects"]![0]!["id"]!.GetValue<string>();
        var workspaces = new WorkspaceStore(fixture.Lifecycle.GlobalConfigurationPath, fixture.Lifecycle.Registry);
        await workspaces.CreateAsync("web", Token);
        await workspaces.JoinAsync("web", [id], Token);
        await using var target = await UiFixture.StartAsync(fixture);
        await using var source = await UiFixture.StartAsync(fixture, oracle);
        var paths = new[] { "/api/projects", "/api/workspaces", "/api/docs", "/api/docs/guide", "/api/docs/reference", "/api/search?q=UI", "/api/activity", "/api/stacks", "/api/knowledge?level=user", "/api/knowledge?level=workspace:web", "/api/workspaces/discover?folder=" + Uri.EscapeDataString(fixture.DirectoryRoot) }
            .Concat(new[] { "summary", "metrics", "context", "report", "providers", "hooks", "memories", "memories/" + memoryId, "proposals", "tasks", "milestones", "tasks/TASK-0001/events", "tasks-next-id", "task-proposals", "arch-proposals", "applies", "stack", "structure", "snippet?at=a.cs:1" }.Select(p => $"/api/projects/{id}/{p}"))
            .Concat(new[] { "dependencies", "issues", "duplicates", "stack", "system", "findings", "adoption?library=repository", "snippet?at=repository:%20a.cs:1" }.Select(p => "/api/workspaces/web/" + p));
        List<string> differences = [];
        foreach (var path in paths)
        {
            var expected = Normalize(await UiTests.ReadAsync(source.Client, path));
            var actual = Normalize(await UiTests.ReadAsync(target.Client, path));
            if (!JsonNode.DeepEquals(expected, actual))
            {
                differences.Add(path + "\nGo: " + expected.ToJsonString() + "\nC#: " + actual.ToJsonString());
            }
        }
        foreach (var asset in new[] { "/", "/app.js", "/style.css", "/brand.css", "/logo.svg", "/favicon.png", "/apple-touch-icon.png" })
        {
            var expected = await source.Client.GetByteArrayAsync(asset, Token);
            var actual = await target.Client.GetByteArrayAsync(asset, Token);
            if (asset is "/app.js")
            {
                // PORT-028 adds offline polling; PORT-030 guards asynchronous review loading; all other bytes remain frozen.
                var script = Encoding.UTF8.GetString(actual);
                var newline = script.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                script = script.Replace(string.Join(newline,
                    "  let poll = 0;",
                    "  const stopPolling = () => { clearInterval(poll); poll = 0; };",
                    "  const startPolling = () => {",
                    "    if (!poll) poll = setInterval(() => { if (!document.hidden) queueLive({ all: true }); }, 15000);",
                    "  };",
                    "  if (!window.EventSource) { startPolling(); return; }"), "  if (!window.EventSource) return;", StringComparison.Ordinal)
                    .Replace("      stopPolling();" + newline, "", StringComparison.Ordinal)
                    .Replace("      startPolling();" + newline, "", StringComparison.Ordinal);
                var reviewChanges = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(ProviderFixture.RepositoryRoot, "test/fixtures/compatibility/ui-review-port-changes.json"), Token))!.AsArray();
                foreach (var change in reviewChanges.Reverse())
                {
                    var port = change!["port"]!.GetValue<string>().ReplaceLineEndings(newline);
                    script.Should().Contain(port, "the documented review-race patch must be present");
                    script = script.Replace(port, change["go"]!.GetValue<string>().ReplaceLineEndings(newline), StringComparison.Ordinal);
                }
                actual = Encoding.UTF8.GetBytes(script);
            }
            actual.Should().Equal(expected, asset);
        }
        var documentation = await UiTests.ReadAsync(source.Client, "/api/docs");
        foreach (var section in documentation["sections"]!.AsArray().OfType<JsonObject>())
        {
            var slug = section["slug"]!.GetValue<string>();
            var expected = Normalize(await UiTests.ReadAsync(source.Client, "/api/docs/" + slug));
            var actual = Normalize(await UiTests.ReadAsync(target.Client, "/api/docs/" + slug));
            JsonNode.DeepEquals(expected, actual).Should().BeTrue("documentation section " + slug + ": " + expected + " / " + actual);
        }
        var sourceHealth = await UiTests.ReadAsync(source.Client, $"/api/projects/{id}/health");
        var targetHealth = await UiTests.ReadAsync(target.Client, $"/api/projects/{id}/health");
        foreach (var key in new[] { "git", "project", "database", "config", "clients", "worktrees", "providers", "sync" })
        {
            JsonNode.DeepEquals(sourceHealth["status"]![key], targetHealth["status"]![key]).Should().BeTrue(key + ": " + sourceHealth + " / " + targetHealth);
        }
        targetHealth["healthy"]!.GetValue<bool>().Should().Be(sourceHealth["healthy"]!.GetValue<bool>());
        differences.Should().BeEmpty("read API JSON must match the frozen implementation:\n" + string.Join("\n\n", differences));
    }
    private static JsonNode Normalize(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (key is "since" or "generated_at")
                { obj[key] = "<request time>"; }
                else if (key is "body" && value is JsonValue body && body.TryGetValue<string>(out var text))
                { obj[key] = text.Replace(":7477", ":17477", StringComparison.Ordinal).Replace("\r\nThe default listener is `127.0.0.1:17477`. Use `--addr` to override it and `--no-open` to leave the browser closed.\r\n", "", StringComparison.Ordinal); }
                else if (value is not null)
                { Normalize(value); }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var value in array.OfType<JsonNode>())
            { Normalize(value); }
        }
        return node;
    }
}
