using Harness.Core.Architecture;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Architecture;

namespace Harness.Infrastructure.Workspaces;

public sealed partial class WorkspaceEvidenceReader
{
    private readonly Dictionary<string, (ProjectStack? Stack, ProjectBuild? Build)> _stacks = new(
        StringComparer.Ordinal
    );

    public async Task<WorkspaceDependencyGraph> DependencySummaryAsync(CancellationToken token)
    {
        var graph = await DependenciesAsync(token).ConfigureAwait(false);
        List<WorkspaceDependencyNode> nodes = [];
        foreach (var node in graph.Nodes)
        {
            var member = members.First(member => member.Id == node.ProjectId);
            var model = await StackAsync(member, token).ConfigureAwait(false);
            nodes.Add(
                node with
                {
                    Library = model.Build is not null && !model.Build.Projects.Any(unit => unit.Kind is "entry"),
                }
            );
        }
        return new(nodes, graph.Edges);
    }

    public async Task<ProjectStackSummary> ProjectStackAsync(string id, CancellationToken token)
    {
        var member =
            members.FirstOrDefault(member => member.Id == id)
            ?? throw new ArgumentException("project is not a workspace member");
        var current = await StackAsync(member, token).ConfigureAwait(false);
        return new(
            current.Stack,
            await UsesAsync(current.Build, members.Where(member => member.Id != id).ToArray(), token)
                .ConfigureAwait(false)
        );
    }

    public async Task<WorkspaceStackSummary> WorkspaceStackAsync(CancellationToken token)
    {
        Dictionary<string, ProjectStack> stacks = new(StringComparer.Ordinal);
        Dictionary<(string Kind, string Name), Dictionary<string, IReadOnlyList<string>>> rows = [];
        Dictionary<string, string> published = new(StringComparer.Ordinal);
        Dictionary<string, string> owners = new(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var model = await StackAsync(member, token).ConfigureAwait(false);
            if (model.Stack is not { } stack)
            {
                continue;
            }

            stacks.Add(member.Id, stack);
            if (stack.Runtime is { } runtime)
            {
                Row("runtime", runtime.Name)[member.Id] = [runtime.Major ?? ""];
            }

            foreach (var framework in stack.Frameworks.Where(framework => !string.IsNullOrEmpty(framework.Major)))
            {
                Row("framework", framework.Name)[member.Id] = [framework.Major!];
            }

            foreach (
                var use in await UsesAsync(model.Build, members.Where(peer => peer.Id != member.Id).ToArray(), token)
                    .ConfigureAwait(false)
            )
            {
                if (use.Versions.Count > 0)
                {
                    Row("core", use.Package)[member.Id] = use.Versions;
                    owners[use.Package] = use.MemberId;
                }
            }

            if (model.Build is not null)
            {
                foreach (
                    var unit in model.Build.Projects.Where(unit =>
                        !string.IsNullOrEmpty(unit.Package) && !string.IsNullOrEmpty(unit.Version)
                    )
                )
                {
                    published[unit.Package!] = unit.Version!;
                }
            }
        }
        var output = rows.Select(row =>
            {
                var version = published.GetValueOrDefault(row.Key.Name);
                var distinct = row
                    .Value.Values.SelectMany(versions => versions)
                    .Concat(version is null ? [] : new[] { version })
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                return new StackVersionRow(
                    row.Key.Kind,
                    row.Key.Name,
                    row.Value,
                    row.Key.Kind is "core" ? owners.GetValueOrDefault(row.Key.Name) : null,
                    version,
                    distinct > 1
                );
            })
            .OrderBy(row =>
                row.Kind switch
                {
                    "runtime" => 0,
                    "framework" => 1,
                    _ => 2,
                }
            )
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(stacks, output);
        Dictionary<string, IReadOnlyList<string>> Row(string kind, string name)
        {
            if (!rows.TryGetValue((kind, name), out var row))
            {
                row = new(StringComparer.Ordinal);
                rows.Add((kind, name), row);
            }
            return row;
        }
    }

    private async Task<IReadOnlyList<MemberPackageUse>> UsesAsync(
        ProjectBuild? build,
        IReadOnlyList<WorkspaceMember> others,
        CancellationToken token
    )
    {
        if (build is null)
        {
            return [];
        }

        List<MemberPackageUse> result = [];
        foreach (var member in others)
        {
            var peer = await StackAsync(member, token).ConfigureAwait(false);
            if (peer.Build is null)
            {
                continue;
            }

            var published = peer
                .Build.Projects.Where(unit => !string.IsNullOrEmpty(unit.Package))
                .GroupBy(unit => unit.Package!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Version, StringComparer.Ordinal);
            foreach (
                var group in build
                    .Projects.SelectMany(unit => unit.Packages)
                    .Where(package => published.ContainsKey(package.Name))
                    .GroupBy(package => package.Name, StringComparer.Ordinal)
            )
            {
                result.Add(
                    new(
                        group.Key,
                        member.Title.Length > 0 ? member.Title : member.Name,
                        member.Id,
                        group
                            .Where(package => !string.IsNullOrEmpty(package.Version))
                            .Select(package => package.Version!)
                            .Distinct(StringComparer.Ordinal)
                            .Order(WorkspaceNameComparer.Instance)
                            .ToArray(),
                        published[group.Key]
                    )
                );
            }
        }
        return result.OrderBy(use => use.Package, WorkspaceNameComparer.Instance).ToArray();
    }

    private async Task<(ProjectStack? Stack, ProjectBuild? Build)> StackAsync(
        WorkspaceMember member,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        if (_stacks.TryGetValue(member.Id, out var cached))
        {
            return cached;
        }

        (ProjectStack? Stack, ProjectBuild? Build) model = (null, null);
        try
        {
            var (repository, ecosystem) = await RepositoryAsync(member, token).ConfigureAwait(false);
            model = (new StackReader(repository, ecosystem).Read(), new BuildReader(repository, ecosystem).Read());
        }
        catch (Exception error) when (ReadError(error))
        { /* Missing members do not contribute invented stack or version evidence. */
        }
        _stacks.Add(member.Id, model);
        return model;
    }
}
