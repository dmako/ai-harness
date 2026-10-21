using System.Text.Json;

namespace Harness.Core.Providers;

public sealed record ProviderConfiguration
{
    public string Type { get; init; } = "";
    public string Pack { get; init; } = "";
    public bool? Enabled { get; init; }
    public string Command { get; init; } = "";
    public string[] Args { get; init; } = [];
    public int TimeoutMs { get; init; }
    public string[] Only { get; init; } = [];
    public string[] Languages { get; init; } = [];
    public bool? Context { get; init; }
    public string Dir { get; init; } = "";
    public string Freshness { get; init; } = "";
    public Dictionary<string, CapabilityMapping> Capabilities { get; init; } = new(StringComparer.Ordinal);
    public RefreshMapping? Refresh { get; init; }
    public ResolveMapping? Resolve { get; init; }
    public bool IndexWorktrees { get; init; }
    public bool IsEnabled() => Enabled is not false;
    public bool InContext() => Context is not false;
}

public sealed record CapabilityMapping
{
    public string Tool { get; init; } = "";
    public Dictionary<string, JsonElement> Args { get; init; } = new(StringComparer.Ordinal);
    public string Items { get; init; } = "";
    public Dictionary<string, string> Fields { get; init; } = new(StringComparer.Ordinal);
    public string Evidence { get; init; } = "";
    public string Kind { get; init; } = "";
    public Dictionary<string, JsonElement> Where { get; init; } = new(StringComparer.Ordinal);
    public string Error { get; init; } = "";
    public string Truncated { get; init; } = "";
    public string Pattern { get; init; } = "";
    public string Miss { get; init; } = "";
    public CapabilityMapping[] Chain { get; init; } = [];
    public string Combine { get; init; } = "";
}

public sealed record RefreshMapping
{
    public string Tool { get; init; } = "";
    public Dictionary<string, JsonElement> Args { get; init; } = new(StringComparer.Ordinal);
    public string Skip { get; init; } = "";
}

public sealed record ResolveMapping
{
    public string Tool { get; init; } = "";
    public Dictionary<string, JsonElement> Args { get; init; } = new(StringComparer.Ordinal);
    public string Items { get; init; } = "";
    public string Match { get; init; } = "";
    public string Value { get; init; } = "";
    public string Fix { get; init; } = "";
}
