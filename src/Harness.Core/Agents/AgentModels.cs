using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Harness.Core.Agents;

/// <summary>Where a catalog entry came from.</summary>
public static class AgentModelSources
{
    /// <summary>The client's own list, from the handshake of a session harness started.</summary>
    public const string Session = "session";

    /// <summary>The client's own list, from a short-lived probe of its CLI by harness ui.</summary>
    public const string Probe = "probe";

    /// <summary>Claude Code's documented model aliases, when its initialize answer lists no models.</summary>
    public const string Alias = "alias";

    /// <summary>A model the client's transcripts recorded (agent_usage), which its own list does not name.</summary>
    public const string Seen = "seen";

    /// <summary>Entries the client itself reported (or its documented fallback), replaced as a whole by a refresh.</summary>
    public static bool FromClient(string source) => source is Session or Probe or Alias;
}

/// <summary>
/// One model an agent client offers: the id to pass on (Claude Code --model, the Codex thread's model), the label to
/// show, and what the client says about it. Resolved is the full model id a Claude Code alias stands for; Upgrade the
/// model Codex suggests instead; Hidden a Codex model kept out of its picker.
/// </summary>
public sealed record AgentModel(string Id, string Label)
{
    public string Description { get; init; } = "";
    public bool IsDefault { get; init; }
    public bool Hidden { get; init; }
    public string Resolved { get; init; } = "";
    public string Upgrade { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>This entry stands for <paramref name="id"/>: its own id or the full id its alias resolves to.</summary>
    public bool Names(string id) =>
        string.Equals(Id, id, StringComparison.Ordinal)
        || (Resolved.Length > 0 && string.Equals(Resolved, id, StringComparison.Ordinal));
}

/// <summary>What a client reported about its models: the list, the CLI's version when it said, and whether the list
/// is the documented fallback rather than the client's own answer.</summary>
public sealed record AgentModelReport(IReadOnlyList<AgentModel> Models)
{
    public string Version { get; init; } = "";
    public bool Fallback { get; init; }
}

/// <summary>
/// The models harness knows for one client (TASK-0142): the client's own list from its last successful refresh, with
/// the models its transcripts recorded that the list lacks ('seen'), the time of that refresh and, when a later one
/// failed, why. A failed refresh never changes the models.
/// </summary>
public sealed record AgentModelCatalog(string Client)
{
    /// <summary>How long a refresh stays current before harness ui probes the client again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public IReadOnlyList<AgentModel> Models { get; init; } = [];
    public DateTimeOffset? RefreshedAt { get; init; }
    public string Source { get; init; } = "";
    public string Version { get; init; } = "";
    public string Error { get; init; } = "";
    public DateTimeOffset? FailedAt { get; init; }

    /// <summary>The client's default model, when it named one.</summary>
    public AgentModel? Default => Models.FirstOrDefault(m => m.IsDefault);

    /// <summary>Never refreshed, or the last refresh is older than <see cref="MaxAge"/>.</summary>
    public bool IsStale(DateTimeOffset now) => RefreshedAt is not { } at || now - at > MaxAge;

    /// <summary>The catalog has a model with this id (or an alias resolving to it).</summary>
    public bool Contains(string id) => Models.Any(m => m.Names(id));
}

/// <summary>Merging what a client reports into its catalog, and the catalog's JSON form in agent-models.json.</summary>
public static partial class AgentModelCatalogs
{
    /// <summary>
    /// The catalog after a successful refresh: the reported models replace the client's earlier list in the order the
    /// client gave them, and 'seen' entries stay after them unless the list now names them. An empty report is a
    /// failed refresh.
    /// </summary>
    public static AgentModelCatalog Refreshed(
        AgentModelCatalog old,
        AgentModelReport report,
        string source,
        DateTimeOffset now
    )
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(report);
        var reported = report
            .Models.Where(m => m.Id.Length > 0)
            .DistinctBy(m => m.Id, StringComparer.Ordinal)
            .Select(m =>
                m with
                {
                    Label = m.Label.Length > 0 ? m.Label : m.Id,
                    Source = report.Fallback ? AgentModelSources.Alias : source,
                    LastSeen = now,
                }
            )
            .ToList();
        if (reported.Count is 0)
        {
            return Failed(old, "the client reported no models", now);
        }
        var seen = old.Models.Where(m => m.Source is AgentModelSources.Seen && !reported.Any(r => r.Names(m.Id)));
        return old with
        {
            Models = [.. reported, .. seen.OrderBy(m => m.Id, StringComparer.Ordinal)],
            RefreshedAt = now,
            Source = report.Fallback ? AgentModelSources.Alias : source,
            Version = report.Version.Length > 0 ? report.Version : old.Version,
            Error = "",
            FailedAt = null,
        };
    }

    /// <summary>The catalog after a failed refresh: the same models and refresh time, with the reason.</summary>
    public static AgentModelCatalog Failed(AgentModelCatalog old, string error, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(old);
        return old with { Error = error.Length > 0 ? error : "the refresh failed", FailedAt = now };
    }

