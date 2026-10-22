using System.Text;
using System.Text.Json.Nodes;
using Harness.Core.Web;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Workspaces;

namespace Harness.Cli.Ui;

internal sealed class UiProjectCatalog(ProjectLifecycle lifecycle) : IDisposable
{
    private sealed record Snapshot(string Key, DateTimeOffset At, IReadOnlyList<JsonObject> Projects);

    private readonly SemaphoreSlim _gate = new(1);
    private Snapshot? _snapshot;

    public void Forget() => _snapshot = null;

    private WorkspaceStore Workspaces => new(lifecycle.GlobalConfigurationPath, lifecycle.Registry);

    private string Key()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(lifecycle.GlobalConfigurationPath))!;
        StringBuilder key = new();
        void Stamp(string path)
        {
            var file = new FileInfo(path);
            key.Append(path)
                .Append('@')
                .Append(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)
                .Append(':')
                .Append(file.Exists ? file.Length : 0)
                .Append(';');
        }
        Stamp(Path.Combine(directory, "projects.json"));
        Stamp(Path.Combine(directory, "workspaces.json"));
        foreach (var project in lifecycle.Registry.Load()["projects"]!.AsArray().OfType<JsonObject>())
        {
            var path =
                project["db_path"]?.GetValue<string>()
                ?? Path.Combine(project["root"]!.GetValue<string>(), ".harness", "harness.db");
            Stamp(path);
            Stamp(path + "-wal");
        }
        return key.ToString();
    }

    public async Task<IReadOnlyList<JsonObject>> ListAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var key = Key();
            if (
                _snapshot is { } cached
                && cached.Key == key
                && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(2)
            )
            {
                return cached.Projects.Select(p => (JsonObject)p.DeepClone()).ToArray();
            }
            List<JsonObject> projects = [];
            foreach (var project in lifecycle.Registry.Load()["projects"]!.AsArray().OfType<JsonObject>())
            {
                projects.Add(await InspectAsync(project, token));
            }
            _snapshot = new(key, DateTimeOffset.UtcNow, projects);
            return projects.Select(p => (JsonObject)p.DeepClone()).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JsonObject> FindAsync(string id, CancellationToken token)
    {
        if (
            _snapshot is { } cached
            && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(2)
            && cached.Key == Key()
            && cached.Projects.FirstOrDefault(p => Text(p, "id") == id) is { } found
        )
        {
            return (JsonObject)found.DeepClone();
        }
        var project =
            lifecycle.Registry.Load()["projects"]!
                .AsArray()
                .OfType<JsonObject>()
                .FirstOrDefault(p => Text(p, "id") == id)
            ?? throw new UiRequestException(404, id + ": project not registered");
        return await InspectAsync(project, token);
    }

    private async Task<JsonObject> InspectAsync(JsonObject project, CancellationToken token)
    {
        var copy = (JsonObject)project.DeepClone();
        copy["live"] = await ProjectOverview.InspectAsync(lifecycle, project, token);
        if (Directory.Exists(Text(project, "root")))
        {
            var clientChecks = UiReadService.ClientChecks(Text(project, "root"));
            copy["live"]!["checks_warned"] =
                copy["live"]!["checks_warned"]!.GetValue<int>() + clientChecks.Count(c => c.Level is "warn");
            copy["live"]!["checks_failed"] =
                copy["live"]!["checks_failed"]!.GetValue<int>() + clientChecks.Count(c => c.Level is "fail");
        }
        var names = Workspaces
            .Load()
            .Where(w => w.Members.Contains(Text(project, "id"), StringComparer.Ordinal))
            .Select(w => w.Name)
            .ToArray();
        if (names.Length > 0)
        {
            copy["workspaces"] = UiJson.Node(names);
        }
        return copy;
    }

    public static string Text(JsonNode node, string key) => node[key]?.GetValue<string>() ?? "";

    public static string Display(JsonNode project) =>
        project["title"]?.GetValue<string>() is { Length: > 0 } title
            ? title
            : WorkspaceStore.ShortName(project.AsObject());

    public void Dispose() => _gate.Dispose();
}
