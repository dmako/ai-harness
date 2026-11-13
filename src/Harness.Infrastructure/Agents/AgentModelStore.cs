using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Agents;
using Harness.Infrastructure.Workspaces;

namespace Harness.Infrastructure.Agents;

/// <summary>
/// The machine-wide model catalog per agent client (TASK-0142), agent-models.json beside the global configuration:
/// {version, clients: {client: catalog}}. Every project and browser shares it; harness-agents serve writes a session's
/// handshake list into it, harness ui its probes and the models the projects' transcripts recorded. Writes go through
/// the machine file's lock and atomic replace; a file that cannot be read counts as empty and is replaced by the next
/// write.
/// </summary>
public sealed class AgentModelStore(string path)
{
    public const string FileName = "agent-models.json";

    /// <summary>The catalog file beside this global configuration file.</summary>
    public static string PathFor(string globalConfigurationPath) =>
        System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(globalConfigurationPath))!,
            FileName
        );

    public string Path { get; } = path;

    /// <summary>The client's catalog; empty when the file or the client is missing or unreadable.</summary>
    public AgentModelCatalog Read(string client) =>
        AgentModelCatalogs.FromJson(client, Load()["clients"]?[client] as JsonObject);

    /// <summary>Records a successful refresh (a session's handshake or a probe).</summary>
    public Task<bool> RefreshedAsync(
        string client,
        AgentModelReport report,
        string source,
        DateTimeOffset now,
        CancellationToken token = default
    ) => UpdateAsync(client, old => (AgentModelCatalogs.Refreshed(old, report, source, now), true), token);

    /// <summary>Records a failed refresh: the models stay as they were.</summary>
    public Task<bool> FailedAsync(string client, string error, DateTimeOffset now, CancellationToken token = default) =>
        UpdateAsync(client, old => (AgentModelCatalogs.Failed(old, error, now), true), token);

    /// <summary>Adds the models the client's transcripts recorded; writes only when something is new.</summary>
    public Task<bool> SeenAsync(
        string client,
        IReadOnlyList<(string Id, DateTimeOffset At)> models,
        CancellationToken token = default
    ) =>
        models.Count is 0
            ? Task.FromResult(false)
            : UpdateAsync(client, old => AgentModelCatalogs.Seen(old, models), token);

    private Task<bool> UpdateAsync(
        string client,
        Func<AgentModelCatalog, (AgentModelCatalog Catalog, bool Changed)> change,
        CancellationToken token
    ) =>
        AtomicMachineFile.UpdateAsync(
            Path,
            Load,
            all =>
            {
                var clients = all["clients"] as JsonObject;
                if (clients is null)
                {
                    clients = [];
                    all["clients"] = clients;
                }
                var (catalog, changed) = change(AgentModelCatalogs.FromJson(client, clients[client] as JsonObject));
                if (changed)
                {
                    clients[client] = AgentModelCatalogs.ToJson(catalog);
                }
                return changed;
            },
            token
        );

    private JsonObject Load()
    {
        try
        {
            return AtomicMachineFile.Load(Path, Empty);
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or InvalidDataException
                        or InvalidOperationException
                        or DecoderFallbackException
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            return Empty();
        }
    }

    private static JsonObject Empty() => new() { ["version"] = 1, ["clients"] = new JsonObject() };
}