    /// <summary>
    /// The catalog with the models a client's transcripts recorded: one the catalog does not name becomes a 'seen'
    /// entry, an existing 'seen' entry keeps its latest time. Ids that are not model ids (Claude's
    /// &lt;synthetic&gt;) are skipped. Changed says whether anything was added or moved on.
    /// </summary>
    public static (AgentModelCatalog Catalog, bool Changed) Seen(
        AgentModelCatalog old,
        IEnumerable<(string Id, DateTimeOffset At)> models
    )
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(models);
        var list = old.Models.ToList();
        var changed = false;
        foreach (var (id, at) in models)
        {
            if (!ModelId.IsMatch(id))
            {
                continue;
            }
            var index = list.FindIndex(m => m.Names(id));
            if (index < 0)
            {
                list.Add(new AgentModel(id, id) { Source = AgentModelSources.Seen, LastSeen = at });
                changed = true;
            }
            else if (list[index].Source is AgentModelSources.Seen && list[index].LastSeen < at)
            {
                list[index] = list[index] with { LastSeen = at };
                changed = true;
            }
        }
        if (!changed)
        {
            return (old, false);
        }
        var client = list.Where(m => m.Source is not AgentModelSources.Seen);
        var seen = list.Where(m => m.Source is AgentModelSources.Seen).OrderBy(m => m.Id, StringComparer.Ordinal);
        return (old with { Models = [.. client, .. seen] }, true);
    }

    /// <summary>A model id as the clients write them: letters, digits and . _ - : / [ ], no spaces or brackets.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/\[\]-]{0,127}$")]
    private static partial Regex ModelId { get; }

    /// <summary>The catalog as agent-models.json keeps it under its client.</summary>
    public static JsonObject ToJson(AgentModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var json = new JsonObject
        {
            ["refreshed_at"] = Time(catalog.RefreshedAt),
            ["source"] = catalog.Source,
            ["cli_version"] = catalog.Version,
            ["error"] = catalog.Error,
            ["failed_at"] = Time(catalog.FailedAt),
            ["models"] = new JsonArray([.. catalog.Models.Select(m => (JsonNode)Model(m))]),
        };
        return json;
    }

    private static JsonObject Model(AgentModel model)
    {
        var json = new JsonObject
        {
            ["id"] = model.Id,
            ["label"] = model.Label,
            ["default"] = model.IsDefault,
            ["source"] = model.Source,
            ["last_seen"] = Time(model.LastSeen),
        };
        Optional(json, "description", model.Description);
        Optional(json, "resolved", model.Resolved);
        Optional(json, "upgrade", model.Upgrade);
        if (model.Hidden)
        {
            json["hidden"] = true;
        }
        return json;
    }

    /// <summary>A client's catalog read back from agent-models.json; what cannot be read is left out.</summary>
    public static AgentModelCatalog FromJson(string client, JsonObject? json)
    {
        if (json is null)
        {
            return new(client);
        }
        List<AgentModel> models = [];
        foreach (var item in (json["models"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Text(item["id"]) is not { Length: > 0 } id)
            {
                continue;
            }
            models.Add(
                new AgentModel(id, Text(item["label"]) is { Length: > 0 } label ? label : id)
                {
                    Description = Text(item["description"]),
                    IsDefault = Flag(item["default"]),
                    Hidden = Flag(item["hidden"]),
                    Resolved = Text(item["resolved"]),
                    Upgrade = Text(item["upgrade"]),
                    Source = Text(item["source"]),
                    LastSeen = ParseTime(Text(item["last_seen"])) ?? default,
                }
            );
        }
        return new(client)
        {
            Models = models,
            RefreshedAt = ParseTime(Text(json["refreshed_at"])),
            Source = Text(json["source"]),
            Version = Text(json["cli_version"]),
            Error = Text(json["error"]),
            FailedAt = ParseTime(Text(json["failed_at"])),
        };
    }

    private static string Time(DateTimeOffset? at) =>
        at is { } value && value != default
            ? value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : "";

    private static DateTimeOffset? ParseTime(string text) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var at
        )
            ? at
            : null;

    private static void Optional(JsonObject json, string name, string value)
    {
        if (value.Length > 0)
        {
            json[name] = value;
        }
    }

    /// <summary>A JSON string value, or empty.</summary>
    public static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>A JSON true.</summary>
    public static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}

/// <summary>
/// How the clients tell their models (TASK-0142), checked against the installed CLIs. Codex: the app-server request
/// model/list (codex-cli 0.167.1) answers {data: [{id, model, displayName, description, hidden, isDefault, upgrade,
/// upgradeInfo}], nextCursor}. Claude Code: the stream-json control request initialize (Claude Code 2.1.318, what the
/// Agent SDK's supportedModels() reads) answers {models: [{value, resolvedModel, displayName, description}],
/// claude_code_version}, where the value "default" is the CLI's default and names the model it resolves to.
/// </summary>
public static class AgentModelProtocol
{
    /// <summary>The request id harness gives its initialize control request.</summary>
    public const string ClaudeInitializeId = "harness-initialize";

