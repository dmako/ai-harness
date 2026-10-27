using System.Globalization;
using System.Text.Json.Nodes;
using Harness.Core.Retrieval;
using Harness.Core.Sync;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Sync;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Workspaces;

/// <summary>Machine overview reads existing state without starting providers or migrating databases.</summary>
public static class ProjectOverview
{
    public static async Task<JsonObject> InspectAsync(
        ProjectLifecycle lifecycle,
        JsonObject project,
        CancellationToken token
    )
    {
        var root = project["root"]?.GetValue<string>() ?? "";
        var live = new JsonObject
        {
            ["state"] = "missing",
            ["context_answers_30d"] = 0,
            ["context_hit_rate_30d"] = 0d,
            ["pending_proposals"] = 0,
            ["pending_arch_proposals"] = 0,
            ["pending_task_proposals"] = 0,
            ["pending_blueprint_proposals"] = 0,
            ["checks_failed"] = 0,
            ["checks_warned"] = 0,
        };
        if (!Directory.Exists(root))
        {
            live["detail"] =
                "repository not found at "
                + root
                + " (moved or deleted? 'harness projects forget "
                + project["id"]?.GetValue<string>()
                + "')";
            return live;
        }
        var inspection = await lifecycle.InspectAsync(root, token).ConfigureAwait(false);
        live["schema"] = inspection.SchemaVersion;
        live["schema_latest"] = HarnessDatabase.LatestSchemaVersion;
        var checks = inspection.Checks(lifecycle.Version);
        live["checks_failed"] = checks.Count(check => check.Level is "fail");
        live["checks_warned"] = checks.Count(check => check.Level is "warn");
        var (state, detail) = State(inspection);
        live["state"] = state;
        if (detail.Length > 0)
        {
            live["detail"] = detail;
        }

        if (inspection.Head is { } head)
        {
            AddHead(live, head);
        }
        if (state is not "ok")
        {
            return live;
        }

        try
        {
            await ObserveAsync(live, inspection, token).ConfigureAwait(false);
        }
        catch (Exception error)
            when (error is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException)
        { /* Preserve the independent checks if optional observations are unavailable. */
        }
        return live;
    }

    private static (string State, string Detail) State(ProjectInspection inspection) =>
        inspection switch
        {
            { GitError: { } error } => ("error", error),
            { Location.Initialized: false } => ("not_initialized", "no project database (run 'harness init')"),
            { DatabaseError: { } error } => ("error", error),
            { SchemaVersion: var version } when version < HarnessDatabase.LatestSchemaVersion => (
                "schema_outdated",
                "migrations pending (run 'harness sync')"
            ),
            _ => ("ok", ""),
        };

    private static void AddHead(JsonObject live, GitHead head)
    {
        if (head.Branch.Length > 0)
        {
            live["branch"] = head.Branch;
        }

        if (head.Commit.Length > 0)
        {
            live["commit"] = head.Commit;
        }
    }

    /// <summary>The database's pending proposals, context scores and sync freshness.</summary>
    private static async Task ObserveAsync(JsonObject live, ProjectInspection inspection, CancellationToken token)
    {
        using var database = HarnessDatabase.OpenReadOnly(inspection.Location.DatabasePath);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        await CountProposalsAsync(command, live, token).ConfigureAwait(false);
        await ScoreContextAsync(command, live, token).ConfigureAwait(false);
        AddSync(live, database, inspection);
    }

    private static async Task CountProposalsAsync(SqliteCommand command, JsonObject live, CancellationToken token)
    {
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM memory_proposals WHERE status='pending'),
                   (SELECT COUNT(*) FROM finding_proposals WHERE status='pending'),
                   (SELECT COUNT(*) FROM task_proposals WHERE status='pending'),
                   (SELECT COUNT(*) FROM blueprint_proposals WHERE status='pending')
            """;
        using var counts = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        await counts.ReadAsync(token).ConfigureAwait(false);
        live["pending_proposals"] = counts.GetInt32(0);
        live["pending_arch_proposals"] = counts.GetInt32(1);
        live["pending_task_proposals"] = counts.GetInt32(2);
        live["pending_blueprint_proposals"] = counts.GetInt32(3);
    }

    /// <summary>The last 30 days' context answers and their mean hit rate over changed non-test files.</summary>
    private static async Task ScoreContextAsync(SqliteCommand command, JsonObject live, CancellationToken token)
    {
        command.CommandText = """
            SELECT o.call_id, o.path, o.hit FROM context_outcomes o
            JOIN context_calls c ON c.id=o.call_id WHERE c.at >= $since
            """;
        command.Parameters.AddWithValue(
            "$since",
            DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        );
        Dictionary<long, (int Hits, int Changed)> answers = [];
        using (var scores = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await scores.ReadAsync(token).ConfigureAwait(false))
            {
                var id = scores.GetInt64(0);
                var score = answers.GetValueOrDefault(id);
                if (!TaskParsing.IsTestFile(scores.GetString(1)))
                {
                    score = (score.Hits + scores.GetInt32(2), score.Changed + 1);
                }

                answers[id] = score;
            }
        }
        live["context_answers_30d"] = answers.Count;
        var scored = answers.Values.Where(answer => answer.Changed > 0).ToArray();
        live["context_hit_rate_30d"] = scored.Length is 0
            ? 0
            : scored.Average(answer => (double)answer.Hits / answer.Changed);
    }

    private static void AddSync(JsonObject live, HarnessDatabase database, ProjectInspection inspection)
    {
        var worktree = inspection.Worktrees.FirstOrDefault(tree => tree.Path == inspection.Location.Root);
        var sync = worktree is null ? null : SyncStore.LoadState(database, worktree.Id);
        if (sync is not null)
        {
            live["synced_at"] = sync.SyncedAt;
        }

        var stale = SyncFreshness.Staleness(
            sync,
            inspection.Head?.Branch ?? "",
            inspection.Head?.Commit ?? "",
            inspection.Head?.Detached ?? false
        );
        if (stale.Length > 0)
        {
            live["stale"] = stale;
        }
    }
}
