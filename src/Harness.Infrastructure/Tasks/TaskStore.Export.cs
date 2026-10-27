using System.Text;
using System.Text.RegularExpressions;
using Harness.Core.Tasks;

namespace Harness.Infrastructure.Tasks;

public sealed partial class TaskStore
{
    public TaskExportResult Export(string path, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var all = List(cancellationToken: cancellationToken);
        byte[]? original;
        try
        {
            original = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            original = null;
        }
        catch (DirectoryNotFoundException)
        {
            original = null;
        }
        var text = original is null ? "" : new UTF8Encoding(false, true).GetString(original);
        var present = original is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : BacklogCodec.Parse(text, path).Tasks.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var definitions = all.Where(task => !present.Contains(task.Id))
            .Select(task => task.ExportDefinition().Normalize())
            .ToArray();
        var result = new TaskExportResult(path, definitions.Select(d => d.Id).ToArray(), original is null);
        string content;
        if (original is null)
        {
            content = BacklogCodec.Write(
                new(
                    1,
                    definitions,
                    Milestones(cancellationToken)
                        .Where(m => m.Definition.Title.Length is not 0)
                        .Select(m => m.Definition)
                        .ToArray()
                )
            );
        }
        else
        {
            if (definitions.Length is 0 || dryRun)
            {
                return result;
            }

            var key = TasksKey.Match(text);
            if (!key.Success)
            {
                throw new InvalidDataException($"{path}: no top-level 'tasks:' list to append to");
            }

            var item = ListItem.Match(text, key.Index + key.Length);
            var indent = item.Success ? item.Groups[1].Value : "  ";
            content = key.Groups[1].Success ? text[..key.Index] + "tasks:" + text[(key.Index + key.Length)..] : text;
            if (!content.EndsWith('\n'))
            {
                content += "\n";
            }

            if (!content.EndsWith("\n\n", StringComparison.Ordinal))
            {
                content += "\n";
            }

            content += string.Concat(
                BacklogCodec
                    .WriteDefinitions(definitions)
                    .Split('\n')
                    .Select(line => line.Length is 0 ? "" : indent + line + "\n")
            );
            BacklogFile parsed;
            try
            {
                parsed = BacklogCodec.Parse(content, path);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"{path}: appending the tasks would break the file ({exception.Message}); is 'tasks:' its last section?",
                    exception
                );
            }
            if (parsed.Tasks.Count != present.Count + definitions.Length)
            {
                throw new InvalidDataException($"{path}: 'tasks:' must be the file's last section to append to it");
            }
        }
        if (dryRun)
        {
            return result;
        }

        cancellationToken.ThrowIfCancellationRequested();
        WriteAtomic(path, Encoding.UTF8.GetBytes(content), original);
        return result;
    }

    private static void WriteAtomic(string path, byte[] content, byte[]? expected)
    {
        var absolute = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(absolute)!;
        Directory.CreateDirectory(directory);
        var mode =
            !OperatingSystem.IsWindows() && expected is not null
                ? File.GetUnixFileMode(absolute)
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var temporary = Path.Combine(directory, ".harness-backlog-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, mode);
            }

            if (expected is not null && !File.ReadAllBytes(absolute).AsSpan().SequenceEqual(expected))
            {
                throw new IOException($"{path}: file changed while exporting; try again");
            }

            File.Move(temporary, absolute, overwrite: expected is not null);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    [GeneratedRegex(@"(?m)^tasks:[ \t]*(\[\])?[ \t]*\r?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TasksKey { get; }

    [GeneratedRegex(@"(?m)^([ \t]*)- ", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ListItem { get; }
}
