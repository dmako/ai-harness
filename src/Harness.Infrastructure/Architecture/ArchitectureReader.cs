using Harness.Core.Architecture;
using Harness.Core.Memory;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Workspaces;

namespace Harness.Infrastructure.Architecture;

public sealed record ArchitectureRead(ProjectArchitecture? Architecture, IReadOnlyList<string> Notes);

/// <summary>One request caches each member's files/build. Quick reads omit workspace and stale-knowledge scans.</summary>
public sealed class ArchitectureReader(IReadOnlyList<WorkspaceMember> members)
{
    private readonly WorkspaceEvidenceReader _evidence = new(members);
    private readonly Dictionary<string, ProjectBuild?> _builds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Clones.RepeatedCodeReader> _repeated = new(StringComparer.Ordinal);
    public async Task<IReadOnlyList<ArchitectureFinding>> RepeatedAsync(WorkspaceMember member, IReadOnlyList<ArchitectureFinding>? provided, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_repeated.TryGetValue(member.Id, out var reader))
        {
            var (repository, _) = await _evidence.RepositoryAsync(member, token).ConfigureAwait(false);
            reader = new(repository);
            _repeated.Add(member.Id, reader);
        }
        return reader.Read(provided);
    }
    public async Task<ArchitectureRead> ProjectAsync(WorkspaceMember current, BoundaryRules? rules, bool includeMembers, CancellationToken token, bool workspaceKnown = false)
    {
        var build = await BuildAsync(current, token).ConfigureAwait(false);
        if (build is null)
        {
            return new(null, ["no .NET, Go or TypeScript/JavaScript project found here"]);
        }

        HashSet<string> consumed = new(StringComparer.Ordinal);
        List<string> notes = [];
        var complete = true;
        if (includeMembers)
        {
            foreach (var member in members.Where(member => member.Id != current.Id))
            {
                try
                {
                    var peer = await BuildAsync(member, token).ConfigureAwait(false);
                    if (peer is null)
                    {
                        continue;
                    }

                    consumed.UnionWith(peer.Projects.SelectMany(unit => unit.Packages).Select(package => package.Name));
                    var (repository, _) = await _evidence.RepositoryAsync(member, token).ConfigureAwait(false);
                    if (repository.Notes.Count > 0)
                    {
                        complete = false;
                    }

                    notes.AddRange(repository.Notes.Select(note => member.Name + ": " + note));
                }
                catch (Exception error) when (WorkspaceEvidenceReader.ReadError(error))
                { complete = false; notes.Add(member.Name + ": " + error.Message.Split('\n')[0]); }
            }
        }
        // Unreadable peers cannot prove a published package unused.
        Func<string, bool>? used = includeMembers && complete && (members.Count > 1 || workspaceKnown) ? consumed.Contains : null;
        var architecture = ArchitectureAnalyzer.Analyze(build, used);
        if (rules is not null)
        {
            architecture = architecture with { Findings = rules.Apply(architecture) };
        }

        var (own, _) = await _evidence.RepositoryAsync(current, token).ConfigureAwait(false);
        notes.AddRange(own.Notes);
        return new(architecture, notes);
    }
    public async Task<WorkspaceSystem> SystemAsync(CancellationToken token, string workspace = "", KnowledgeLevels? levels = null)
    {
        List<SystemMember> inputs = [];
        List<string> notes = [];
        foreach (var member in members)
        {
            try
            {
                var (repository, _) = await _evidence.RepositoryAsync(member, token).ConfigureAwait(false);
                var build = await BuildAsync(member, token).ConfigureAwait(false);
                inputs.Add(new(member.Id, member.Title.Length > 0 ? member.Title : member.Name, member.Name, repository, build is not null && !build.Projects.Any(unit => unit.Kind is "entry")));
            }
            catch (Exception error) when (WorkspaceEvidenceReader.ReadError(error)) { notes.Add(member.Name + ": " + error.Message.Split('\n')[0]); }
        }

        token.ThrowIfCancellationRequested();
        var system = new SystemReader(inputs).Read();
        var shared = await new WorkspaceDuplicateReader(members, _evidence).SharedAsync(workspace, levels, token).ConfigureAwait(false);
        system = system with { Findings = system.Findings.Concat(shared.Findings).ToArray() };
        var allNotes = (system.Notes ?? []).Concat(notes).ToArray();
        return system with { Notes = allNotes.Length is 0 ? null : allNotes };
    }
    public async Task<AnalysisRepository> RepositoryAsync(WorkspaceMember member, CancellationToken token) => (await _evidence.RepositoryAsync(member, token).ConfigureAwait(false)).Repository;

    private async Task<ProjectBuild?> BuildAsync(WorkspaceMember member, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_builds.TryGetValue(member.Id, out var build))
        {
            return build;
        }

        var (repository, ecosystem) = await _evidence.RepositoryAsync(member, token).ConfigureAwait(false);
        build = new BuildReader(repository, ecosystem).Read();
        _builds.Add(member.Id, build);
        return build;
    }

    /// <summary>Own groups precede workspace/user groups; every applicable rule is checked.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Wider stores are borrowed only during the using declaration over their read-only database.")]
    public static BoundaryRules RulesFor(IMemoryStore? own, KnowledgeLevels? levels, string projectId, CancellationToken token)
    {
        var rules = new BoundaryRules();
        var query = new MemoryQuery { Types = ["constraint", "decision", "convention"], Limit = 2000 };
        void Add(IMemoryStore store, string? scope)
        {
            foreach (var memory in store.Search(query, token))
            {
                rules.Add(memory.Content, new(memory.Id, Scope: scope ?? memory.Scope.ToString(), Type: memory.Type));
            }
        }
        if (own is not null)
        {
            Add(own, null);
        }

        if (levels is null)
        {
            return rules;
        }

        var workspaces = levels.Load();
        var scopes = workspaces.Where(workspace => projectId.Length > 0 && workspace.Members.Contains(projectId, StringComparer.Ordinal)).Select(workspace => new MemoryScope("workspace", workspace.Name)).Append(MemoryScope.User);
        foreach (var scope in scopes)
        {
            var path = levels.KnowledgePath(workspaces, scope);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var database = HarnessDatabase.OpenReadOnly(path);
                Add(new MemoryStore(database), scope.ToString());
            }
            catch (Exception error) when (KnowledgeLevels.IsStorageError(error)) { /* Optional unavailable wider knowledge follows the source's best-effort rule lookup. */ }
        }
        return rules;
    }
}
