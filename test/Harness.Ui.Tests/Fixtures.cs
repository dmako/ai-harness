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

    public static string Read(string path) => File.ReadAllText(Path.Combine(Folder, path));

    private static JsonArray Column(JsonArray columns, string status) =>
        columns.Single(c => (string?)c!["status"] == status)!["tasks"]!.AsArray();
}
