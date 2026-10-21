using Harness.Core.Memory;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Memory;

public sealed record MemoryPromotion(MemoryRecord From, string Level, IReadOnlyList<FoundMemory> HoldsIn, string Status, bool Retired = false, MemoryRecord? Memory = null)
{
    public IReadOnlyList<string> SimilarAtLevel { get; init; } = [];
}

/// <summary>Knowledge operations shared by MCP and CLI. The project database factory remains caller-owned.</summary>
public sealed class MemoryService(Func<CancellationToken, Task<HarnessDatabase>> existingDatabase, KnowledgeLevels levels, Func<CancellationToken, Task<string>> branch,
    Func<CancellationToken, Task<string>>? projectIdentity = null) : IMemorySession
{
    public async Task<Harness.Core.Retrieval.RetrievalMemories> SearchContextAsync(MemoryQuery query, CancellationToken cancellationToken)
    {
        var project = new MemoryStore(await existingDatabase(cancellationToken));
        using var opened = await LayersAsync(project, cancellationToken);
        var result = opened.Layers.Search(query, cancellationToken);
        return new(result.Memories) { Errors = result.Errors.Select(error => error.Scope + ": " + error.Error.Message).ToArray() };
    }

    public async Task<MemoryPutResult> PutMemoryAsync(MemoryInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var scope = input.Scope ?? MemoryScope.Project;
        if (scope.Wider)
        {
            var workspaces = levels.Load();
            var projectId = await ProjectIdAsync(cancellationToken);
            if (scope.Kind is "workspace" && projectId.Length is 0)
            {
                throw new InvalidOperationException("workspace memories need a registered project ('harness sync')");
            }

            using var database = levels.OpenLevel(workspaces, scope, projectId, cancellationToken);
            return AtLevel(new MemoryStore(database).Put(input with { Scope = MemoryScope.Project }, cancellationToken), scope);
        }
        var project = new MemoryStore(await existingDatabase(cancellationToken));
        var result = project.Put(input, cancellationToken);
        if (result.Status is not "created")
        {
            return result;
        }

        using var opened = await LayersAsync(project, cancellationToken);
        List<string> overrides = [];
        foreach (var level in opened.Wider)
        {
            try
            {
                overrides.AddRange(level.Store.Similar(input.Type, input.Content, cancellationToken)
                    .Select(similar => level.Scope + " " + similar.Memory.Id + " (" + MemoryText.Quote(similar.Memory.Content) + ")"));
            }
            catch (Exception exception) when (KnowledgeLevels.IsStorageError(exception)) { /* Best-effort explanatory hint. */ }
        }
        return overrides.Count is 0 ? result : result with { Hint = "In this project this memory overrides " + string.Join(", ", overrides) + "." };
    }

    public async Task<MemoryPutResult> SupersedeMemoryAsync(string id, MemoryInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.SupersedeReason))
        {
            throw new ArgumentException("reason is required: say why the old memory no longer holds");
        }

        var project = new MemoryStore(await existingDatabase(cancellationToken));
        using var opened = await LayersAsync(project, cancellationToken);
        var located = opened.Layers.Find(id, cancellationToken);
        var store = (MemoryStore)located.Store;
        if (located.Scope is { } scope)
        {
            if (input.Scope is not null && input.Scope != scope)
            {
                throw new InvalidOperationException($"{id} is a {scope} memory: supersede it at that level (scope {scope}), or store a project memory that overrides it");
            }

            return AtLevel(store.Supersede(id, input with { Scope = MemoryScope.Project }, cancellationToken), scope);
        }
        if (input.Scope is { Wider: true })
        {
            throw new InvalidOperationException($"a project memory moves to a wider level by promotion ('harness memory promote {id} --to {input.Scope}')");
        }

        return store.Supersede(id, input, cancellationToken);
    }

    public async Task<MemoryDetail> ReadMemoryAsync(string id, bool history, CancellationToken cancellationToken)
    {
        var project = new MemoryStore(await existingDatabase(cancellationToken));
        using var opened = await LayersAsync(project, cancellationToken);
        var located = opened.Layers.Find(id, cancellationToken);
        var detail = ((MemoryStore)located.Store).Details(id, history, cancellationToken);
        return located.Scope is null ? detail : detail with
        {
            Memory = detail.Memory with { Scope = located.Scope },
            History = detail.History.Select(memory => memory with { Scope = located.Scope }).ToArray(),
        };
    }

    public async Task<MemoryDetail> RetireAsync(string id, string by, string reason, CancellationToken cancellationToken)
    {
        var project = new MemoryStore(await existingDatabase(cancellationToken));
        using var opened = await LayersAsync(project, cancellationToken);
        var located = opened.Layers.Find(id, cancellationToken);
        var store = (MemoryStore)located.Store;
        store.Retire(id, by, reason, cancellationToken);
        var detail = store.Details(id, history: true, cancellationToken);
        // The Go CLI relabels the selected memory only; its history keeps the stored project scopes.
        return located.Scope is null ? detail : detail with { Memory = detail.Memory with { Scope = located.Scope } };
    }

    public async Task<MemoryDetail> ReadCliAsync(string id, CancellationToken cancellationToken)
    {
        var project = new MemoryStore(await existingDatabase(cancellationToken));
        using var opened = await LayersAsync(project, cancellationToken);
        var located = opened.Layers.Find(id, cancellationToken);
        var detail = ((MemoryStore)located.Store).Details(id, history: true, cancellationToken);
        return located.Scope is null ? detail : detail with { Memory = detail.Memory with { Scope = located.Scope } };
    }

    public async Task<MemorySearchResult> SearchMemoryAsync(MemoryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var currentBranch = query.Scope is null ? "" : await branch(cancellationToken);
        var project = new MemoryStore(await existingDatabase(cancellationToken));
        using var opened = await LayersAsync(project, cancellationToken);
        var result = opened.Layers.Search(query with { Branch = currentBranch }, cancellationToken);
        if (result.Errors.Count > 0 && result.Memories.Count is 0)
        {
            throw new IOException(string.Join('\n', result.Errors.Select(error => error.Scope + ": " + error.Error.Message)));
        }

        var output = new MemorySearchResult(result.Memories, query.Scope?.ToString() ?? "", currentBranch,
            string.Join('\n', result.Errors.Select(error => error.Scope + ": " + error.Error.Message)));
        if (result.Memories.Count is not 0 || !query.Types.Contains("convention", StringComparer.Ordinal))
        {
            return output;
        }

        var words = ConventionWords(query.Text);
        if (words.Length is 0)
        {
            return output;
        }

        FoundMemory[] elsewhere = [];
        try
        {
            elsewhere = KnowledgeLevels.Across(levels.OtherProjects(await ProjectIdAsync(cancellationToken)),
                store => store.Search(new MemoryQuery { Text = words, Types = ["convention"], Limit = 3 }, cancellationToken), cancellationToken).Take(5).ToArray();
        }
        catch (Exception exception) when (KnowledgeLevels.IsStorageError(exception)) { /* The undefined convention hint remains useful without other projects. */ }
        var hint = "No convention about this is defined for this project, its workspaces or the user. Ask the user which one holds and record it with memory_put type=convention (scope project, workspace:<name> or user)";
        if (elsewhere.Length > 0)
        {
            hint += "; other projects follow the conventions listed under elsewhere, which may be adopted";
        }

        return output with { Hint = hint + ".", Elsewhere = elsewhere };
    }

    public async Task<MemoryProposalResult> ProposeMemoryAsync(MemoryProposalInput input, CancellationToken cancellationToken) =>
        new MemoryStore(await existingDatabase(cancellationToken)).Propose(input, cancellationToken);

    public async Task<IReadOnlyList<MemoryProposal>> ListProposalsAsync(string status, int limit, CancellationToken cancellationToken) =>
        new MemoryStore(await existingDatabase(cancellationToken)).Proposals(status.Trim(), limit, cancellationToken);

    public async Task<MemoryApprovalResult> ApproveMemoryAsync(string id, MemoryApprovalInput input, CancellationToken cancellationToken) =>
        new MemoryStore(await existingDatabase(cancellationToken)).Approve(id, input, cancellationToken);

    public async Task<MemoryProposal> RejectMemoryAsync(string id, string by, string note, CancellationToken cancellationToken) =>
        new MemoryStore(await existingDatabase(cancellationToken)).Reject(id, by, note, cancellationToken);

    public async Task<MemoryPromotion> PromoteAsync(string id, MemoryScope scope, string by, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.Wider)
        {
            throw new ArgumentException("--to must be workspace:<name> or user");
        }

        var database = await existingDatabase(cancellationToken);
        var project = ProjectStore.Load(database) ?? throw new InvalidOperationException("project is not registered ('harness init')");
        var store = new MemoryStore(database);
        var memory = store.Get(id, cancellationToken);
        if (!memory.Current(new MemoryTimestamp(DateTimeOffset.UtcNow)))
        {
            throw new InvalidOperationException($"{id} is not current (superseded or retired)");
        }

        if (memory.Scope != MemoryScope.Project)
        {
            throw new InvalidOperationException($"{id} applies to {memory.Scope} only; promote project-wide memories");
        }

        var workspaces = levels.Load();
        var others = levels.OtherProjects(project.Id, scope.Kind is "workspace" ? scope.Value : null);
        var holds = KnowledgeLevels.Across(others, other => other.Similar(memory.Type, memory.Content, cancellationToken).Select(similar => similar.Memory).ToArray(), cancellationToken);
        var name = levels.ProjectName(project.Id, project.Root);
        var reference = "promoted from " + name + ":" + id;
        if (holds.Count > 0)
        {
            reference += "; also holds in " + string.Join(", ", holds.Select(found => found.Project + ":" + found.Memory.Id));
        }

        using var wider = levels.OpenLevel(workspaces, scope, project.Id, cancellationToken);
        var result = new MemoryStore(wider).Put(new MemoryInput { Type = memory.Type, Content = memory.Content, Source = new MemorySource("user", reference, by), Force = force }, cancellationToken);
        if (result.Status is "needs_decision")
        {
            return new MemoryPromotion(memory, scope.ToString(), holds, result.Status)
            {
                SimilarAtLevel = result.Similar.Select(similar => similar.Memory.Id + " " + similar.Memory.Content).ToArray(),
            };
        }

        store.Retire(id, by, "promoted to " + scope + " as " + result.Memory!.Id, cancellationToken);
        return new MemoryPromotion(memory, scope.ToString(), holds, result.Status, Retired: true, Memory: result.Memory with { Scope = scope });
    }

    private async Task<string> ProjectIdAsync(CancellationToken cancellationToken)
    {
        try
        { return projectIdentity is null ? ProjectStore.Load(await existingDatabase(cancellationToken))?.Id ?? "" : await projectIdentity(cancellationToken); }
        catch (Exception exception) when (KnowledgeLevels.IsStorageError(exception)) { return ""; }
    }

    private async Task<OpenKnowledgeLayers> LayersAsync(MemoryStore project, CancellationToken cancellationToken)
    {
        try
        { return levels.OpenLayers(project, await ProjectIdAsync(cancellationToken), cancellationToken); }
        catch (Exception exception) when (KnowledgeLevels.IsStorageError(exception)) { return new OpenKnowledgeLayers(project); }
    }

    private static MemoryPutResult AtLevel(MemoryPutResult result, MemoryScope scope) => result with
    {
        Memory = result.Memory is null ? null : result.Memory with { Scope = scope },
        Similar = result.Similar.Select(similar => similar with { Memory = similar.Memory with { Scope = scope } }).ToArray(),
    };

    private static readonly HashSet<string> ConventionStopwords = new(("this that these those with from into onto over under about when while then than " +
        "there their they them what which where have been being does done doing should would could will shall must " +
        "instead because before after also only just very some other another each every both either neither such " +
        "make makes made uses used using change changes changing work working task").Split(' '), StringComparer.Ordinal);

    private static string ConventionWords(string text)
    {
        var words = System.Text.RegularExpressions.Regex.Matches(text, "[A-Za-z][A-Za-z0-9]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking)
            .Select(match => MemoryText.Normalize(match.Value)).Where(word => word.Length >= 4 && !ConventionStopwords.Contains(word)).Distinct(StringComparer.Ordinal);
        return string.Join(' ', words);
    }
}
