using Harness.Core.Workspaces;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Workspaces;

namespace Harness.Infrastructure.Projects;

public sealed partial class HarnessStateSession
{
    public async Task<WorkspaceMembership> ReadWorkspaceMembershipAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(location.DatabasePath))
        {
            throw new InvalidOperationException("workspace search needs an initialized project ('harness init')");
        }

        await DatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (_registration is null)
        {
            throw new InvalidOperationException("workspace search needs a registered project");
        }

        var store = new WorkspaceStore(globalConfigurationPath ?? ConfigurationPaths.GlobalPath(), registry);
        var members = store.Members(_registration.Project.Id);
        if (members.Count < 2)
        {
            throw new InvalidOperationException(
                "this project is in no workspace with other projects ('harness workspace create <name> --discover <folder>')"
            );
        }

        var current =
            members.FirstOrDefault(member => member.Id == _registration.Project.Id)
            ?? throw new InvalidOperationException("this project is not registered on this machine ('harness sync')");
        return new(current, members);
    }
}
