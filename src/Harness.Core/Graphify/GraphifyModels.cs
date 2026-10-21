using System.Text.Json.Serialization;

namespace Harness.Core.Graphify;

public sealed record GraphifyOptions(string Root, string Command)
{
    public IReadOnlyList<string> CommandArguments { get; init; } = [];
    public IReadOnlyList<string> WatchArguments { get; init; } = [];
    public string SessionDirectory { get; init; } = "";
    public string RecordsPath { get; init; } = "";
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    public TimeSpan ReadyWait { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan Idle { get; init; } = TimeSpan.FromHours(2);
    public bool StopOnExit { get; init; }
    public string StartedBy { get; init; } = "harness";
}

public sealed record GraphifySession
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("pid")] public int Pid { get; init; }
    [JsonPropertyName("reachable")] public bool Reachable { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("ready")] public bool Ready { get; init; }
    [JsonPropertyName("stage")] public string Stage { get; init; } = "";
    [JsonPropertyName("root")] public string Root { get; init; } = "";
    [JsonPropertyName("input")] public string Input { get; init; } = "";
    [JsonPropertyName("memory_bytes")] public long MemoryBytes { get; init; }
    [JsonPropertyName("uptime_ms")] public long UptimeMs { get; init; }
    [JsonPropertyName("started_by")] public string StartedBy { get; init; } = "";
    [JsonPropertyName("discovery_incomplete")] public string DiscoveryIncomplete { get; init; } = "";
    [JsonPropertyName("last_query")] public string LastQuery { get; init; } = "";
    [JsonPropertyName("load_took_ms")] public long LoadTookMs { get; init; }
    public TimeSpan Remaining() => Ready ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Max(0, LoadTookMs - UptimeMs));
}

public interface IGraphifyWatch
{
    int Id { get; }
    Task<int> Completion { get; }
    void Kill();
}

/// <summary>External command, descriptor and optional harness observation boundary; no compiler implementation.</summary>
public interface IGraphifySessions
{
    string Root { get; }
    Task<byte[]> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
    Task<IReadOnlyList<GraphifySession>> ListAsync(bool recent, CancellationToken cancellationToken);
    Task<IGraphifyWatch?> StartAsync(CancellationToken cancellationToken);
    Task StopAsync(string id, CancellationToken cancellationToken);
    Task<int> CleanAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> SweepAsync(TimeSpan idle, CancellationToken cancellationToken);
    Task TouchAsync(string id, CancellationToken cancellationToken);
    Task NoteLoadedAsync(long milliseconds, CancellationToken cancellationToken);
    Task NoteDiscoveryAsync(string id, string reason, CancellationToken cancellationToken);
    string InputDescription();
    string LastLog();
}
