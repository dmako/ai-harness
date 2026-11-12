using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Harness.Core.Agents;
using Harness.Core.Web;
using Harness.Infrastructure.Agents;

namespace Harness.Cli.Ui;

/// <summary>
/// The agent hosts harness ui started or found (TASK-0116): it starts them detached (harness-agents serve), keeps a
/// connection to each live one's pipe, and holds their events, numbered by the host, for the page: the whole log on
/// request and, through <see cref="Drain"/>, what is new for the live stream. A host outlives harness ui; a new
/// harness ui finds it again by its state file and reconnects, getting its events from the start.
/// </summary>
internal sealed class UiAgents : IAsyncDisposable
{
    private static readonly TimeSpan Connect = TimeSpan.FromSeconds(5);

    private sealed class Host(string project, string directory, string key)
    {
        public string Project { get; } = project;
        public string Directory { get; } = directory;
        public string Key { get; } = key;
        public object Gate { get; } = new();
        public List<JsonObject> Events { get; } = [];
        public long Drained { get; set; }
        public AgentHostClient? Client { get; set; }
        public Task Reading { get; set; } = Task.CompletedTask;
        public bool Connecting { get; set; }
        public Task Connected { get; set; } = Task.CompletedTask;

        public long Last => Events.Count is 0 ? 0 : AgentHostFiles.Sequence(Events[^1]);
    }

