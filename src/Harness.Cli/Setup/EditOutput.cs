using Harness.Infrastructure.Clients;

namespace Harness.Cli.Setup;

internal sealed record EditWords(string Thing, string Unchanged, string Absent)
{
    public static EditWords Server { get; } =
        new("harness server", "already configured, unchanged", "not configured, nothing to remove");
    public static EditWords Instructions { get; } = new("harness section", "up to date", "no harness section");
}

internal static class EditOutput
{
    public static async Task PrintAsync(
        TextWriter output,
        string label,
        EditWords words,
        FileChange change,
        bool dryRun
    )
    {
        var message = change.Action switch
        {
            "add" => "add " + words.Thing,
            "update" => "update " + words.Thing,
            "remove" => "remove " + words.Thing,
            "unchanged" => words.Unchanged,
            "absent" => words.Absent,
            "skip" => "skipped: " + change.Reason,
            _ => "refused: " + change.Reason,
        };
        if (dryRun && change.Writes)
        {
            message = "would " + message;
        }

        if (change.Deleted)
        {
            message += " (file deleted: nothing else was in it)";
        }

        await output.WriteLineAsync(label + ": " + message);
        if (!change.Writes)
        {
            return;
        }

        foreach (var line in Diff(change.Before, change.After))
        {
            await output.WriteLineAsync("    " + line);
        }

        if (change.Backup.Length > 0)
        {
            await output.WriteLineAsync("    backup: " + change.Backup);
        }
    }

    internal static IEnumerable<string> Diff(string before, string after)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
        {
            suffix++;
        }

        return a[prefix..(a.Length - suffix)]
            .Select(s => "- " + s)
            .Concat(b[prefix..(b.Length - suffix)].Select(s => "+ " + s));
    }
}
