using System.Text.Json;

namespace Harness.Core.Providers;

public sealed record ProviderConfiguration
{
    public string Type { get; set; } = "";
    public string Pack { get; set; } = "";
    public bool? Enabled { get; set; }
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = [];
    public int TimeoutMs { get; set; }
    public string[] Only { get; set; } = [];
    public string[] Languages { get; set; } = [];
    public bool? Context { get; set; }
    public string Dir { get; set; } = "";
    public string Freshness { get; set; } = "";
    public Dictionary<string, CapabilityMapping> Capabilities { get; set; } = new(StringComparer.Ordinal);
    public RefreshMapping? Refresh { get; set; }
    public ResolveMapping? Resolve { get; set; }
    public bool IndexWorktrees { get; set; }

    public bool IsEnabled() => Enabled is not false;

    public bool InContext() => Context is not false;
}

public sealed record CapabilityMapping
{
    public string Tool { get; set; } = "";
    public Dictionary<string, JsonElement> Args { get; set; } = new(StringComparer.Ordinal);
    public string Items { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
    public string Evidence { get; set; } = "";
    public string Kind { get; set; } = "";
    public Dictionary<string, JsonElement> Where { get; set; } = new(StringComparer.Ordinal);
    public string Error { get; set; } = "";
    public string Truncated { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Miss { get; set; } = "";
    public CapabilityMapping[] Chain { get; set; } = [];
    public string Combine { get; set; } = "";
}

public sealed record RefreshMapping
{
    public string Tool { get; set; } = "";
    public Dictionary<string, JsonElement> Args { get; set; } = new(StringComparer.Ordinal);
    public string Skip { get; set; } = "";
}

public sealed record ResolveMapping
{
    public string Tool { get; set; } = "";
    public Dictionary<string, JsonElement> Args { get; set; } = new(StringComparer.Ordinal);
    public string Items { get; set; } = "";
    public string Match { get; set; } = "";
    public string Value { get; set; } = "";
    public string Fix { get; set; } = "";
}