    private readonly ConcurrentDictionary<string, Host> _hosts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Starts a host for a task; returns its key at once, while the host opens the client's session. An empty
    /// permission mode is the client's default, or for a resumed session the mode it was started with.
    /// </summary>
    public async Task<string> StartAsync(
        JsonObject project,
        string taskId,
        string client,
        string prompt,
        string model,
        string resume,
        string mode = ""
    )
    {
        if (client is not (AgentClients.Claude or AgentClients.Codex))
        {
            throw new UiRequestException(400, "client must be claude-code or codex");
        }
        if (mode.Length > 0 && AgentPermissionModes.Find(client, mode) is null)
        {
            throw new UiRequestException(400, AgentPermissionModes.Problem(client, mode));
        }
        if (resume.Length is 0 && prompt.Trim().Length is 0)
        {
            throw new UiRequestException(400, "give the agent a prompt");
        }
        var root = UiProjectCatalog.Text(project, "root");
        var database = UiProjectCatalog.Text(project, "db_path");
        var directory = AgentHostFiles.Directory(database);
        var key = AgentHostFiles.NewKey();
        List<string> arguments =
        [
            "serve",
            "--client",
            client,
            "--task",
            taskId,
            "--cwd",
            root,
            "--db",
            database,
            "--key",
            key,
            "--state",
            directory,
        ];
        if (prompt.Trim().Length > 0)
        {
            arguments.AddRange(["--prompt", prompt]);
        }
        if (model.Length > 0)
        {
            arguments.AddRange(["--model", model]);
        }
        if (resume.Length > 0)
        {
            arguments.AddRange(["--resume", resume]);
        }
        if (mode.Length > 0)
        {
            arguments.AddRange(["--permission-mode", mode]);
        }
        // HARNESS_CLAUDE_COMMAND / HARNESS_CODEX_COMMAND: a client that is not on PATH as claude or codex
        var variable = client is AgentClients.Codex ? "HARNESS_CODEX_COMMAND" : "HARNESS_CLAUDE_COMMAND";
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } command)
        {
            arguments.AddRange(["--command", command]);
        }
        AgentHostLauncher.Start(arguments, root, Path.Combine(directory, key + ".err"));
        await Track(UiProjectCatalog.Text(project, "id"), directory, key).Connected;
        return key;
    }

    /// <summary>The hosts of a task, the newest first, each with whether it still runs; live ones get connected.</summary>
    public IReadOnlyList<JsonObject> Hosts(JsonObject project, string taskId)
    {
        var directory = AgentHostFiles.Directory(UiProjectCatalog.Text(project, "db_path"));
        List<JsonObject> hosts = [];
        foreach (var state in AgentHostFiles.States(directory))
        {
            if (!string.Equals(AgentHostFiles.Text(state, "task_id"), taskId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var alive = AgentHostFiles.Alive(state);
            if (alive)
            {
                Track(UiProjectCatalog.Text(project, "id"), directory, AgentHostFiles.Text(state, "key"));
            }
            hosts.Add(
                new JsonObject
                {
                    ["key"] = AgentHostFiles.Text(state, "key"),
                    ["client"] = AgentHostFiles.Text(state, "client"),
                    ["session_id"] = AgentHostFiles.Text(state, "session_id"),
                    ["model"] = AgentHostFiles.Text(state, "model"),
                    ["permission_mode"] = AgentHostFiles.Text(state, "permission_mode"),
                    ["status"] = alive ? AgentHostFiles.Text(state, "status") : Ended(state),
                    ["alive"] = alive,
                    ["started_at"] = AgentHostFiles.Text(state, "started_at"),
                    ["updated_at"] = AgentHostFiles.Text(state, "updated_at"),
                }
            );
        }
        return hosts;
    }

    /// <summary>
    /// The permission modes of each client for the start dialog: {client: [{id, label, description, default,
    /// unsafe}]}, in the order to show them.
    /// </summary>
    public static JsonObject PermissionModes()
    {
        var modes = new JsonObject();
        foreach (var client in AgentPermissionModes.Clients)
        {
            modes[client] = new JsonArray(
                [
                    .. AgentPermissionModes
                        .For(client)
                        .Select(m =>
                            (JsonNode?)
                                new JsonObject
                                {
                                    ["id"] = m.Id,
                                    ["label"] = m.Label,
                                    ["description"] = m.Description,
                                    ["default"] = m.IsDefault,
                                    ["unsafe"] = m.Unsafe,
                                }
                        ),
                ]
            );
        }
        return modes;
    }

    /// <summary>A host's events after <paramref name="after"/>, with its status and whether it still runs.</summary>
    public JsonObject Log(JsonObject project, string key, long after)
    {
        var directory = Folder(project, key);
        var state =
            AgentHostFiles.ReadState(directory, key) ?? throw new UiRequestException(404, "no agent session " + key);
        var alive = AgentHostFiles.Alive(state);
        IReadOnlyList<JsonObject> events;
        if (alive && Track(UiProjectCatalog.Text(project, "id"), directory, key).Host is var host)
        {
            lock (host.Gate)
            {
                events =
                    host.Events.Count > 0 && AgentHostFiles.Sequence(host.Events[0]) <= after + 1
                        ? host
                            .Events.Where(e => AgentHostFiles.Sequence(e) > after)
                            .Select(e => (JsonObject)e.DeepClone())
                            .ToList()
                        : AgentHostFiles.Events(directory, key, after);
            }
        }
        else
        {
            events = AgentHostFiles.Events(directory, key, after);
        }
        return new JsonObject
        {
            ["key"] = key,
            ["client"] = AgentHostFiles.Text(state, "client"),
            ["session_id"] = AgentHostFiles.Text(state, "session_id"),
            ["status"] = alive ? AgentHostFiles.Text(state, "status") : Ended(state),
            ["alive"] = alive,
            ["events"] = new JsonArray([.. events.Select(e => (JsonNode?)e)]),
        };
    }

    /// <summary>Sends a command to a live host: send (text), stop, approve (id, allow) or close.</summary>
    public async Task CommandAsync(JsonObject project, string key, JsonObject command, CancellationToken token)
    {
        var directory = Folder(project, key);
        var state =
            AgentHostFiles.ReadState(directory, key) ?? throw new UiRequestException(404, "no agent session " + key);
        if (!AgentHostFiles.Alive(state))
        {
            throw new UiRequestException(409, "the agent session has ended; resume it to go on");
        }
        var (host, connected) = Track(UiProjectCatalog.Text(project, "id"), directory, key);
        await connected.WaitAsync(token);
        AgentHostClient? client;
        lock (host.Gate)
        {
            client = host.Client;
        }
        if (client is null)
        {
            throw new UiRequestException(503, "cannot reach the agent session; try again");
        }
        await client.SendAsync(command, token);
    }

    /// <summary>
    /// The events that arrived since the last drain, per host: [{project, key, events}], for the live stream.
    /// </summary>
    public JsonArray Drain()
    {
        JsonArray drained = [];
        foreach (var host in _hosts.Values)
        {
            lock (host.Gate)
            {
                var fresh = host.Events.Where(e => AgentHostFiles.Sequence(e) > host.Drained).ToList();
                if (fresh.Count is 0)
                {
                    continue;
                }
                host.Drained = host.Last;
                drained.Add(
                    (JsonNode)
                        new JsonObject
                        {
                            ["project"] = host.Project,
                            ["key"] = host.Key,
                            ["events"] = new JsonArray([.. fresh.Select(e => (JsonNode?)e.DeepClone())]),
                        }
                );
            }
        }
        return drained;
    }

    private static string Ended(JsonObject state) =>
        AgentHostFiles.Text(state, "status") is AgentHostFiles.Failed ? AgentHostFiles.Failed : AgentHostFiles.Closed;

    private static string Folder(JsonObject project, string key)
    {
        if (!AgentHostFiles.IsKey(key))
        {
            throw new UiRequestException(404, "no agent session " + key);
        }
        return AgentHostFiles.Directory(UiProjectCatalog.Text(project, "db_path"));
    }

    /// <summary>
    /// The host's record here, connecting to its pipe unless connected or connecting; Connected ends once that try
    /// ended (a host just started takes a moment to listen). A host seen for the first time sends the live stream
    /// only what it logs from now on: the page reads what came before.
    /// </summary>
    private (Host Host, Task Connected) Track(string project, string directory, string key)
    {
        var host = _hosts.GetOrAdd(
            key,
            name => new Host(project, directory, name)
            {
                Drained = AgentHostFiles.Events(directory, name).Select(AgentHostFiles.Sequence).DefaultIfEmpty().Max(),
            }
        );
        lock (host.Gate)
        {
            if (host.Client is null && !host.Connecting)
            {
                host.Connecting = true;
                host.Connected = ConnectAsync(host);
            }
            return (host, host.Connected);
        }
    }

    private async Task ConnectAsync(Host host)
    {
        AgentHostClient? client = null;
        try
        {
            // a host just started opens its pipe once its client runs: keep trying for a while
            for (var attempt = 0; attempt < 6 && client is null && !_stopping.IsCancellationRequested; attempt++)
            {
                client = await AgentHostClient.ConnectAsync(host.Key, Connect, _stopping.Token);
                if (
                    client is null
                    && AgentHostFiles.ReadState(host.Directory, host.Key) is { } state
                    && AgentHostFiles.Text(state, "status") is not AgentHostFiles.Starting
                    && !AgentHostFiles.Alive(state)
                )
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // harness ui is stopping.
        }
        lock (host.Gate)
        {
            host.Connecting = false;
            host.Client = client;
            if (client is not null)
            {
                host.Reading = ReadAsync(host, client);
            }
        }
    }

    private async Task ReadAsync(Host host, AgentHostClient client)
    {
        try
        {
            await foreach (var item in client.EventsAsync(_stopping.Token))
            {
                lock (host.Gate)
                {
                    if (AgentHostFiles.Sequence(item) > host.Last)
                    {
                        host.Events.Add(item);
                    }
                    if (host.Events.Count > 5000)
                    {
                        host.Events.RemoveRange(0, host.Events.Count - 5000);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // harness ui is stopping.
        }
        finally
        {
            lock (host.Gate)
            {
                host.Client = null;
            }
            await client.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        foreach (var host in _hosts.Values)
        {
            Task reading;
            lock (host.Gate)
            {
                reading = host.Reading;
            }
            await reading.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        _stopping.Dispose();
    }
}
