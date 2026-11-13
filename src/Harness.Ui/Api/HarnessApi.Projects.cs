namespace Harness.Ui.Api;

/// <summary>The projects and workspaces areas: the lists the shell and Home draw from.</summary>
public sealed partial class HarnessApi
{
    public async Task<IReadOnlyList<ProjectInfo>> ProjectsAsync(CancellationToken token) =>
        (await GetAsync("api/projects", UiJsonContext.Default.ProjectList, token)).Projects;

    public async Task<IReadOnlyList<WorkspaceInfo>> WorkspacesAsync(CancellationToken token) =>
        (await GetAsync("api/workspaces", UiJsonContext.Default.WorkspaceList, token)).Workspaces;

}
