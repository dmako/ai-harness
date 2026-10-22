using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Workspaces;

internal static class AtomicMachineFile
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static JsonObject Load(string path, Func<JsonObject> empty)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path, Utf8))?.AsObject()
                ?? throw new InvalidDataException("Machine file is null.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return empty();
        }
    }

    internal static async Task<bool> UpdateAsync(
        string path,
        Func<JsonObject> load,
        Func<JsonObject, bool> edit,
        CancellationToken token
    )
    {
        StorageFiles.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var lease = await LockAsync(path + ".lock", token).ConfigureAwait(false);
        var before = File.Exists(path) ? await File.ReadAllBytesAsync(path, token).ConfigureAwait(false) : null;
        var value = load();
        if (!edit(value))
        {
            return false;
        }

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

            using (var file = new FileStream(temporary, options))
            {
                await file.WriteAsync(
                        Utf8.GetBytes(value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n"),
                        token
                    )
                    .ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            var current = File.Exists(path) ? await File.ReadAllBytesAsync(path, token).ConfigureAwait(false) : null;
            if (before is null ? current is not null : current is null || !before.AsSpan().SequenceEqual(current))
            {
                throw new IOException("Machine file changed while updating it; retry.");
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            StorageFiles.OwnerOnly(path);
            return true;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
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

                return new(path, options);
            }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
    }
}
