using System.Globalization;
using System.Text;
using Harness.Core.Instructions;

namespace Harness.Infrastructure.Clients;

public sealed record FileChange(
    string Path,
    string Action,
    string Before,
    string After,
    string Reason = "",
    string Backup = "",
    bool Deleted = false
)
{
    public bool Writes => Action is "add" or "update" or "remove";
}

/// <summary>Bounded UTF-8 edits, optimistic conflict checks, sibling backups and atomic replacement.</summary>
public static class FileEditor
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public const int MaximumBytes = 16 * 1024 * 1024;

    public static string Resolve(string path)
    {
        var absolute = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(absolute)!;
        foreach (
            var part in absolute[root.Length..]
                .Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
        )
        {
            root = System.IO.Path.Combine(root, part);
            FileSystemInfo item = Directory.Exists(root) ? new DirectoryInfo(root) : new FileInfo(root);
            if (item.LinkTarget is not null)
            {
                root =
                    item.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("Cannot resolve link: " + root);
            }
        }
        return root;
    }

    public static string Read(string path) => Utf8.GetString(ReadBytes(Resolve(path)));

    private static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes)
        {
            throw new InvalidDataException("file exceeds the edit size limit");
        }

        using var output = new MemoryStream();
        var buffer = new byte[8192];
        for (var read = stream.Read(buffer); read > 0; read = stream.Read(buffer))
        {
            if (output.Length + read > MaximumBytes)
            {
                throw new InvalidDataException("file exceeds the edit size limit");
            }

            output.Write(buffer, 0, read);
        }
        if (output.Length > MaximumBytes)
        {
            throw new InvalidDataException("file exceeds the edit size limit");
        }

        return output.ToArray();
    }

    public static FileChange Apply(
        string path,
        Func<string, bool, TextEdit> edit,
        bool dryRun = false,
        bool backup = false,
        Func<string>? skipMissing = null,
        Func<string, bool>? empty = null
    )
    {
        ArgumentNullException.ThrowIfNull(edit);
        var real = Resolve(path);
        byte[]? before;
        try
        {
            before = ReadBytes(real);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            before = null;
        }
        if (before is null && skipMissing?.Invoke() is { Length: > 0 } reason)
        {
            return new(path, "skip", "", "", reason);
        }

        var text = before is null ? "" : Utf8.GetString(before);
        var result = edit(text, before is not null);
        var change = new FileChange(
            path,
            result.Action,
            text,
            result.Text,
            Deleted: result.Action is "remove" && (empty?.Invoke(result.Text) ?? string.IsNullOrWhiteSpace(result.Text))
        );
        if (!change.Writes)
        {
            return change;
        }

        var bytes = Utf8.GetBytes(result.Text);
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("edited file exceeds the edit size limit");
        }

        if (dryRun)
        {
            return change;
        }

        Check(real, before);
        if (backup && before is not null)
        {
            change = change with { Backup = Backup(real, before) };
        }

        Check(real, before);
        if (change.Deleted)
        {
            File.Delete(real);
            return change;
        }
        var directory = System.IO.Path.GetDirectoryName(real)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, ".harness-edit-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var mode =
                !OperatingSystem.IsWindows() && before is not null
                    ? File.GetUnixFileMode(real)
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            using (var stream = new FileStream(temporary, Options(mode)))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, mode);
            }

            Check(real, before);
            if (!string.Equals(Resolve(path), real, StringComparison.Ordinal))
            {
                throw new IOException("file link changed while harness was editing it; nothing was written, retry");
            }

            File.Move(temporary, real, overwrite: before is not null);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        return change;
    }

    private static void Check(string path, byte[]? expected)
    {
        byte[]? current;
        try
        {
            current = ReadBytes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            current = null;
        }
        if (expected is null ? current is not null : current is null || !current.AsSpan().SequenceEqual(expected))
        {
            throw new IOException("file changed while harness was editing it; nothing was written, retry");
        }
    }

    private static string Backup(string path, byte[] bytes)
    {
        var prefix =
            path + ".bak-harness-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss.fff", CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var name = attempt is 0 ? prefix : prefix + "-" + attempt.ToString(CultureInfo.InvariantCulture);
            FileStream stream;
            try
            {
                var mode = OperatingSystem.IsWindows() ? UnixFileMode.None : File.GetUnixFileMode(path);
                stream = new FileStream(name, Options(mode));
            }
            catch (IOException) when (File.Exists(name))
            {
                continue;
            }
            using (stream)
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(name, File.GetUnixFileMode(path));
                }

                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                return name;
            }
        }
        throw new IOException("cannot allocate a unique harness backup");
    }

    private static FileStreamOptions Options(UnixFileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = mode;
        }

        return options;
    }
}
