using System.Globalization;

namespace Harness.Infrastructure.Git;

/// <summary>
/// The commits since a date: how often each file changed and, per commit, the files it changed together, both under
/// the files' current names (a renamed file's earlier commits count for its new name).
/// </summary>
public sealed record GitChurn(IReadOnlyDictionary<string, int> Counts, int Commits)
{
    public IReadOnlyList<IReadOnlyList<string>> Changes { get; init; } = [];
}

public sealed partial record GitRepository
{
    public async Task<GitChurn> ChurnAsync(DateTimeOffset since, CancellationToken token)
    {
        var result = await RunAsync(
                Root,
                [
                    "-c",
                    "core.quotePath=false",
                    "log",
                    "--no-merges",
                    "--since=" + since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    "--format=%x1e",
                    "--name-status",
                    "-M",
                ],
                token
            )
            .ConfigureAwait(false);
        if (result.Code is not 0)
        {
            throw new IOException("read Git churn: " + result.Error.Trim());
        }

        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        List<IReadOnlyList<string>> changes = [];
        // newest first: a rename maps the older name to the one the file has now
        Dictionary<string, string> renamed = new(StringComparer.Ordinal);
        foreach (var record in result.Output.Split('\u001e'))
        {
            HashSet<string> files = new(StringComparer.Ordinal);
            List<(string Old, string Current)> renames = [];
            foreach (var line in record.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0))
            {
                var fields = line.Split('\t');
                if (fields.Length < 2)
                {
                    continue;
                }

                var path = fields[^1];
                var current = renamed.GetValueOrDefault(path, path);
                files.Add(current);
                if (fields[0].StartsWith('R') && fields.Length is 3)
                {
                    renames.Add((fields[1], current));
                }
            }

            foreach (var (old, current) in renames)
            {
                renamed[old] = current;
            }

            if (files.Count is 0)
            {
                continue;
            }

            foreach (var file in files)
            {
                counts[file] = counts.GetValueOrDefault(file) + 1;
            }

            changes.Add(files.Order(StringComparer.Ordinal).ToArray());
        }

        return new(counts, changes.Count) { Changes = changes };
    }
}
