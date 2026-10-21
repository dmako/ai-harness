using Harness.Core.Memory;
using Harness.Core.Providers;

namespace Harness.Core.Workspaces;

public sealed record Workspace(string Id, string Name, string Title, IReadOnlyList<string> Members, string Created);
public sealed record WorkspaceMember(string Name, string Id, string Root, string DatabasePath = "", string Title = "");
public sealed record WorkspaceReference(string Project, string Path, string Symbol = "");
public sealed record WorkspaceHit(WorkspaceMember Member, ProviderItem Item, string Evidence, string Via = "", bool Exact = false);
public sealed record WorkspaceCall(string Project, ProviderResult Result);
public sealed record WorkspaceAnswer(IReadOnlyList<WorkspaceHit> Hits, IReadOnlyList<WorkspaceCall> Calls, IReadOnlyList<string> Notes, IReadOnlyList<string> Errors);
public sealed record SymbolHome(string Path = "", string Namespace = "", string Package = "", string PackageRef = "");
public sealed record WorkspaceImpact(SymbolHome Home, IReadOnlyList<WorkspaceHit> Hits, IReadOnlyList<string> Notes, IReadOnlyList<string> Errors);
public sealed record WorkspaceMembership(WorkspaceMember Current, IReadOnlyList<WorkspaceMember> Members);
public interface IWorkspaceProjectSession
{
    Task<WorkspaceMembership> ReadWorkspaceMembershipAsync(CancellationToken cancellationToken);
}

public interface IWorkspaceSearchSession : IAsyncDisposable
{
    WorkspaceMember Current { get; }
    IReadOnlyList<WorkspaceMember> Members { get; }
    Task<WorkspaceAnswer> QueryAsync(ProviderRequest request, bool deep, string only, CancellationToken cancellationToken);
    Task<WorkspaceImpact> ImpactAsync(string query, int limit, bool deep, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> ContextMemoriesAsync(WorkspaceMember member, IReadOnlyList<string> paths, string words, CancellationToken cancellationToken);
}

public static class WorkspaceReferences
{
    public static WorkspaceReference? Parse(string text, IReadOnlyList<WorkspaceMember> members)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !members.Any(member => member.Name == text[..colon]))
        {
            return null;
        }

        var path = text[(colon + 1)..];
        var hash = path.LastIndexOf('#');
        return hash < 0 ? new(text[..colon], path) : new(text[..colon], path[..hash], path[(hash + 1)..]);
    }
    public static string LastSegment(string text) => text[(text.LastIndexOfAny(['.', ':']) + 1)..];
}
