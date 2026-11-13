namespace Harness.Ui.Api;

/// <summary>The tasks and Board area: a project's Board and this machine's choices for it.</summary>
public sealed partial class HarnessApi
{
    public Task<BoardResponse> BoardAsync(string projectId, CancellationToken token) =>
        GetAsync($"api/projects/{Uri.EscapeDataString(projectId)}/board", UiJsonContext.Default.BoardResponse, token);

    /// <summary>Saves this machine's Board choices for the project (kept in ui.json, shared by every browser).</summary>
    public Task SetBoardAsync(string projectId, BoardChange change, CancellationToken token) =>
        PostAsync($"api/projects/{Uri.EscapeDataString(projectId)}/board", change, UiJsonContext.Default.BoardChange, token);

}
