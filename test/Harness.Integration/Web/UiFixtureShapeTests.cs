using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Findings;
using Harness.Core.Tasks;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Tasks;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Web;

/// <summary>
/// The Avalonia UI's headless tests answer from fixture files (test/Harness.Ui.Tests/Fixtures, one per endpoint at
/// its route's path, e.g. api/projects/{id}/board.json). They must keep the server's shapes: every property a fixture
/// has must exist in a real harness ui's answer. With HARNESS_CAPTURE_UI_FIXTURES set to a folder, the server's
/// answers are also written there (the project id replaced by prj_fixture) to start or refresh fixtures from.
/// </summary>
[Collection("Provider processes")]
public sealed class UiFixtureShapeTests
{
    public const string CaptureVariable = "HARNESS_CAPTURE_UI_FIXTURES";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string FixtureFolder =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Harness.Ui.Tests/Fixtures"));

    [Fact]
    public async Task CommittedUiFixturesMatchTheServersShapes()
    {
        using var fixture = await SyncFixture.CreateAsync();
        Seed(fixture);
        await using var server = await UiFixture.StartAsync(fixture);
        var projects = JsonNode.Parse(await server.Client.GetStringAsync("/api/projects", Token))!;
        var id = (string)projects["projects"]![0]!["id"]!;
        await TitleAsync(server, id, "Fixture project");
        await PostAsync(
            server,
            "/api/workspaces",
            new JsonObject { ["name"] = "main", ["title"] = "Main workspace", ["project_ids"] = new JsonArray(id) }
        );
        var capture = Environment.GetEnvironmentVariable(CaptureVariable);

        var files = Directory.EnumerateFiles(FixtureFolder, "*.json", SearchOption.AllDirectories).ToArray();
        files.Should().NotBeEmpty();
        var problems = new List<string>();
        foreach (var file in files)
        {
            var route = "/" + Path.GetRelativePath(FixtureFolder, file).Replace('\\', '/')[..^".json".Length];
            // {id} is the project, {task} a task of it
            var path = route.Replace("{id}", id, StringComparison.Ordinal).Replace("{task}", "TASK-0001", StringComparison.Ordinal);
            var answer = await server.Client.GetStringAsync(path, Token);
            var served = Paths(JsonNode.Parse(answer));
            problems.AddRange(
                Paths(JsonNode.Parse(await File.ReadAllTextAsync(file, Token)))
                    .Where(path => !served.Contains(path))
                    .Select(path => $"{route}: {path} is not in the server's answer")
            );
            if (capture is { Length: > 0 })
            {
                var target = Path.Combine(capture, route.TrimStart('/') + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(
                    target,
                    JsonNode.Parse(answer.Replace(id, "prj_fixture", StringComparison.Ordinal))!.ToJsonString(
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
                    ),
                    Token
                );
            }
        }
        problems.Should().BeEmpty();
    }

    /// <summary>Gives the project a display title through harness ui's own write, so title appears in its answers.</summary>
    internal static Task TitleAsync(UiFixture server, string id, string title) =>
        PostAsync(server, $"/api/projects/{id}/rename", new JsonObject { ["title"] = title });

    /// <summary>A write through harness ui, as its page sends it.</summary>
    internal static async Task PostAsync(UiFixture server, string path, JsonObject body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Harness-UI", "1");
        request.Headers.Add("Origin", "http://" + server.Client.BaseAddress!.Authority);
        using var response = await server.Client.SendAsync(request, Token);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>
    /// A project with blueprints (coloured), a task in several statuses and one claimed, so lists and the activity feed
    /// have items to compare.
    /// </summary>
    internal static void Seed(SyncFixture fixture)
    {
        using var database = fixture.Database();
        var tasks = new TaskStore(database);
        tasks.ImportBlueprints(
            [
                new("BP-009", "V13 Features and bugfixes", "Ship v13", ["Released", "Documented"], "- one\n- two", Color: "yellow"),
                new("BP-008", "V12", Color: "lime"),
            ],
            "test",
            cancellationToken: Token
        );
        tasks.Import(
            [
                new TaskDefinition
                {
                    Id = "TASK-0001",
                    Title = "Ready one",
                    Blueprint = "BP-009",
                    Priority = "high",
                    Type = "spike",
                    Status = "ready",
                    Goal = "Try it out",
                    Acceptance = ["It runs"],
                    Scope = ["src"],
                    Validation = ["dotnet test"],
                    Notes = "A note",
                },
                new TaskDefinition
                {
                    Id = "TASK-0002",
                    Title = "Done one",
                    Blueprint = "BP-008",
                    Status = "done",
                },
                new TaskDefinition
                {
                    Id = "TASK-0003",
                    Title = "Claimed one",
                    Blueprint = "BP-009",
                    Status = "ready",
                },
            ],
            "test",
            cancellationToken: Token
        );
        // a claim, so the activity feed has a line
        tasks.Claim("TASK-0003", new("claude-code"), cancellationToken: Token);
        // a task waiting on another, and a proposal of each kind for the Inbox
        tasks.Import(
            [new TaskDefinition { Id = "TASK-0004", Title = "Waits", Blueprint = "BP-009", DependsOn = ["TASK-0001"] }],
            "test",
            cancellationToken: Token
        );
        tasks.Propose(
            new TaskDefinition
            {
                Title = "Proposed task",
                Goal = "Its goal",
                Blueprint = "BP-009",
                Acceptance = ["Checked"],
                DependsOn = ["TASK-0001"],
                Scope = ["src"],
            },
            "seen while working",
            "agent:test",
            Token
        );
        tasks.ProposeBlueprint("", new("", "Proposed blueprint", "Its goal", ["Done when shipped"], "a note"), "why", "agent:test", Token);
        new FindingStore(database).Propose(
            new FindingProposal
            {
                Kind = "task",
                Scope = "project",
                Basis = "observation",
                Title = "Split the service",
                Body = "It does two things",
                Evidence = ["src/Service.cs:12"],
                ProposedBy = "agent:test",
            },
            Token
        );
    }

    /// <summary>Every property path of <paramref name="node"/>; array items share their array's path ("columns[].tasks[].id").</summary>
    private static HashSet<string> Paths(JsonNode? node)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? current, string at)
        {
            switch (current)
            {
                case JsonObject obj:
                    foreach (var (name, value) in obj)
                    {
                        var path = at.Length is 0 ? name : at + "." + name;
                        paths.Add(path);
                        Walk(value, path);
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        Walk(item, at + "[]");
                    }
                    break;
                default:
                    break;
            }
        }
        Walk(node, "");
        return paths;
    }
}
