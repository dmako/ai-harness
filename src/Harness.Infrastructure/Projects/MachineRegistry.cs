using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using Harness.Core.Projects;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Projects;

public sealed class MachineRegistry(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
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

    public async Task NoteInitAsync(ProjectIdentity project, IReadOnlyList<WorktreeIdentity> worktrees, string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(worktrees);
        StorageFiles.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var lease = await LockAsync(cancellationToken);
        var before = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
        var registry = Load();
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
        entry["last_init"] = now;
        // Preserve names, clients, workspaces and future fields written by other versions.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var file = new FileStream(temporary, options))
            {
                var bytes = StrictUtf8.GetBytes(registry.ToJsonString(JsonOptions) + "\n");
                await file.WriteAsync(bytes, cancellationToken);
                file.Flush(flushToDisk: true);
            }

            var current = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
            if (before is null ? current is not null : current is null || !before.AsSpan().SequenceEqual(current))
            {
                throw new IOException("Machine registry changed while updating it; retry.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            StorageFiles.OwnerOnly(path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                return new FileStream(path + ".lock", options);
            }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }
}
