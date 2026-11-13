using System.Text.Json.Nodes;
using Harness.Core.Agents;
using Harness.Core.Web;
using Harness.Infrastructure.Agents;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    /// <summary>How long after a failed probe the dialog may start another.</summary>
    private static readonly TimeSpan ProbeRetry = TimeSpan.FromMinutes(5);

    private AgentModelStore ModelStore => new(AgentModelStore.PathFor(lifecycle.GlobalConfigurationPath));

    /// <summary>
    /// Each client's model catalog for the start dialog (TASK-0142): its models (hidden ones left out), its default,
    /// the age of its last refresh and whether one is running or failed. The project's transcripts add the models
    /// they recorded first; a catalog that is empty or older than 24 h starts a background probe of its CLI, which
    /// the answer never waits for: the dialog asks again while 'refreshing' is true. Only a request with the
    /// X-Harness-UI header starts a probe, as probing starts the client's CLI.
    /// </summary>
    private async Task<JsonNode?> AgentModelsAsync(JsonObject project, UiReadRequest request, CancellationToken token)
    {
        var store = ModelStore;
        await SeenModelsAsync(project, store, token);
        var now = DateTimeOffset.UtcNow;
        var clients = new JsonObject();
        foreach (var client in AgentPermissionModes.Clients)
        {
            var known = store.Read(client);
            if (
                agents is not null
                && request.Trusted
                && !UiAgents.ModelsOffline
                && known.IsStale(now)
                && (known.FailedAt is not { } failed || now - failed > ProbeRetry)
            )
            {
                agents.RefreshModels(client, store, GlobalDirectory);
            }
            clients[client] = CatalogJson(known, now, agents?.RefreshingModels(client) ?? false);
        }
        return new JsonObject { ["clients"] = clients, ["max_age_hours"] = AgentModelCatalog.MaxAge.TotalHours };
    }

    /// <summary>The models the project's transcripts recorded, as 'seen' entries; a project not readable adds none.</summary>
    private static async Task SeenModelsAsync(JsonObject project, AgentModelStore store, CancellationToken token)
    {
        IReadOnlyList<(string Client, string Model, DateTimeOffset LastUsed)> used;
        try
        {
            using var database = Open(project);
            used = AgentUsageRefresh.UsedModels(database, token);
        }
        catch (Exception exception) when (exception is UiRequestException || ReadError(exception))
        {
            return;
        }
        foreach (var group in used.GroupBy(u => u.Client, StringComparer.Ordinal))
        {
            try
            {
                await store.SeenAsync(group.Key, [.. group.Select(u => (u.Model, u.LastUsed))], token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The catalog stays as it was; the dialog still gets it.
            }
        }
    }

    private static JsonObject CatalogJson(AgentModelCatalog known, DateTimeOffset now, bool refreshing)
    {
        var shown = known.Models.Where(m => !m.Hidden).ToList();
        var json = AgentModelCatalogs.ToJson(known with { Models = shown });
        json["default"] = known.Default is { } model
            ? new JsonObject { ["id"] = model.Id, ["label"] = model.Label }
            : null;
        json["age_seconds"] = known.RefreshedAt is { } at ? (long)(now - at).TotalSeconds : null;
        json["stale"] = known.IsStale(now);
        json["refreshing"] = refreshing;
        return json;
    }

    /// <summary>
    /// Marks the hosts started with a model the client's catalog does not name ("model_unlisted"): allowed, as the
    /// CLI and scripts may pass any id, but the session view says so. A client without a catalog marks nothing.
    /// </summary>
    private void MarkUnlistedModels(IEnumerable<JsonObject> hosts)
    {
        Dictionary<string, AgentModelCatalog> catalogs = new(StringComparer.Ordinal);
        var store = ModelStore;
        foreach (var host in hosts)
        {
            var requested = AgentHostFiles.Text(host, "requested_model");
            var client = AgentHostFiles.Text(host, "client");
            if (requested.Length is 0)
            {
                continue;
            }
            if (!catalogs.TryGetValue(client, out var known))
            {
                known = store.Read(client);
                catalogs[client] = known;
            }
            if (known.Models.Count > 0 && !known.Contains(requested))
            {
                host["model_unlisted"] = true;
            }
        }
    }
}
