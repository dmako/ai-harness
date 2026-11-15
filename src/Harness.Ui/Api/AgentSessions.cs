using System.Text.Json.Serialization;

namespace Harness.Ui.Api;

public sealed class AgentHost
{
    public required string Key { get; init; }
    public required string Client { get; init; }
    public string SessionId { get; init; } = "";
    public string Status { get; init; } = "starting";
    public bool Alive { get; init; }
    public string PermissionMode { get; init; } = "";
    public string RequestedModel { get; init; } = "";
    public bool ModelUnlisted { get; init; }
}

public sealed class AgentMode
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    [JsonPropertyName("default")]
    public bool Default { get; init; }
    public bool Unsafe { get; init; }
    public string Display => Unsafe ? $"⚠ {Label} (unsafe)" : DefaultLabel;
    private string DefaultLabel => Default ? $"{Label} (default)" : Label;
    public override string ToString() => Display;
}

public sealed class AgentModels
{
    public IReadOnlyDictionary<string, AgentCatalog> Clients { get; init; } =
        new Dictionary<string, AgentCatalog>(StringComparer.Ordinal);
}

public sealed class AgentCatalog
{
    [JsonPropertyName("default")]
    public AgentModel? Default { get; init; }
    public IReadOnlyList<AgentModel> Models { get; init; } = [];
    public bool Refreshing { get; init; }
    public string? Error { get; init; }
}

public sealed class AgentModel
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Source { get; init; }
}

public sealed class AgentEvent
{
    public long Seq { get; init; }
    public required string Event { get; init; }
    public string? Id { get; init; }
    public string? Text { get; init; }
    public string? Tool { get; init; }
    public string? Status { get; init; }
    public string? Model { get; init; }
    public string? SessionId { get; init; }
    public string? Error { get; init; }
    public bool Allow { get; init; }
    public long DurationMs { get; init; }
    public double? CostUsd { get; init; }
}

public sealed class AgentBatchEventArgs : EventArgs
{
    public required string Project { get; init; }
    public required string Key { get; init; }
    public IReadOnlyList<AgentEvent> Events { get; init; } = [];
}

public sealed class AgentLog
{
    public required string Status { get; init; }
    public bool Alive { get; init; }
    public IReadOnlyList<AgentEvent> Events { get; init; } = [];
}

public sealed class AgentStart
{
    public required string Client { get; init; }
    public required string Prompt { get; init; }
    public string Model { get; init; } = "";
    public string Mode { get; init; } = "";
    public string Resume { get; init; } = "";
}

public sealed class AgentCommand
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Allow { get; init; }
}
