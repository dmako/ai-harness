namespace Harness.Ui.Api;

/// <summary>The workspace area: system, dependencies, stack, findings, issues, shared code, libraries and members.</summary>
public sealed partial class HarnessApi
{
    public Task<SystemAnswer> SystemAsync(string workspace, CancellationToken token) =>
        GetAsync(Workspace(workspace, "system"), UiJsonContext.Default.SystemAnswer, token);

    public Task<DependencyAnswer> DependenciesAsync(string workspace, CancellationToken token) =>
        GetAsync(Workspace(workspace, "dependencies"), UiJsonContext.Default.DependencyAnswer, token);

    public Task<WorkspaceStackAnswer> WorkspaceStackAsync(string workspace, CancellationToken token) =>
        GetAsync(Workspace(workspace, "stack"), UiJsonContext.Default.WorkspaceStackAnswer, token);

    public Task<RollupAnswer> RollupAsync(string workspace, CancellationToken token) =>
        GetAsync(Workspace(workspace, "findings"), UiJsonContext.Default.RollupAnswer, token);

    public Task<IssuesAnswer> IssuesAsync(string workspace, CancellationToken token) =>
        GetAsync(Workspace(workspace, "issues"), UiJsonContext.Default.IssuesAnswer, token);

    public Task<DuplicatesAnswer> DuplicatesAsync(string workspace, CancellationToken token) =>
        GetAsync(Workspace(workspace, "duplicates"), UiJsonContext.Default.DuplicatesAnswer, token);

    public Task<LibraryAdoption> AdoptionAsync(string workspace, string library, CancellationToken token) =>
        GetAsync(Workspace(workspace, "adoption?library=" + Uri.EscapeDataString(library)), UiJsonContext.Default.LibraryAdoption, token);

    public Task<DiscoverAnswer> DiscoverAsync(string folder, CancellationToken token) =>
        GetAsync("api/workspaces/discover?folder=" + Uri.EscapeDataString(folder), UiJsonContext.Default.DiscoverAnswer, token);

    public async Task<WorkspaceWritten> CreateWorkspaceAsync(WorkspaceCreate body, CancellationToken token) =>
        Read(await PostAsync("api/workspaces", body, UiJsonContext.Default.WorkspaceCreate, token), UiJsonContext.Default.WorkspaceWritten);

    public async Task<WorkspaceWritten> JoinAsync(string workspace, IReadOnlyList<string> projectIds, CancellationToken token) =>
        Read(
            await PostAsync(Workspace(workspace, "join"), new WorkspaceJoin { ProjectIds = projectIds }, UiJsonContext.Default.WorkspaceJoin, token),
            UiJsonContext.Default.WorkspaceWritten
        );

    public Task LeaveAsync(string workspace, string projectId, CancellationToken token) =>
        PostAsync(Workspace(workspace, "leave"), new WorkspaceLeave { ProjectId = projectId }, UiJsonContext.Default.WorkspaceLeave, token);

    public Task DeleteWorkspaceAsync(string workspace, CancellationToken token) =>
        PostAsync(Workspace(workspace, "delete"), Nothing, UiJsonContext.Default.DictionaryStringString, token);

    /// <summary>A finding between the members: mark, task (in a member, by its id in Project) or unmark.</summary>
    public Task WorkspaceFindingAsync(string workspace, string action, FindingAction body, CancellationToken token) =>
        PostAsync(Workspace(workspace, "findings/" + action), body, UiJsonContext.Default.FindingAction, token);

    private static string Workspace(string name, string path) => $"api/workspaces/{Uri.EscapeDataString(name)}/{path}";
}
