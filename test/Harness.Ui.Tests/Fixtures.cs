using System.Text.Json.Nodes;

namespace Harness.Ui.Tests;

/// <summary>
/// Answers as harness ui gives them. The base answers are files under Fixtures/, one per endpoint, at the route's
/// path with its parameters kept as written ({id}); FixtureApi serves them. The helpers here vary them for a test.
/// CommittedUiFixturesMatchTheServersShapes (Harness.Integration) checks the files against a real server.
/// </summary>
internal static class Fixtures
{
    public const string ProjectId = "prj_fixture";

    /// <summary>The folder FixtureApi reads (copied beside the test assembly).</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Projects => Read("api/projects.json");

    /// <summary>
    /// The Board fixture with its sort set to <paramref name="sort"/>, TASK-0002 titled <paramref name="title"/>
    /// when given, and <paramref name="moreDone"/> more done tasks (TASK-1000…) so the done column scrolls.
    /// </summary>
    public static string Board(string sort = "priority", string? title = null, int moreDone = 0)
    {
        var board = JsonNode.Parse(Read("api/projects/{id}/board.json"))!;
        board["settings"]!["sort"] = sort;
        var columns = board["columns"]!.AsArray();
        if (title is not null)
        {
            Column(columns, "in_progress").Single(t => (string?)t!["id"] == "TASK-0002")!["title"] = title;
        }
        var done = Column(columns, "done");
        for (var n = 1000; n < 1000 + moreDone; n++)
        {
            done.Add(
                new JsonObject
                {
                    ["id"] = $"TASK-{n}",
                    ["title"] = $"Done task {n} with a title that wraps onto a second line",
                    ["status"] = "done",
                    ["priority"] = "low",
                    ["type"] = "chore",
                    ["blueprint"] = "BP-008",
                }
            );
        }
        return board.ToJsonString();
    }

    /// <summary>
    /// The Health fixture synced: two recent syncs (the newest partial, its graph provider failing; the one before
    /// refreshing both providers), the index fresh; with <paramref name="healthy"/> every check passes.
    /// </summary>
    public static string Health(bool healthy = false)
    {
        var health = JsonNode.Parse(Read("api/projects/{id}/health.json"))!;
        var sync = health["status"]!["sync"]!.AsObject();
        sync.Remove("stale");
        sync["synced_at"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        sync["runs"] = new JsonArray(
            Run(2, minutesAgo: 5, "partial", "eeacf0ad6662", ("text", "refreshed", null), ("graph", "failed", "the graph provider timed out after 120 s")),
            Run(1, minutesAgo: 90, "ok", "1234567890ab", ("text", "refreshed", null), ("graph", "refreshed", null))
        );
        health["status"]!["providers"] = new JsonArray("text", "graph");
        if (healthy)
        {
            foreach (var check in health["checks"]!.AsArray())
            {
                check!["level"] = "ok";
                check["fix"] = null;
            }
            health["healthy"] = true;
            health["fixable"] = new JsonArray();
            health["previewed"] = new JsonArray();
        }
        return health.ToJsonString();
    }

    private static JsonObject Run(long id, int minutesAgo, string status, string head, params (string Name, string Status, string? Detail)[] providers) =>
        new()
        {
            ["id"] = id,
            ["started_at"] = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("O"),
            ["finished_at"] = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).AddSeconds(4).ToString("O"),
            ["mode"] = "incremental",
            ["reason"] = "post-commit",
            ["base_commit"] = "0000aaaa1111",
            ["head_commit"] = head,
            ["added"] = 2,
            ["changed"] = 5,
            ["removed"] = 1,
            ["status"] = status,
            ["providers"] = new JsonArray(
                [.. providers.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["status"] = p.Status, ["detail"] = p.Detail })]
            ),
        };

    public static string Read(string path) => File.ReadAllText(Path.Combine(Folder, path));

    private static JsonArray Column(JsonArray columns, string status) =>
        columns.Single(c => (string?)c!["status"] == status)!["tasks"]!.AsArray();
}
