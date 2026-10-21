using System.Globalization;
using Harness.Core.Retrieval;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Retrieval;

namespace Harness.Infrastructure.Projects;

public sealed partial class HarnessStateSession
{
    public async Task<ContextProject> ReadContextProjectAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ReadProviderProjectAsync(cancellationToken).ConfigureAwait(false);
        List<string> notes = [];
        var sources = new RetrievalSources(Branch: snapshot.Branch);
        if (location.Repository is { } repository)
            sources = sources with { RecentChanges = repository.RecentCommitsAsync };
        if (File.Exists(location.DatabasePath))
        {
            try
            {
                var database = await ExistingMemoryDatabaseAsync(cancellationToken).ConfigureAwait(false);
                var pending = new MemoryStore(database).Pending(cancellationToken);
                if (pending.Count > 0 && pending.Oldest is { } oldest)
                {
                    var age = DateTimeOffset.UtcNow - DateTimeOffset.Parse(oldest.ToRfc3339Nano(), CultureInfo.InvariantCulture);
                    var elapsed = age.TotalHours < 24 ? ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + " hour(s)" : ((int)age.TotalDays).ToString(CultureInfo.InvariantCulture) + " day(s)";
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"tell the user: {pending.Count} memory proposal(s) wait for review, the oldest for {elapsed} (harness proposals list, or the Proposals tab of harness ui)"));
                }
                sources = sources with { Memory = Knowledge.SearchContextAsync };
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            { notes.Add("no project memory (run harness init to record decisions)"); }
        }
        else
            notes.Add("no project memory (run harness init to record decisions)");
        var reader = new RetrievalFileReader(location.Root);
        return new(sources, reader.ReadBodyAsync, path => Directory.Exists(Path.Combine(location.Root, path)), snapshot.Stale, snapshot.CrossBranch, notes);
    }
}
