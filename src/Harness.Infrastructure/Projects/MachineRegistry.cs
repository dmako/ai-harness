using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Projects;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Projects;

public sealed class MachineRegistry(string path)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public JsonObject Load()
    {
        try
        {
            var registry = JsonNode.Parse(File.ReadAllText(path, StrictUtf8))?.AsObject() ?? throw new InvalidDataException("Registry is null.");
            registry["projects"] ??= new JsonArray();
            if (registry["projects"] is not JsonArray projects || projects.Any(item => item is not JsonObject))
            {
                throw new InvalidDataException("Registry projects must be an array of objects.");
            }

            return registry;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new JsonObject { ["version"] = 1, ["projects"] = new JsonArray() };
        }
    }

    public Task NoteInitAsync(ProjectIdentity project, IReadOnlyList<WorktreeIdentity> worktrees, string version, CancellationToken cancellationToken = default) =>
        NoteAsync(project, worktrees, version, "last_init", cancellationToken);

    public Task NoteMcpAsync(ProjectIdentity project, IReadOnlyList<WorktreeIdentity> worktrees, string version, CancellationToken cancellationToken = default) =>
        NoteAsync(project, worktrees, version, "last_mcp", cancellationToken);

    public Task NoteSyncAsync(ProjectIdentity project, IReadOnlyList<WorktreeIdentity> worktrees, string version, CancellationToken cancellationToken = default) =>
        NoteAsync(project, worktrees, version, "last_sync", cancellationToken);

    private Task<bool> NoteAsync(ProjectIdentity project, IReadOnlyList<WorktreeIdentity> worktrees, string version, string eventKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(worktrees);
        return Harness.Infrastructure.Workspaces.AtomicMachineFile.UpdateAsync(path, Load, registry =>
        {
            var projects = registry["projects"]!.AsArray();
            var entry = projects.OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<string>() == project.Id);
            var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            if (entry is null)
            {
                entry = new JsonObject { ["id"] = project.Id, ["first_seen"] = now };
                projects.Add(entry);
            }

            entry["root"] = project.Root;
            entry["db_path"] = project.DatabasePath;
            entry["schema_version"] = HarnessDatabase.LatestSchemaVersion;
            entry["harness_version"] = version;
            entry["worktrees"] = new JsonArray(worktrees.Select(tree => JsonValue.Create(tree.Path)).ToArray<JsonNode?>());
            entry[eventKey] = now;
            return true;
        }, cancellationToken);
    }

    public Task<bool> ForgetAsync(string id, CancellationToken token = default) => Harness.Infrastructure.Workspaces.AtomicMachineFile.UpdateAsync(path, Load, registry =>
    {
        var projects = registry["projects"]!.AsArray();
        var rows = projects.OfType<JsonObject>().Where(row => row["id"]?.GetValue<string>() == id).ToArray();
        foreach (var row in rows)
        {
            projects.Remove(row);
        }

        return rows.Length > 0;
    }, token);

    public async Task<JsonObject> RenameAsync(string id, string? name, string? title, CancellationToken token = default)
    {
        name = name?.Trim();
        if (name is { Length: > 0 } && !System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)))
        {
            throw new InvalidOperationException("short name " + JsonSerializer.Serialize(name) + ": use letters, digits, '.', '_' or '-' (max 64)");
        }

        title = Harness.Infrastructure.Workspaces.WorkspaceStore.NormalizeTitle(title);
        JsonObject? result = null;
        await Harness.Infrastructure.Workspaces.AtomicMachineFile.UpdateAsync(path, Load, registry =>
        {
            var row = registry["projects"]!.AsArray().OfType<JsonObject>().FirstOrDefault(row => row["id"]?.GetValue<string>() == id)
                ?? throw new InvalidOperationException("project " + id + " is not registered on this machine");
            if (name is not null)
            { if (name.Length is 0)
                {
                    row.Remove("name");
                }
                else
                {
                    row["name"] = name;
                }
            }
            if (title is not null)
            { if (title.Length is 0)
                {
                    row.Remove("title");
                }
                else
                {
                    row["title"] = title;
                }
            }
            result = (JsonObject)row.DeepClone();
            return true;
        }, token).ConfigureAwait(false);
        return result!;
    }
}
