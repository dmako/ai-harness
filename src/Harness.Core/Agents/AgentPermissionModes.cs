namespace Harness.Core.Agents;

/// <summary>
/// A permission (security) mode an agent session can start in: its id, what the start dialog shows (Label,
/// Description), whether it is the client's default and whether it lets the agent act without any approval (Unsafe).
/// The client settings it stands for: Claude Code's extra arguments (ClaudeArguments), Codex's thread approval
/// policy and sandbox (ApprovalPolicy, Sandbox).
/// </summary>
public sealed record AgentPermissionMode(string Id, string Label, string Description)
{
    public bool IsDefault { get; init; }
    public bool Unsafe { get; init; }
    public IReadOnlyList<string> ClaudeArguments { get; init; } = [];
    public string ApprovalPolicy { get; init; } = "";
    public string Sandbox { get; init; } = "";
}

/// <summary>
/// The permission modes each agent client offers (TASK-0135), checked against Claude Code 2.1.318's
/// --permission-mode choices and codex app-server 0.167.1's AskForApproval and SandboxMode. Auto is every client's
/// default: what the agent knows to be safe runs, the rest waits for the user's approval.
/// </summary>
public static class AgentPermissionModes
{
    public const string Auto = "auto";
    public const string Manual = "manual";

    private static readonly AgentPermissionMode[] ClaudeModes =
    [
        new(Auto, "Auto", "Actions Claude Code judges safe run without asking; the rest wait here for Allow or Deny.")
        {
            IsDefault = true,
            ClaudeArguments = ["--permission-mode", "auto"],
        },
        new(Manual, "Manual", "Every tool call that needs permission waits here for Allow or Deny."),
        new(
            "acceptEdits",
            "Accept edits",
            "File edits run without asking; other tools that need permission wait here for Allow or Deny."
        )
        {
            ClaudeArguments = ["--permission-mode", "acceptEdits"],
        },
        new("plan", "Plan", "Claude Code only reads and plans; it changes nothing.")
        {
            ClaudeArguments = ["--permission-mode", "plan"],
        },
        new(
            "bypassPermissions",
            "Bypass permissions",
            "Unsafe: every tool runs without asking, with nothing to approve."
        )
        {
            Unsafe = true,
            ClaudeArguments = ["--permission-mode", "bypassPermissions"],
        },
    ];

    private static readonly AgentPermissionMode[] CodexModes =
    [
        new(
            Auto,
            "Auto",
            "Codex decides when to ask; the rest runs unasked, contained only by the workspace-write sandbox "
                + "(writes in the working folder, no network)."
        )
        {
            IsDefault = true,
            ApprovalPolicy = "on-request",
            Sandbox = "workspace-write",
        },
        new(
            Manual,
            "Manual",
            "Commands Codex does not know to be safe wait here for Allow or Deny; workspace-write sandbox."
        )
        {
            ApprovalPolicy = "untrusted",
            Sandbox = "workspace-write",
        },
        new(
            "read-only",
            "Read only",
            "Codex reads but changes nothing (read-only sandbox); it asks here when it needs more."
        )
        {
            ApprovalPolicy = "on-request",
            Sandbox = "read-only",
        },
        new("full-access", "Full access", "Unsafe: no sandbox and no approvals; commands run with your full access.")
        {
            Unsafe = true,
            ApprovalPolicy = "never",
            Sandbox = "danger-full-access",
        },
    ];

    /// <summary>The clients that offer permission modes.</summary>
    public static IReadOnlyList<string> Clients { get; } = [AgentClients.Claude, AgentClients.Codex];

    /// <summary>The modes a client offers, in the order the start dialog shows them; none for an unknown client.</summary>
    public static IReadOnlyList<AgentPermissionMode> For(string client) =>
        client switch
        {
            AgentClients.Claude => ClaudeModes,
            AgentClients.Codex => CodexModes,
            _ => [],
        };

    /// <summary>The client's default mode (Auto).</summary>
    public static AgentPermissionMode Default(string client) =>
        For(client).FirstOrDefault(m => m.IsDefault)
        ?? throw new ArgumentException("unknown agent client " + client, nameof(client));

    /// <summary>The client's mode with this id (ordinal), or null.</summary>
    public static AgentPermissionMode? Find(string client, string id) =>
        For(client).FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>The mode with this id, or the client's default for an empty id; an unknown id throws.</summary>
    public static AgentPermissionMode Resolve(string client, string id)
    {
        if (id.Length is 0)
        {
            return Default(client);
        }
        return Find(client, id) ?? throw new ArgumentException(Problem(client, id), nameof(id));
    }

    /// <summary>Why <paramref name="id"/> is not a mode of the client, naming the ones it has.</summary>
    public static string Problem(string client, string id) =>
        "unknown permission mode '"
        + id
        + "' for "
        + client
        + "; choose one of "
        + string.Join(", ", For(client).Select(m => m.Id));
}