    /// <summary>How many model/list pages harness reads at most.</summary>
    public const int CodexPages = 10;

    /// <summary>
    /// Claude Code's documented model aliases, offered when its initialize answer has no models list (an older
    /// version): sonnet, opus and haiku. 'Default' is the start dialog's own first choice.
    /// </summary>
    public static IReadOnlyList<AgentModel> ClaudeAliases { get; } =
    [
        new("sonnet", "Sonnet") { Description = "the latest Sonnet model" },
        new("opus", "Opus") { Description = "the latest Opus model" },
        new("haiku", "Haiku") { Description = "the latest Haiku model" },
    ];

    /// <summary>The stream-json control request that asks Claude Code for its initialize answer.</summary>
    public static JsonObject ClaudeInitialize() =>
        new()
        {
            ["type"] = "control_request",
            ["request_id"] = ClaudeInitializeId,
            ["request"] = new JsonObject { ["subtype"] = "initialize" },
        };

    /// <summary>
    /// The models in Claude Code's control_response to initialize (the whole line, or its inner response). The
    /// "default" entry is not a model of its own: the model it resolves to becomes the default (added when the list
    /// lacks it). Without a models list the documented aliases are the (fallback) report; an error answer is null.
    /// </summary>
    public static AgentModelReport? Claude(JsonObject answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var outer = answer["response"] as JsonObject ?? answer;
        if (AgentModelCatalogs.Text(outer["subtype"]) is "error")
        {
            return null;
        }
        var inner = outer["response"] as JsonObject ?? outer;
        var version = AgentModelCatalogs.Text(inner["claude_code_version"]);
        if (inner["models"] is not JsonArray list)
        {
            return new(ClaudeAliases) { Version = version, Fallback = true };
        }
        List<AgentModel> models = [];
        var defaultResolved = "";
        var defaultDescription = "";
        foreach (var item in list.OfType<JsonObject>())
        {
            var id = AgentModelCatalogs.Text(item["value"]);
            var resolved = AgentModelCatalogs.Text(item["resolvedModel"]);
            if (id is "default")
            {
                defaultResolved = resolved;
                defaultDescription = AgentModelCatalogs.Text(item["description"]);
                continue;
            }
            if (id.Length is 0)
            {
                continue;
            }
            models.Add(
                new AgentModel(id, AgentModelCatalogs.Text(item["displayName"]))
                {
                    Description = AgentModelCatalogs.Text(item["description"]),
                    Resolved = resolved == id ? "" : resolved,
                }
            );
        }
        if (defaultResolved.Length > 0)
        {
            // the first alias for the default's model (opus before claude-opus-…), else the model itself
            var index = models.FindIndex(m => m.Names(defaultResolved));
            if (index >= 0)
            {
                models[index] = models[index] with { IsDefault = true };
            }
            else
            {
                var label = defaultDescription.Split(" · ", 2)[0].Trim();
                models.Insert(
                    0,
                    new AgentModel(defaultResolved, label.Length > 0 ? label : defaultResolved) { IsDefault = true }
                );
            }
        }
        return new(models) { Version = version };
    }

    /// <summary>The params of one model/list call: the page after <paramref name="cursor"/>.</summary>
    public static JsonObject CodexListParams(string cursor) =>
        cursor.Length > 0 ? new JsonObject { ["cursor"] = cursor } : [];

    /// <summary>
    /// One model/list result: its models (id is the model string a thread takes) and the cursor of the next page
    /// (empty for the last).
    /// </summary>
    public static (IReadOnlyList<AgentModel> Models, string Next) Codex(JsonObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<AgentModel> models = [];
        foreach (var item in (result["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = AgentModelCatalogs.Text(item["model"]) is { Length: > 0 } model
                ? model
                : AgentModelCatalogs.Text(item["id"]);
            if (id.Length is 0)
            {
                continue;
            }
            var upgrade = AgentModelCatalogs.Text(item["upgradeInfo"]?["model"]) is { Length: > 0 } next
                ? next
                : AgentModelCatalogs.Text(item["upgrade"]);
            models.Add(
                new AgentModel(id, AgentModelCatalogs.Text(item["displayName"]))
                {
                    Description = AgentModelCatalogs.Text(item["description"]),
                    IsDefault = AgentModelCatalogs.Flag(item["isDefault"]),
                    Hidden = AgentModelCatalogs.Flag(item["hidden"]),
                    Upgrade = upgrade,
                }
            );
        }
        return (models, AgentModelCatalogs.Text(result["nextCursor"]));
    }

    /// <summary>The Codex version in initialize's userAgent ("name/0.167.1 (…)"), or empty.</summary>
    public static string CodexVersion(JsonObject initialized)
    {
        ArgumentNullException.ThrowIfNull(initialized);
        var agent = AgentModelCatalogs.Text(initialized["userAgent"]);
        var slash = agent.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return "";
        }
        var rest = agent[(slash + 1)..];
        var end = rest.IndexOf(' ', StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }
}
