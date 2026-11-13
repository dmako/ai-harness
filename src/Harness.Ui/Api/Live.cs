namespace Harness.Ui.Api;

// The live area: GET /api/events (UiLiveHub), a text/event-stream whose "change" events carry a LiveChange.

/// <summary>An "event: change" of /api/events: which projects changed and what kinds of their data.</summary>
public sealed class LiveChange
{
    public bool Registry { get; init; }
    public bool Workspaces { get; init; }
    public bool All { get; init; }

    /// <summary>Knowledge levels whose memories changed (workspace:&lt;name&gt;, user).</summary>
    public IReadOnlyList<string> Levels { get; init; } = [];
    public IReadOnlyList<string> Projects { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Kinds { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

/// <summary>{"error": "..."}, what harness ui answers a failed request with.</summary>
public sealed class ApiError
{
    public required string Error { get; init; }
}
