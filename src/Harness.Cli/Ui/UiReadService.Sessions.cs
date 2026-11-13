using System.Text.Json.Nodes;
using Harness.Core.Serialization;
using Harness.Core.Tasks;
using Harness.Core.Web;
using Harness.Infrastructure.Agents;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Tasks;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    /// <summary>
    /// A task's agent sessions with what each used (TASK-0114), read on from the transcripts first, and the task's
    /// totals: tokens, active time, API-equivalent price, and the wall time from its first claim to its completion.
    /// </summary>
    private JsonNode? TaskSessions(
        JsonObject project,
        HarnessDatabase database,
        TaskStore tasks,
        string id,
        CancellationToken token
    )
    {
        var refresh = "";
        try
        {
            var root = UiProjectCatalog.Text(project, "root");
            var configuration = ConfigurationLoader
                .LoadFiles(lifecycle.GlobalConfigurationPath, ConfigurationPaths.ProjectPath(root))
                .Value;
            AgentUsageRefresh.Refresh(
                database,
                ConfigurationLoader.Prices(configuration),
                TranscriptRoots.Default(),
                id,
                token
            );
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or Microsoft.Data.Sqlite.SqliteException
            )
        { /* The usage read before stays shown; the page says it could not be brought up to date. */
            refresh = error.Message.Split('\n')[0];
        }

        var worked = tasks.Sessions(id, token);
        var events = tasks.Events(id, token);
        var claimed = events.FirstOrDefault(e => e.Event is "claimed")?.At;
        var completed = events.LastOrDefault(e => e.Event is "completed")?.At;
        JsonObjectMap totals = new()
        {
            ["input_tokens"] = worked.Sum(s => s.InputTokens),
            ["cache_read_tokens"] = worked.Sum(s => s.CacheReadTokens),
            ["cache_write_tokens"] = worked.Sum(s => s.CacheWriteTokens),
            ["output_tokens"] = worked.Sum(s => s.OutputTokens),
            ["reasoning_tokens"] = worked.Sum(s => s.ReasoningTokens),
            ["active_ms"] = worked.Sum(s => s.ActiveMs),
        };
        // a price only when every session that used tokens has one
        if (worked.Any(s => s.CostUsd is not null) && worked.All(s => s.CostUsd is not null || Tokens(s) is 0))
        {
            totals["cost_usd"] = worked.Sum(s => s.CostUsd ?? 0);
        }
        // the agent hosts started for the task from harness ui, live or ended (TASK-0116)
        var hosts = agents?.Hosts(project, id) ?? [];
        MarkUnlistedModels(hosts);
        JsonObjectMap output = new()
        {
            ["sessions"] = worked.Select(TaskViews.Session),
            ["hosts"] = hosts,
            // what the start dialog offers per client (TASK-0135)
            ["permission_modes"] = UiAgents.PermissionModes(),
            ["totals"] = totals,
            ["price_basis"] = "API-equivalent",
        };
        if (claimed is { } from)
        {
            output["claimed_at"] = from.ToRfc3339();
        }
        if (completed is { } to)
        {
            output["completed_at"] = to.ToRfc3339();
        }
        if (refresh.Length > 0)
        {
            output["refresh_error"] = refresh;
        }
        return UiJson.Node(output);
    }

    private static long Tokens(AgentSession s) =>
        s.InputTokens + s.CacheReadTokens + s.CacheWriteTokens + s.OutputTokens;
}
