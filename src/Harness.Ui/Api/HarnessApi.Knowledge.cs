using System.Globalization;

namespace Harness.Ui.Api;

/// <summary>The knowledge area: a project's memories and proposals, the wider levels, and every write on them.</summary>
public sealed partial class HarnessApi
{
    /// <summary>The seven memory types, in the page's order.</summary>
    public static readonly IReadOnlyList<string> MemoryTypes = ["goal", "constraint", "decision", "convention", "warning", "fact", "open_question"];

    /// <summary>A project's memories of every type (with the wider levels'), matching <paramref name="query"/>.</summary>
    public Task<MemoryList> MemoriesAsync(string projectId, string? query, bool inactive, CancellationToken token, bool removed = false)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"memories?type={string.Join(',', MemoryTypes)}&limit=500")
            + (removed ? "&shared=removed" : "&levels=1")
            + (query is { Length: > 0 } q ? "&q=" + Uri.EscapeDataString(q) : "")
            + (inactive ? "&inactive=1" : "");
        return GetAsync(Project(projectId, path), UiJsonContext.Default.MemoryList, token);
    }

    /// <summary>A memory with its versions and trail: a project's, or a level's when <paramref name="level"/> is given.</summary>
    public Task<MemoryDetail> MemoryAsync(string? projectId, string memoryId, string? level, CancellationToken token) =>
        GetAsync(
            level is { Length: > 0 }
                ? $"api/knowledge/memories/{Uri.EscapeDataString(memoryId)}?level={Uri.EscapeDataString(level)}"
                : Project(projectId!, $"memories/{Uri.EscapeDataString(memoryId)}"),
            UiJsonContext.Default.MemoryDetail,
            token
        );

    public Task<Applies> AppliesAsync(string projectId, string scope, CancellationToken token) =>
        GetAsync(Project(projectId, "applies?scope=" + Uri.EscapeDataString(scope)), UiJsonContext.Default.Applies, token);

    public Task<MemoryProposalList> MemoryProposalsAsync(string projectId, string status, CancellationToken token) =>
        GetAsync(Project(projectId, "proposals?status=" + Uri.EscapeDataString(status)), UiJsonContext.Default.MemoryProposalList, token);

    public Task<LevelKnowledge> LevelAsync(string level, CancellationToken token) =>
        GetAsync("api/knowledge?level=" + Uri.EscapeDataString(level), UiJsonContext.Default.LevelKnowledge, token);

    /// <summary>Adds the author's statement: to a project (its scope), or to a level (<see cref="MemoryAdd.Level"/>).</summary>
    public async Task<MemoryAdded> AddMemoryAsync(string? projectId, MemoryAdd memory, CancellationToken token)
    {
        var answer = await PostAsync(
            memory.Level is { Length: > 0 } ? "api/knowledge/memories" : Project(projectId!, "memories"),
            memory,
            UiJsonContext.Default.MemoryAdd,
            token
        );
        return System.Text.Json.JsonSerializer.Deserialize(answer, UiJsonContext.Default.MemoryAdded)
            ?? throw new HarnessApiException("memories: empty answer");
    }

    /// <summary>Corrects (content) or retires a memory; a level's (<see cref="MemoryChange.Level"/>) at its level.</summary>
    public Task ChangeMemoryAsync(string? projectId, string memoryId, MemoryChange change, CancellationToken token)
    {
        var what = change.Content is null ? "retire" : "supersede";
        var path = change.Level is { Length: > 0 }
            ? $"api/knowledge/memories/{Uri.EscapeDataString(memoryId)}/{what}"
            : Project(projectId!, $"memories/{Uri.EscapeDataString(memoryId)}/{what}");
        return PostAsync(path, change, UiJsonContext.Default.MemoryChange, token);
    }

    public async Task<Promoted> PromoteAsync(string projectId, string memoryId, Promotion promotion, CancellationToken token)
    {
        var answer = await PostAsync(
            Project(projectId, $"memories/{Uri.EscapeDataString(memoryId)}/promote"),
            promotion,
            UiJsonContext.Default.Promotion,
            token
        );
        return System.Text.Json.JsonSerializer.Deserialize(answer, UiJsonContext.Default.Promoted)
            ?? throw new HarnessApiException("promote: empty answer");
    }

    /// <summary>Shares a memory with the team (.harness/knowledge.toml), or withdraws it (<paramref name="share"/> false).</summary>
    public async Task<ShareResult> ShareAsync(string projectId, string memoryId, bool share, CancellationToken token)
    {
        var answer = await PostAsync(
            Project(projectId, $"memories/{Uri.EscapeDataString(memoryId)}/{(share ? "share" : "withdraw")}"),
            Nothing,
            UiJsonContext.Default.DictionaryStringString,
            token
        );
        return System.Text.Json.JsonSerializer.Deserialize(answer, UiJsonContext.Default.ShareResult)
            ?? throw new HarnessApiException("share: empty answer");
    }

    /// <summary>Keeps a memory a Git change took out of project knowledge as a local one.</summary>
    public Task KeepMemoryAsync(string projectId, string memoryId, CancellationToken token) =>
        PostAsync(Project(projectId, $"memories/{Uri.EscapeDataString(memoryId)}/keep"), Nothing, UiJsonContext.Default.DictionaryStringString, token);

    public async Task<ProposalApproved> ApproveMemoryAsync(string projectId, string proposalId, ProposalApproval approval, CancellationToken token)
    {
        var answer = await PostAsync(
            Project(projectId, $"proposals/{Uri.EscapeDataString(proposalId)}/approve"),
            approval,
            UiJsonContext.Default.ProposalApproval,
            token
        );
        return System.Text.Json.JsonSerializer.Deserialize(answer, UiJsonContext.Default.ProposalApproved)
            ?? throw new HarnessApiException("approve: empty answer");
    }

    public Task RejectMemoryAsync(string projectId, string proposalId, string note, CancellationToken token) =>
        PostAsync(
            Project(projectId, $"proposals/{Uri.EscapeDataString(proposalId)}/reject"),
            new Rejection { Note = note },
            UiJsonContext.Default.Rejection,
            token
        );
}
