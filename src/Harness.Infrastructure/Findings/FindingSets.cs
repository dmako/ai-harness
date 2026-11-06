using Harness.Core.Architecture;
using Harness.Core.Findings;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Architecture.Clones;
using Harness.Infrastructure.Architecture.Complexity;
using Harness.Infrastructure.Architecture.Metrics;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Workspaces;

namespace Harness.Infrastructure.Findings;

/// <summary>Request-scoped project/workspace finding sets, shared by MCP and sync.</summary>
public sealed class FindingSets(
    string root,
    string projectId,
    HarnessDatabase? database,
    string globalConfigurationPath,
    Projects.MachineRegistry registry,
    ArchitectureRead? projectRead = null
)
{
    private readonly KnowledgeLevels _levels = new(globalConfigurationPath, registry);
    private readonly WorkspaceStore _workspaces = new(globalConfigurationPath, registry);
    private WorkspaceMember Current => new("", projectId, root, database?.Path ?? "");

    /// <summary>What a sync's project read read of every workspace member, for its workspace findings (TASK-0108).</summary>
    internal WorkspaceEvidenceReader? Evidence { get; private set; }

    private KnowledgeWorkspace[] Workspaces() =>
        _levels
            .Load()
            .Where(w => projectId.Length > 0 && w.Members.Contains(projectId, StringComparer.Ordinal))
            .ToArray();

    private async Task<(IReadOnlyList<ArchitectureFinding> Found, bool Known)> ProjectAsync(
        bool full,
        IProviderRegistry? providers,
        Task? refreshed,
        CancellationToken token
    )
    {
        var members = ReadMembers(full);
        var evidence = new WorkspaceEvidenceReader(members);
        if (full)
        {
            Evidence = evidence;
        }

        // A sync lexes only the files changed since the last one (TASK-0105).
        var reader = new ArchitectureReader(members, evidence, Clones(full));
        var own = database is null ? null : new MemoryStore(database);
        var rules = ArchitectureReader.RulesFor(own, _levels, projectId, token);
        var read = projectRead ?? await reader.ProjectAsync(Current, rules, full, token).ConfigureAwait(false);
        var found = (read.Architecture?.Findings ?? []).ToList();
        var stored = database is null ? [] : new FindingStore(database).Current(token).Findings;
        if (!full)
        {
            found.AddRange(SyncOnly(read.Architecture?.Projects ?? [], found, stored));
        }
        else if (read.Architecture is not null)
        {
            found.AddRange(
                await SyncedAsync(reader, own, stored, providers, refreshed, token).ConfigureAwait(false)
            );
        }
        return (FindingStore.Merge(found), read.Architecture is not null);
    }

    /// <summary>
    /// The clone cache, with a full read (a sync) given the file hashes sync just recorded for this worktree: files
    /// whose hash is unchanged are neither read nor lexed again.
    /// </summary>
    private CloneCache Clones(bool full)
    {
        var cache = CloneCache.For(globalConfigurationPath);
        if (!full || database is null || root.Length is 0)
        {
            return cache;
        }

        try
        {
            var here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (
                Projects.ProjectStore.Worktrees(database)
                    .FirstOrDefault(tree =>
                        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(tree.Path)), here, comparison)
                    )
                is { } worktree
            )
            {
                cache.Record(root, Sync.SyncStore.FileHashes(database, worktree.Id));
            }
        }
        catch (Exception error) when (ReadError(error))
        { /* Without recorded hashes every file is read and hashed. */
        }

        return cache;
    }

    /// <summary>This project, with a full read also its workspaces' other members.</summary>
    private IReadOnlyList<WorkspaceMember> ReadMembers(bool full)
    {
        var members = full ? _workspaces.Members(projectId) : [];
        return members.Any(m => m.Id == projectId) ? members : members.Append(Current).ToArray();
    }

    /// <summary>
    /// The stored findings a quick read cannot recompute: providers', clones', complexity, stale knowledge
    /// and published packages' unused code.
    /// </summary>
    private static IEnumerable<ArchitectureFinding> SyncOnly(
        IEnumerable<LayeredUnit> projects,
        List<ArchitectureFinding> found,
        IReadOnlyList<Finding> stored
    )
    {
        var have = found.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var published = projects
            .Where(p => !string.IsNullOrEmpty(p.Package))
            .Select(p => p.Path)
            .ToHashSet(StringComparer.Ordinal);
        return stored.Where(f =>
            f.Source.Provider is not ("" or "harness")
            || f.Source.Tool is "internal/clones" or "internal/complexity" or ComplexityReader.Measured
            || f.Kind is "stale-knowledge"
            || f.Kind is "unused" && published.Contains(f.Project ?? "") && !have.Contains(f.Key)
        );
    }

    /// <summary>What only a sync computes: knowledge drift, providers' cycles and duplicates, complexity.</summary>
    private async Task<List<ArchitectureFinding>> SyncedAsync(
        ArchitectureReader reader,
        MemoryStore? own,
        IReadOnlyList<Finding> stored,
        IProviderRegistry? providers,
        Task? refreshed,
        CancellationToken token
    )
    {
        // The providers (mostly their start) answer while harness detects its own repeated code (TASK-0102).
        var asked = Task.Run(() => ProviderFindingsAsync(stored, providers, refreshed, token), token);
        List<ArchitectureFinding> found = [];
        try
        {
            var repo = await reader.RepositoryAsync(Current, token).ConfigureAwait(false);
            if (own is not null)
            {
                found.AddRange(
                    KnowledgeDrift.Read(
                        repo,
                        own.Search(new() { Types = ["constraint", "decision", "convention"], Limit = 2000 }, token),
                        token
                    )
                );
            }

            await reader.PrepareRepeatedAsync(Current, token).ConfigureAwait(false);
        }
        catch
        {
            // The registry outlives no call: wait for the providers, the first error is the one reported.
            await ((Task)asked).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        var fresh = await asked.ConfigureAwait(false);
        found.AddRange(await reader.RepeatedAsync(Current, fresh, token).ConfigureAwait(false));
        var complexity = Complexity();
        var exact = await ExactAsync(complexity, token).ConfigureAwait(false);
        found.AddRange(
            await reader.ComplexityAsync(Current, complexity.Thresholds(), exact, token).ConfigureAwait(false)
        );
        return found;
    }

    /// <summary>
    /// Providers' cycles and duplicates, asked once their refresh (<paramref name="refreshed"/>, run alongside by the
    /// sync) is over; stored ones stay for a kind no provider answered.
    /// </summary>
    private static async Task<List<ArchitectureFinding>> ProviderFindingsAsync(
        IReadOnlyList<Finding> stored,
        IProviderRegistry? providers,
        Task? refreshed,
        CancellationToken token
    )
    {
        if (refreshed is not null)
        {
            // Refreshed or failed alike: a failed refresh is reported by the sync, the questions go to the old index.
            await refreshed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            token.ThrowIfCancellationRequested();
        }

        if (providers is not null)
        {
            await providers.DiscoverAsync(token).ConfigureAwait(false);
        }

        var cycles = await ArchitectureProviders.CyclesAsync(providers, token).ConfigureAwait(false);
        var duplicates = await RepeatedCodeReader.ProviderDuplicatesAsync(providers, token).ConfigureAwait(false);
        var fresh = cycles.Findings.Concat(duplicates.Findings).ToList();
        fresh.AddRange(
            stored.Where(f =>
                f.Source.Provider is not "harness"
                && !(f.Kind is "cycle" && cycles.Answered || f.Kind is "duplicate" && duplicates.Answered)
            )
        );
        return fresh;
    }

    private ComplexityConfiguration Complexity()
    {
        try
        {
            var project = root.Length is 0 ? "" : ConfigurationPaths.ProjectPath(root);
            return ConfigurationLoader.LoadFiles(globalConfigurationPath, project).Value.Architecture.Complexity;
        }
        catch (InvalidDataException)
        { /* The commands that load configuration report its errors; findings keep the default thresholds. */
            return new();
        }
    }

    /// <summary>Exact .NET values measured for HEAD, if the worker is enabled and its run for HEAD is done.</summary>
    private async Task<IReadOnlyList<MeasuredMember>?> ExactAsync(
        ComplexityConfiguration complexity,
        CancellationToken token
    )
    {
        if (!complexity.Metrics.Enabled || projectId.Length is 0 || root.Length is 0)
        {
            return null;
        }

        try
        {
            var head = await (await GitRepository.DiscoverAsync(root, token).ConfigureAwait(false))
                .ReadHeadAsync(token)
                .ConfigureAwait(false);
            return MetricsStore.For(globalConfigurationPath, projectId).Load(head.Commit) is { State: "done" } run
                ? run.Members
                : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        { /* Without HEAD the count from source stands. */
            return null;
        }
    }

    public IReadOnlyList<FindingContextGroup> Context(CancellationToken token)
    {
        List<FindingContextGroup> groups = [];
        if (database is not null)
        {
            groups.Add(new("", "", new FindingStore(database).Current(token).Findings));
        }

        foreach (var workspace in Workspaces())
        {
            var member = _workspaces.Resolve(workspace.Members).FirstOrDefault(m => m.Id == projectId);
            var path = _levels.KnowledgePath(_levels.Load(), new("workspace", workspace.Name));
            if (member is null || !File.Exists(path))
            {
                continue;
            }

            try
            {
                using var db = HarnessDatabase.OpenReadOnly(path);
                groups.Add(
                    new(
                        "workspace " + workspace.Name,
                        member.Title.Length > 0 ? member.Title : member.Name,
                        new FindingStore(db).Current(token).Findings
                    )
                );
            }
            catch (Exception error) when (ReadError(error))
            { /* Context history is best effort, never a source scan. */
            }
        }
        return groups;
    }

    public async Task<FindingsRead> ReadAsync(string scope, CancellationToken token)
    {
        if (scope is not ("" or "project" or "workspace"))
        {
            throw new ArgumentException("scope must be project or workspace");
        }

        List<FindingGroup> groups = [];
        var note = "";
        if (scope is "workspace")
        {
            foreach (var workspace in Workspaces())
            {
                var members = _workspaces.Resolve(workspace.Members);
                var found = (
                    await new ArchitectureReader(members)
                        .SystemAsync(token, workspace.Name, _levels)
                        .ConfigureAwait(false)
                ).Findings;
                var path = _levels.KnowledgePath(_levels.Load(), new("workspace", workspace.Name));
                using var db = File.Exists(path) ? HarnessDatabase.OpenReadOnly(path) : null;
                var annotated = db is null
                    ? (
                        found.Select(f => new Finding(ArchitectureAnalyzer.Complete(f))).ToArray(),
                        (FindingSnapshot?)null
                    )
                    : new FindingStore(db).Annotate(found, token);
                groups.Add(
                    new(
                        "workspace " + workspace.Name,
                        "workspace:" + workspace.Name,
                        annotated.Item1,
                        annotated.Item2,
                        members
                            .Select(m => new WorkspaceMemberEvidence(m.Title.Length > 0 ? m.Title : m.Name, m.Root))
                            .ToArray()
                    )
                );
            }
            if (groups.Count is 0)
            {
                return new(groups, "this project is in no workspace ('harness workspace join')");
            }
        }
        else
        {
            var (found, known) = await ProjectAsync(database is null, null, null, token).ConfigureAwait(false);
            if (!known)
            {
                return new([], "no .NET, Go or TypeScript/JavaScript project found here");
            }

            var annotated = database is null
                ? (found.Select(f => new Finding(f)).ToArray(), (FindingSnapshot?)null)
                : new FindingStore(database).Annotate(found, token);
            groups.Add(new("project", "project", annotated.Item1, annotated.Item2, [new("", root)]));
            if (database is null)
            {
                note = "not initialized: no history or marks (harness init)";
            }
        }
        if (!groups.Any(g => g.Snapshot is not null))
        {
            note = (note.Length > 0 ? note + "; " : "") + "no snapshot yet: statuses come with harness sync";
        }

        return new(groups, note);
    }

    public async Task<FindingProposed> ProposeAsync(FindingProposalRequest request, string by, CancellationToken token)
    {
        if (database is null)
        {
            throw new InvalidOperationException("project not initialized (run 'harness init')");
        }

        var proposal = new FindingProposal
        {
            Kind = request.Kind,
            Scope = "project",
            FindingKey = request.Finding.Trim(),
            Title = request.Title,
            Body = request.Reason,
            Evidence = request.Evidence ?? [],
            ProposedBy = by,
            Basis = "observation",
        };
        if (proposal.FindingKey.Length > 0)
        {
            var read = await ReadAsync(request.Scope, token).ConfigureAwait(false);
            var match = read
                .Groups.SelectMany(g =>
                    g.Findings.Where(f => f.Status is not "gone" && f.Key == proposal.FindingKey)
                        .Select(f => (Group: g, Finding: f))
                )
                .FirstOrDefault();
            if (match.Finding is null)
            {
                throw new InvalidOperationException(
                    $"no {(request.Scope.Length is 0 ? "project" : request.Scope)} finding \"{proposal.FindingKey}\" now (architecture_findings lists them; a finding between services needs scope workspace)"
                );
            }

            proposal = proposal with
            {
                Scope = match.Group.Scope,
                Basis = match.Finding.Basis,
                Evidence = match.Finding.Evidence ?? [],
                Title =
                    proposal.Title.Length is 0 || proposal.Kind is "by_design" ? match.Finding.Title : proposal.Title,
            };
        }
        else if (request.Scope is "workspace")
        {
            throw new ArgumentException("an observation of your own is proposed for this project (scope project)");
        }

        return new FindingStore(database).Propose(proposal, token);
    }

    public async Task<FindingProposal> ApproveAsync(
        string id,
        string by,
        string title = "",
        string body = "",
        CancellationToken token = default
    )
    {
        if (database is null)
        {
            throw new InvalidOperationException("project not initialized");
        }

        var proposal = new FindingStore(database).GetProposal(id, token);
        if (proposal.Status is not "pending")
        {
            throw new InvalidOperationException(proposal.Id + " was already " + proposal.Status);
        }

        var workspaceName = proposal.Scope.StartsWith("workspace:", StringComparison.Ordinal)
            ? proposal.Scope[10..]
            : "";
        if (workspaceName.Length > 0 && !Workspaces().Any(w => w.Name == workspaceName))
        {
            throw new InvalidOperationException("this project is no longer in workspace " + workspaceName);
        }

        var readScope = workspaceName.Length > 0 ? "workspace" : "project";
        var read =
            proposal.FindingKey.Length > 0
                ? await ReadAsync(readScope, token).ConfigureAwait(false)
                : new FindingsRead([]);
        var group = read.Groups.FirstOrDefault(g => g.Scope == proposal.Scope);
        var current = group?.Findings.FirstOrDefault(f => f.Key == proposal.FindingKey && f.Status is not "gone");
        using var wider =
            workspaceName.Length > 0
                ? _levels.OpenLevel(_levels.Load(), new("workspace", workspaceName), projectId, token)
                : null;
        var member =
            workspaceName.Length > 0
                ? _workspaces
                    .Resolve(Workspaces().Single(w => w.Name == workspaceName).Members)
                    .First(m => m.Id == projectId)
                : null;
        var memberName = member is null ? "" : MemberName(member);
        return new FindingReview(wider ?? database, database, projectId).Approve(
            proposal with
            {
                Title = title,
                Body = body,
            },
            current,
            by,
            workspaceName.Length > 0 ? "workspace " + workspaceName : "this project",
            memberName,
            wider is null ? null : MemoryScope.Project,
            token
        );
    }

    public async Task<IReadOnlyList<FindingSync>> SyncAsync(
        string commit,
        IProviderRegistry? providers,
        Task? refreshed,
        CancellationToken token
    )
    {
        if (database is null)
        {
            throw new InvalidOperationException("project not initialized");
        }

        List<FindingSync> results = [];
        try
        {
            var (found, known) = await ProjectAsync(true, providers, refreshed, token).ConfigureAwait(false);
            if (known)
            {
                results.Add(new("project", new FindingStore(database).Take(commit, found, token)));
            }
        }
        catch (Exception error) when (ReadError(error))
        {
            results.Add(new("project", null, error.Message));
        }
        KnowledgeWorkspace[] workspaces;
        try
        {
            workspaces = Workspaces();
        }
        catch (Exception error) when (ReadError(error))
        {
            return results;
        }
        foreach (var workspace in workspaces)
        {
            try
            {
                results.Add(await WorkspaceAsync(workspace, token).ConfigureAwait(false));
            }
            catch (Exception error) when (ReadError(error))
            {
                results.Add(new("workspace " + workspace.Name, null, error.Message));
            }
        }

        return results;
    }

    /// <summary>
    /// A workspace's findings, computed from the project read's evidence, or kept as stored when no member's
    /// inputs changed since they were taken (<see cref="WorkspaceInputs"/>).
    /// </summary>
    private async Task<FindingSync> WorkspaceAsync(KnowledgeWorkspace workspace, CancellationToken token)
    {
        var where = "workspace " + workspace.Name;
        var members = _workspaces.Resolve(workspace.Members);
        var level = new MemoryScope("workspace", workspace.Name);
        var knowledge = _levels.Load();
        using var db = _levels.OpenLevel(knowledge, level, projectId, token);
        var stamp = _levels.KnowledgePath(knowledge, level) + ".inputs";
        var inputs = await WorkspaceInputs
            .FingerprintAsync(members, db, globalConfigurationPath, token)
            .ConfigureAwait(false);
        var store = new FindingStore(db);
        if (inputs is not null && WorkspaceInputs.Stored(stamp) == inputs)
        {
            return new(where, null) { Kept = store.Current(token).Findings.Count };
        }

        var evidence = Evidence?.For(members) ?? new WorkspaceEvidenceReader(members);
        var found = (
            await new ArchitectureReader(members, evidence)
                .SystemAsync(token, workspace.Name, _levels)
                .ConfigureAwait(false)
        ).Findings;
        var taken = store.Take("", found, token);
        WorkspaceInputs.Store(stamp, inputs);
        return new(where, taken);
    }

    private static string MemberName(WorkspaceMember member) => member.Title.Length > 0 ? member.Title : member.Name;

    private static bool ReadError(Exception error) =>
        error
            is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or Microsoft.Data.Sqlite.SqliteException
                or InvalidOperationException
                or System.Text.Json.JsonException
                or ArgumentException;
}

public sealed record FindingSync(string Where, FindingTake? Result, string Error = "")
{
    /// <summary>Set when the stored findings were kept because no input changed: how many there are.</summary>
    public int? Kept { get; init; }
}
