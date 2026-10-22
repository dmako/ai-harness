using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Graphify;

/// <summary>Cross-process read/modify/atomic-replace session observations.</summary>
internal sealed class GraphifyRecords(string path)
{
    internal JsonObject Load()
    {
        JsonObject data;
        try
        {
            data = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            data = new();
        }
        foreach (var name in new[] { "sessions", "starting", "load_ms" }.Where(name => data[name] is not JsonObject))
        {
            data[name] = new JsonObject();
        }

        return data;
    }

    internal async Task UpdateAsync(Action<JsonObject> change, CancellationToken token)
    {
        try
        {
            StorageFiles.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using var lease = await LockAsync(path + ".lock", TimeSpan.FromSeconds(2), token)
                .ConfigureAwait(false);
            var before = File.Exists(path) ? await File.ReadAllBytesAsync(path, token).ConfigureAwait(false) : null;
            var data = Load();
            change(data);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                await using (var file = new FileStream(temporary, options))
                {
                    await file.WriteAsync(
                            System.Text.Encoding.UTF8.GetBytes(
                                data.ToJsonString(new() { WriteIndented = true }) + "\n"
                            ),
                            token
                        )
                        .ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                }
                var current = File.Exists(path)
                    ? await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)
                    : null;
                if (before is null ? current is not null : current is null || !before.AsSpan().SequenceEqual(current))
                {
                    return;
                }

                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
                StorageFiles.OwnerOnly(path);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { /* Observations must not prevent queries. */
        }
    }

    internal static async Task<FileStream> LockAsync(string path, TimeSpan timeout, CancellationToken token)
    {
        var began = Stopwatch.GetTimestamp();
        StorageFiles.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                return new FileStream(path, options);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(began) < timeout)
            {
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }
}
