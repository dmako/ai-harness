using System.Text.Json;
using Harness.Core.Providers;
using Harness.Infrastructure.Git;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Projects;

public sealed partial class HarnessStateSession
{
    public async Task<ProviderProjectSnapshot> ReadProviderProjectAsync(CancellationToken cancellationToken)
    {
        var snapshot = new ProviderProjectSnapshot(location.Root);
        GitHead? head = null;
        if (location.Repository is { } repository)
        {
            try
            {
                head = await repository.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
                snapshot = snapshot with { Branch = head.Branch, Commit = head.Commit };
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            { /* Overview retains available identity when Git cannot report HEAD. */
            }
        }
        if (!File.Exists(location.DatabasePath))
        {
            return snapshot;
        }

        try
        {
            var database = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
            if (_registration is { } registration)
            {
                var sync = ReadSync(
                    database,
                    registration.Worktree.Id,
                    head,
                    out var crossBranch,
                    includeRecentRuns: false
                );
                snapshot = snapshot with
                {
                    ProjectId = registration.Project.Id,
                    Stale = sync.GetValueOrDefault("stale") as string ?? "",
                    CrossBranch = crossBranch,
                };
            }
        }
        catch (Exception error)
            when (error is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
        { /* Reading a facade must not initialize or require project state. */
        }
        return snapshot;
    }

    public async Task<IReadOnlyDictionary<string, JsonElement>> ReadProviderStatisticsAsync(
        string provider,
        CancellationToken cancellationToken
    )
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!File.Exists(location.DatabasePath))
        {
            return result;
        }

        try
        {
            var database = await DatabaseAsync(cancellationToken).ConfigureAwait(false);
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*), COUNT(error), AVG(duration_ms), AVG(response_bytes), MAX(at) FROM provider_calls WHERE provider=$provider AND at >= $since";
            command.Parameters.AddWithValue("$provider", provider);
            command.Parameters.AddWithValue(
                "$since",
                DateTimeOffset
                    .UtcNow.AddDays(-1)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
            );
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var stats = new Dictionary<string, object?>
                {
                    ["calls"] = reader.GetInt32(0),
                    ["errors"] = reader.GetInt32(1),
                    ["avg_ms"] = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                    ["avg_bytes"] = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                };
                if (!reader.IsDBNull(4))
                {
                    stats["last_call_at"] = reader.GetString(4);
                }

                result["recent_24h"] = JsonSerializer.SerializeToElement(stats);
            }
            command.Parameters.Clear();
            command.CommandText = """
                SELECT COUNT(DISTINCT CASE WHEN EXISTS (SELECT 1 FROM context_units h WHERE h.call_id=o.call_id AND h.provider=$provider AND h.path=o.path) THEN CAST(o.call_id AS TEXT)||':'||o.path END), COUNT(DISTINCT CAST(o.call_id AS TEXT)||':'||o.path)
                FROM context_outcomes o JOIN context_calls c ON c.id=o.call_id
                WHERE c.at >= $since AND EXISTS (SELECT 1 FROM context_units u WHERE u.call_id=c.id AND u.provider=$provider)
                """;
            command.Parameters.AddWithValue("$provider", provider);
            command.Parameters.AddWithValue(
                "$since",
                DateTimeOffset
                    .UtcNow.AddDays(-30)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
            );
            using var hits = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await hits.ReadAsync(cancellationToken).ConfigureAwait(false);
            var changed = hits.GetInt32(1);
            if (changed > 0)
            {
                result["context_30d"] = JsonSerializer.SerializeToElement(
                    new
                    {
                        hits = hits.GetInt32(0),
                        changed,
                        hit_rate = Math.Truncate((double)hits.GetInt32(0) / changed * 100) / 100,
                    }
                );
            }
        }
        catch (Exception error)
            when (error is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
        { /* Optional observations must not prevent health diagnostics. */
        }
        return result;
    }
}
