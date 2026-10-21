using Harness.Core.Architecture;
using Harness.Core.Findings;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Architecture.Clones;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Workspaces;

namespace Harness.Infrastructure.Findings;

/// <summary>Request-scoped project/workspace finding sets, shared by MCP and sync.</summary>
public sealed class FindingSets(string root, string projectId, HarnessDatabase? database, string globalConfigurationPath, Projects.MachineRegistry registry)
{
    private readonly KnowledgeLevels _levels = new(globalConfigurationPath, registry);
    private readonly WorkspaceStore _workspaces = new(globalConfigurationPath, registry);
    private WorkspaceMember Current => new("", projectId, root, database?.Path ?? "");
    private KnowledgeWorkspace[] Workspaces() => _levels.Load().Where(w => projectId.Length > 0 && w.Members.Contains(projectId, StringComparer.Ordinal)).ToArray();
    private async Task<(IReadOnlyList<ArchitectureFinding> Found, bool Known)> ProjectAsync(bool full, IProviderRegistry? providers, CancellationToken token)
    {
        var members = full ? _workspaces.Members(projectId) : [];
        if (!members.Any(m => m.Id == projectId))
            members = members.Append(Current).ToArray();
        var reader = new ArchitectureReader(members);
        var own = database is null ? null : new MemoryStore(database);
        var rules = ArchitectureReader.RulesFor(own, _levels, projectId, token);
        var read = await reader.ProjectAsync(Current, rules, full, token).ConfigureAwait(false);
        var found = (read.Architecture?.Findings ?? []).ToList();
        var stored = database is null ? [] : new FindingStore(database).Current(token).Findings;
        if (!full)
        {
            var have = found.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            var published = (read.Architecture?.Projects ?? []).Where(p => !string.IsNullOrEmpty(p.Package)).Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
            found.AddRange(stored.Where(f => f.Source.Provider is not ("" or "harness") || f.Source.Tool is "internal/clones" || f.Kind is "stale-knowledge" || f.Kind is "unused" && published.Contains(f.Project ?? "") && !have.Contains(f.Key)));
        }
        else if (read.Architecture is not null)
        {
            var repo = await reader.RepositoryAsync(Current, token).ConfigureAwait(false);
            if (own is not null)
                found.AddRange(KnowledgeDrift.Read(repo, own.Search(new() { Types = ["constraint", "decision", "convention"], Limit = 2000 }, token), token));
            if (providers is not null)
                await providers.DiscoverAsync(token).ConfigureAwait(false);
            var cycles = await ArchitectureProviders.CyclesAsync(providers, token).ConfigureAwait(false);
            var duplicates = await RepeatedCodeReader.ProviderDuplicatesAsync(providers, token).ConfigureAwait(false);
            var fresh = cycles.Findings.Concat(duplicates.Findings).ToList();
            fresh.AddRange(stored.Where(f => f.Source.Provider is not "harness" && !(f.Kind is "cycle" && cycles.Answered || f.Kind is "duplicate" && duplicates.Answered)));
            found.AddRange(await reader.RepeatedAsync(Current, fresh, token).ConfigureAwait(false));
        }
        return (FindingStore.Merge(found), read.Architecture is not null);
    }
    public IReadOnlyList<FindingContextGroup> Context(CancellationToken token)
    {
        List<FindingContextGroup> groups = [];
        if (database is not null)
            groups.Add(new("", "", new FindingStore(database).Current(token).Findings));
        foreach (var workspace in Workspaces())
        {
            var member = _workspaces.Resolve(workspace.Members).FirstOrDefault(m => m.Id == projectId);
            var path = _levels.KnowledgePath(_levels.Load(), new("workspace", workspace.Name));
            if (member is null || !File.Exists(path))
                continue;
            try
            {
                using var db = HarnessDatabase.OpenReadOnly(path);
                groups.Add(new("workspace " + workspace.Name, member.Title.Length > 0 ? member.Title : member.Name, new FindingStore(db).Current(token).Findings));
            }
            catch (Exception error) when (ReadError(error)) { /* Context history is best effort, never a source scan. */ }
        }
        return groups;
    }
    public async Task<FindingsRead> ReadAsync(string scope, CancellationToken token)
    {
        if (scope is not ("" or "project" or "workspace"))
            throw new ArgumentException("scope must be project or workspace");
        List<FindingGroup> groups = [];
        var note = "";
        if (scope is "workspace")
        {
            foreach (var workspace in Workspaces())
            {
                var members = _workspaces.Resolve(workspace.Members);
                var found = (await new ArchitectureReader(members).SystemAsync(token, workspace.Name, _levels).ConfigureAwait(false)).Findings;
                var path = _levels.KnowledgePath(_levels.Load(), new("workspace", workspace.Name));
                using var db = File.Exists(path) ? HarnessDatabase.OpenReadOnly(path) : null;
                var annotated = db is null ? (found.Select(f => new Finding(ArchitectureAnalyzer.Complete(f))).ToArray(), (FindingSnapshot?)null) : new FindingStore(db).Annotate(found, token);
                groups.Add(new("workspace " + workspace.Name, "workspace:" + workspace.Name, annotated.Item1, annotated.Item2, members.Select(m => new WorkspaceMemberEvidence(m.Title.Length > 0 ? m.Title : m.Name, m.Root)).ToArray()));
            }
            if (groups.Count is 0)
                return new(groups, "this project is in no workspace ('harness workspace join')");
        }
        else
        {
            var (found, known) = await ProjectAsync(database is null, null, token).ConfigureAwait(false);
            if (!known)
                return new([], "no .NET, Go or TypeScript/JavaScript project found here");
            var annotated = database is null ? (found.Select(f => new Finding(f)).ToArray(), (FindingSnapshot?)null) : new FindingStore(database).Annotate(found, token);
            groups.Add(new("project", "project", annotated.Item1, annotated.Item2, [new("", root)]));
            if (database is null)
                note = "not initialized: no history or marks (harness init)";
        }
        if (!groups.Any(g => g.Snapshot is not null))
            note = (note.Length > 0 ? note + "; " : "") + "no snapshot yet: statuses come with harness sync";
        return new(groups, note);
    }
    public async Task<FindingProposed> ProposeAsync(FindingProposalRequest request, string by, CancellationToken token)
    {
        if (database is null)
            throw new InvalidOperationException("project not initialized (run 'harness init')");
        var proposal = new FindingProposal { Kind = request.Kind, Scope = "project", FindingKey = request.Finding.Trim(), Title = request.Title, Body = request.Reason, Evidence = request.Evidence ?? [], ProposedBy = by, Basis = "observation" };
        if (proposal.FindingKey.Length > 0)
        {
            var read = await ReadAsync(request.Scope, token).ConfigureAwait(false);
            var match = read.Groups.SelectMany(g => g.Findings.Where(f => f.Status is not "gone" && f.Key == proposal.FindingKey).Select(f => (Group: g, Finding: f))).FirstOrDefault();
            if (match.Finding is null)
                throw new InvalidOperationException($"no {(request.Scope.Length is 0 ? "project" : request.Scope)} finding \"{proposal.FindingKey}\" now (architecture_findings lists them; a finding between services needs scope workspace)");
            proposal = proposal with { Scope = match.Group.Scope, Basis = match.Finding.Basis, Evidence = match.Finding.Evidence ?? [], Title = proposal.Title.Length is 0 || proposal.Kind is "by_design" ? match.Finding.Title : proposal.Title };
        }
        else if (request.Scope is "workspace")
            throw new ArgumentException("an observation of your own is proposed for this project (scope project)");
        return new FindingStore(database).Propose(proposal, token);
    }
    public async Task<FindingProposal> ApproveAsync(string id, string by, string title = "", string body = "", CancellationToken token = default)
    {
        if (database is null)
            throw new InvalidOperationException("project not initialized");
        var proposal = new FindingStore(database).GetProposal(id, token);
        if (proposal.Status is not "pending")
            throw new InvalidOperationException(proposal.Id + " was already " + proposal.Status);
        var workspaceName = proposal.Scope.StartsWith("workspace:", StringComparison.Ordinal) ? proposal.Scope[10..] : "";
        if (workspaceName.Length > 0 && !Workspaces().Any(w => w.Name == workspaceName))
            throw new InvalidOperationException("this project is no longer in workspace " + workspaceName);
        var readScope = workspaceName.Length > 0 ? "workspace" : "project";
        var read = proposal.FindingKey.Length > 0 ? await ReadAsync(readScope, token).ConfigureAwait(false) : new FindingsRead([]);
        var group = read.Groups.FirstOrDefault(g => g.Scope == proposal.Scope);
        var current = group?.Findings.FirstOrDefault(f => f.Key == proposal.FindingKey && f.Status is not "gone");
        using var wider = workspaceName.Length > 0 ? _levels.OpenLevel(_levels.Load(), new("workspace", workspaceName), projectId, token) : null;
        var member = workspaceName.Length > 0 ? _workspaces.Resolve(Workspaces().Single(w => w.Name == workspaceName).Members).First(m => m.Id == projectId) : null;
        var memberName = member is null ? "" : MemberName(member);
        return new FindingReview(wider ?? database, database, projectId).Approve(proposal with { Title = title, Body = body }, current, by,
            workspaceName.Length > 0 ? "workspace " + workspaceName : "this project", memberName,
            wider is null ? null : MemoryScope.Project, token);
    }
    public async Task<IReadOnlyList<FindingSync>> SyncAsync(string commit, IProviderRegistry? providers, CancellationToken token)
    {
        if (database is null)
            throw new InvalidOperationException("project not initialized");
        List<FindingSync> results = [];
        try
        {
            var (found, known) = await ProjectAsync(true, providers, token).ConfigureAwait(false);
            if (known)
                results.Add(new("project", new FindingStore(database).Take(commit, found, token)));
        }
        catch (Exception error) when (ReadError(error)) { results.Add(new("project", null, error.Message)); }
        KnowledgeWorkspace[] workspaces;
        try
        { workspaces = Workspaces(); }
        catch (Exception error) when (ReadError(error)) { return results; }
        foreach (var workspace in workspaces)
            try
            {
                var members = _workspaces.Resolve(workspace.Members);
                var found = (await new ArchitectureReader(members).SystemAsync(token, workspace.Name, _levels).ConfigureAwait(false)).Findings;
                using var db = _levels.OpenLevel(_levels.Load(), new("workspace", workspace.Name), projectId, token);
                results.Add(new("workspace " + workspace.Name, new FindingStore(db).Take("", found, token)));
            }
            catch (Exception error) when (ReadError(error)) { results.Add(new("workspace " + workspace.Name, null, error.Message)); }
        return results;
    }
    private static string MemberName(WorkspaceMember member) => member.Title.Length > 0 ? member.Title : member.Name;
    private static bool ReadError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or System.Text.Json.JsonException or ArgumentException;
}
public sealed record FindingSync(string Where, FindingTake? Result, string Error = "");
