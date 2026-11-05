using System.Text.Json.Nodes;
using Harness.Core.Tasks;
using Harness.Infrastructure.Workspaces;

namespace Harness.Infrastructure.Projects;

/// <summary>
/// harness ui's own choices, kept on this machine beside the global config (ui.json) so they reach every browser
/// and survive restarts: the Board's defaults (Machine › Board) and each project's Board choices. A project's sort
/// and done-age fall back to the defaults when it has none; its blueprint filter is its own only.
/// </summary>
public sealed class UiPreferences(string globalDirectory)
{
    private string Path => System.IO.Path.Combine(globalDirectory, "ui.json");

    private JsonObject Load() => AtomicMachineFile.Load(Path, () => new JsonObject { ["version"] = 1 });

    /// <summary>The Board's defaults for projects without their own.</summary>
    public BoardSettings BoardDefaults() => Defaults(Load());

    /// <summary>The Board's settings for a project: its own, else the defaults.</summary>
    public BoardSettings Board(string projectId)
    {
        var all = Load();
        var defaults = Defaults(all);
        var own = all["board"]?["projects"]?[projectId] as JsonObject;
        return new(
            Sort(own) ?? defaults.Sort,
            Days(own) ?? defaults.HideDoneDays,
            own?["blueprint"]?.GetValue<string>() ?? ""
        );
    }

    /// <summary>Sets the defaults; what is not given (null) stays.</summary>
    public Task SetBoardDefaultsAsync(string? sort, int? hideDoneDays, CancellationToken token = default) =>
        UpdateAsync(board => Set(Child(board, "defaults"), sort, hideDoneDays, blueprint: null), token);

    /// <summary>Sets a project's own choices; what is not given (null) stays.</summary>
    public Task SetBoardAsync(
        string projectId,
        string? sort,
        int? hideDoneDays,
        string? blueprint,
        CancellationToken token = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return UpdateAsync(
            board => Set(Child(Child(board, "projects"), projectId), sort, hideDoneDays, blueprint),
            token
        );
    }

    private Task<bool> UpdateAsync(Action<JsonObject> edit, CancellationToken token) =>
        AtomicMachineFile.UpdateAsync(
            Path,
            Load,
            all =>
            {
                edit(Child(all, "board"));
                return true;
            },
            token
        );

    private static void Set(JsonObject target, string? sort, int? hideDoneDays, string? blueprint)
    {
        if (sort is not null)
        {
            target["sort"] = TaskBoard.ValidSort(sort);
        }

        if (hideDoneDays is { } days)
        {
            target["hide_done_days"] = TaskBoard.ValidHideDoneDays(days);
        }

        if (blueprint is not null)
        {
            target["blueprint"] = blueprint.Trim();
        }
    }

    private static BoardSettings Defaults(JsonObject all)
    {
        var defaults = all["board"]?["defaults"] as JsonObject;
        return new(Sort(defaults) ?? TaskBoard.Defaults.Sort, Days(defaults) ?? TaskBoard.Defaults.HideDoneDays);
    }

    // a value an older or hand-edited file holds that the Board no longer offers falls back to the default
    private static string? Sort(JsonObject? o) =>
        o?["sort"] is JsonValue v && v.TryGetValue<string>(out var sort) && TaskBoard.Sorts.Contains(sort)
            ? sort
            : null;

    private static int? Days(JsonObject? o) =>
        o?["hide_done_days"] is JsonValue v && v.TryGetValue<int>(out var days) && TaskBoard.HideDoneDays.Contains(days)
            ? days
            : null;

    private static JsonObject Child(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject child)
        {
            return child;
        }

        child = [];
        parent[key] = child;
        return child;
    }
}
