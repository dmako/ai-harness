using System.Text.Json.Nodes;
using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Harness.Core.Web;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Tasks;

namespace Harness.Cli.Ui;

/// <summary>harness ui's Board (TASK-0081): the columns, and the choices kept in this machine's ui.json.</summary>
internal sealed partial class UiReadService
{
    private UiPreferences Settings => new(GlobalDirectory);

    /// <summary>The project's Board: one column per status with its cards, its settings and what they can be.</summary>
    private JsonNode? Board(string projectId, TaskStore tasks, CancellationToken token)
    {
        var settings = Settings.Board(projectId);
        var columns = TaskBoard.Columns(tasks.List(cancellationToken: token), settings, DateTimeOffset.UtcNow);
        return UiJson.Node(
            new JsonObjectMap
            {
                ["settings"] = BoardView(settings, blueprint: true),
                ["defaults"] = BoardView(Settings.BoardDefaults(), blueprint: false),
                ["sorts"] = TaskBoard.Sorts.ToArray(),
                ["hide_done_days_options"] = TaskBoard.HideDoneDays.ToArray(),
                ["columns"] = columns
                    .Select(column => new JsonObjectMap
                    {
                        ["status"] = column.Status,
                        ["tasks"] = column.Tasks.Select(TaskViews.Task).ToArray(),
                    })
                    .ToArray(),
                ["blueprints"] = tasks.Blueprints(token).Select(TaskViews.Blueprint).ToArray(),
            }
        );
    }

    /// <summary>Sets a project's own Board choices (sort, hide_done_days, blueprint); what is not given stays.</summary>
    private async Task<JsonNode?> SetBoardAsync(string projectId, JsonObject body, CancellationToken token)
    {
        var fields = new UiWriteBody(body, "sort", "hide_done_days", "blueprint");
        await Settings.SetBoardAsync(
            projectId,
            fields.Optional("sort"),
            fields.Number("hide_done_days"),
            fields.Optional("blueprint"),
            token
        );
        return UiJson.Node(new JsonObjectMap { ["settings"] = BoardView(Settings.Board(projectId), blueprint: true) });
    }

    private JsonNode? BoardDefaults() =>
        UiJson.Node(new JsonObjectMap { ["defaults"] = BoardView(Settings.BoardDefaults(), blueprint: false) });

    /// <summary>Sets the Board's defaults (Machine › Board) for projects without their own.</summary>
    private async Task<JsonNode?> SetBoardDefaultsAsync(JsonObject body, CancellationToken token)
    {
        var fields = new UiWriteBody(body, "sort", "hide_done_days");
        await Settings.SetBoardDefaultsAsync(fields.Optional("sort"), fields.Number("hide_done_days"), token);
        return BoardDefaults();
    }

    private static JsonObjectMap BoardView(BoardSettings settings, bool blueprint)
    {
        JsonObjectMap view = new() { ["sort"] = settings.Sort, ["hide_done_days"] = settings.HideDoneDays };
        if (blueprint)
        {
            view["blueprint"] = settings.Blueprint;
        }

        return view;
    }
}
