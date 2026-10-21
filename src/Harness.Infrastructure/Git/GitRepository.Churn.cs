using System.Globalization;

namespace Harness.Infrastructure.Git;

public sealed record GitChurn(IReadOnlyDictionary<string, int> Counts, int Commits);

public sealed partial record GitRepository
{
    public async Task<GitChurn> ChurnAsync(DateTimeOffset since, CancellationToken token)
    {
        var result = await RunAsync(Root, ["log", "--no-merges", "--since=" + since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), "--format=%x1e", "--name-only"], token).ConfigureAwait(false);
        if (result.Code is not 0)
            throw new IOException("read Git churn: " + result.Error.Trim());
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        var commits = 0;
        foreach (var record in result.Output.Split('\u001e'))
        {
            var seen = false;
            foreach (var path in record.Split('\n').Select(path => path.Trim()).Where(path => path.Length > 0))
            { counts[path] = counts.GetValueOrDefault(path) + 1; seen = true; }
            if (seen)
                commits++;
        }
        return new(counts, commits);
    }
}
