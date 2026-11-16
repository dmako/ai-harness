using Harness.Ui.Api;
using Harness.Ui.Navigation;

namespace Harness.Ui.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Seeds the designer without starting live updates or contacting a server.</summary>
    internal void InitializePreview(IReadOnlyList<ProjectInfo> projects, WorkspaceInfo workspace)
    {
        _projects = projects;
        _workspaces = [workspace];
        _loaded = true;
        Show(Route.Home);
    }
}
