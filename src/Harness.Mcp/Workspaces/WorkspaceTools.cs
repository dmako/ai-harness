using System.Text.Json;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Harness.Core.Workspaces;
using Harness.Mcp.Context;

namespace Harness.Mcp.Workspaces;

public sealed record ContextWorkspace(IReadOnlyList<object> Projects, IReadOnlyList<string> Notes);

public sealed class WorkspaceTools(
    IWorkspaceProjectSession project,
    Func<WorkspaceMembership, IWorkspaceSearchSession> create,
    IProviderRegistry? currentRegistry = null,
    IProviderProjectSession? freshness = null
) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private Entry? _current;
    private bool _disposed;

    private sealed class Entry(WorkspaceMembership membership, IWorkspaceSearchSession search)
    {
        internal WorkspaceMembership Membership { get; } = membership;
        internal IWorkspaceSearchSession Search { get; } = search;
        internal int Users { get; set; }
        internal bool Retired { get; set; }
        internal bool Disposing { get; set; }
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task<object> EvidenceAsync(string name, IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        var depth = arguments?.TryGetValue("depth", out var mode) is true ? mode.GetString() ?? "" : "";
        if (depth is not ("" or "light" or "deep"))
        {
            throw new ArgumentException("depth must be light or deep");
        }

        var key = name is "library_adoption" ? "library" : "concern";
        var text = arguments?.TryGetValue(key, out var value) is true ? value.GetString() ?? "" : "";
        if (name is "similar_concerns" && string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("concern must not be empty: describe it in words");
        }

        return UseAsync<object>(
            async search =>
            {
                if (search is not IWorkspaceEvidenceSession evidence)
                {
                    throw new InvalidOperationException("workspace evidence session unavailable");
                }

                return name is "library_adoption"
                    ? await evidence.AdoptionAsync(text.Trim(), token).ConfigureAwait(false)
                    : await evidence.ConcernAsync(text, depth is "deep", token).ConfigureAwait(false);
            },
            token
        );
    }

    private async Task<T> UseAsync<T>(Func<IWorkspaceSearchSession, Task<T>> action, CancellationToken token)
    {
        var membership = await project.ReadWorkspaceMembershipAsync(token).ConfigureAwait(false);
        Entry entry;
        Entry? retire = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (
                _current is null
                || _current.Membership.Current != membership.Current
                || !_current.Membership.Members.SequenceEqual(membership.Members)
            )
            {
                if (_current is { } old)
                {
                    old.Retired = true;
                    if (old.Users is 0)
                    {
                        retire = old;
                    }
                }
                _current = new(membership, create(membership));
                _entries.Add(_current);
            }
            entry = _current;
            entry.Users++;
        }
        try
        {
            if (retire is not null)
            {
                await CloseAsync(retire).ConfigureAwait(false);
            }

            return await action(entry.Search).ConfigureAwait(false);
        }
        finally
        {
            var close = false;
            lock (_gate)
            {
                entry.Users--;
                close = entry.Retired && entry.Users is 0;
            }
            if (close)
            {
                await CloseAsync(entry).ConfigureAwait(false);
            }
        }
    }

    private async Task CloseAsync(Entry entry)
    {
        lock (_gate)
        {
            if (entry.Disposing)
            {
                return;
            }

            entry.Disposing = true;
        }
        try
        {
            await entry.Search.DisposeAsync().ConfigureAwait(false);
            entry.Closed.TrySetResult();
        }
        catch (Exception error)
        {
            entry.Closed.TrySetException(error);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _entries.Remove(entry);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        Entry[] idle;
        lock (_gate)
        {
            _disposed = true;
            entries = _entries.ToArray();
            foreach (var entry in entries)
            {
                entry.Retired = true;
            }

            idle = entries.Where(entry => entry.Users is 0).ToArray();
        }
        foreach (var entry in idle)
        {
            await CloseAsync(entry).ConfigureAwait(false);
        }

        await Task.WhenAll(entries.Select(entry => entry.Closed.Task)).ConfigureAwait(false);
    }

    public Task<object> SearchAsync(
        string query,
        string kind,
        IReadOnlyList<string> capabilities,
        string path,
        int limit,
        bool deep,
        bool debug,
        CancellationToken token
    ) =>
        UseAsync<object>(
            async search =>
            {
                var reference = WorkspaceReferences.Parse(path, search.Members);
                var only = reference?.Project ?? "";
                path = reference?.Path.TrimEnd('/') ?? path;
                if (path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(path))
                {
                    throw new ArgumentException(
                        "path must be relative to the project root (<project>:<relative path>)"
                    );
                }

                List<object> results = [],
                    calls = [];
                List<string> errors = [],
                    notes = [];
                var impact = kind is "impact" && only.Length is 0;
                SymbolHome home = new();
                var hits = new List<WorkspaceHit>();
                if (impact)
                {
                    var answer = await search.ImpactAsync(query, limit, deep, token).ConfigureAwait(false);
                    home = answer.Home;
                    hits.AddRange(answer.Hits);
                    errors.AddRange(answer.Errors);
                    notes.AddRange(answer.Notes);
                }
                else
                {
                    foreach (var capability in capabilities)
                    {
                        var answer = await search
                            .QueryAsync(new(capability, query, path, limit), deep, only, token)
                            .ConfigureAwait(false);
                        hits.AddRange(answer.Hits);
                        AddUnique(errors, answer.Errors);
                        AddUnique(notes, answer.Notes);
                        if (debug)
                        {
                            calls.AddRange(
                                answer.Calls.Select(call =>
                                {
                                    var row = new Dictionary<string, object?>
                                    {
                                        ["provider"] = call.Project + "/" + call.Result.Provider,
                                        ["capability"] = call.Result.Capability,
                                        ["items"] = call.Result.Items.Count,
                                        ["latency_ms"] = (long)call.Result.Latency.TotalMilliseconds,
                                        ["bytes"] = 0,
                                    };
                                    if (call.Result.Error is { } error)
                                    {
                                        row["error"] = error.Message;
                                    }

                                    return row;
                                })
                            );
                        }
                    }
                }

                var stale =
                    freshness is not null
                    && (await freshness.ReadProviderProjectAsync(token).ConfigureAwait(false)).CrossBranch;
                var indexed = stale
                    ? CapabilitiesOfCurrent(deep)
                        .Where(info => info.Freshness is "indexed")
                        .Select(info => info.Provider)
                        .ToHashSet(StringComparer.Ordinal)
                    : [];
                foreach (
                    var hit in hits.Where(hit =>
                        hit.Member.Id != search.Current.Id || !indexed.Contains(hit.Item.Provider)
                    )
                )
                {
                    results.Add(Item(hit, debug, impact));
                }

                var head =
                    (impact ? "workspace impact: " : "workspace: ")
                    + string.Join(", ", search.Members.Select(member => member.Name))
                    + (deep ? " (deep)" : "");
                if (impact)
                {
                    head += "; " + Describe(home, query, search.Current.Name);
                }

                var output = new Dictionary<string, object?>
                {
                    ["query"] = query,
                    ["results"] = results,
                    ["note"] = string.Join("; ", new[] { head }.Concat(notes)),
                };
                if (errors.Count > 0)
                {
                    output["errors"] = errors;
                }

                if (calls.Count > 0)
                {
                    output["calls"] = calls;
                }

                return output;
            },
            token
        );

    public Task<ContextWorkspace> ContextAsync(
        string task,
        IReadOnlyList<string> symbols,
        IReadOnlyList<string> phrases,
        bool deep,
        int reserve,
        CancellationToken token
    ) =>
        UseAsync(
            search => ContextOfAsync(search, task, symbols.Concat(phrases).ToArray(), deep, reserve, token),
            token
        );

    /// <summary>Other members' code named like the task's names, then their memories, while they fit.</summary>
    private static async Task<ContextWorkspace> ContextOfAsync(
        IWorkspaceSearchSession search,
        string task,
        string[] names,
        bool deep,
        int reserve,
        CancellationToken token
    )
    {
        List<string> notes = [];
        var hits = await ContextHitsAsync(search, names, deep, notes, token).ConfigureAwait(false);
        var budget = new ContextBudget(reserve);
        foreach (var (hit, _) in hits.OrderByDescending(hit => hit.Score))
        {
            budget.AddCode(hit);
        }
        foreach (var member in search.Members.Where(member => budget.Members.ContainsKey(member.Id)))
        {
            budget.AddMemories(
                member.Id,
                await search
                    .ContextMemoriesAsync(member, budget.Paths[member.Id], TaskParsing.ContentWords(task), token)
                    .ConfigureAwait(false)
            );
        }
        var output = search
            .Members.Where(member => budget.Groups.ContainsKey(member.Id))
            .Select(member => (object)budget.Groups[member.Id])
            .ToList();
        if (budget.Dropped > 0)
        {
            notes.Add(
                $"workspace: {budget.Dropped} more match(es) or decision(s) did not fit in its share of the budget"
            );
        }

        return new ContextWorkspace(output, notes);
    }

    /// <summary>Other members' symbols matching a name, each once, with the answers' notes and errors.</summary>
    private static async Task<List<(WorkspaceHit Hit, int Score)>> ContextHitsAsync(
        IWorkspaceSearchSession search,
        string[] names,
        bool deep,
        List<string> notes,
        CancellationToken token
    )
    {
        List<(WorkspaceHit Hit, int Score)> hits = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var query in names)
        {
            var answer = await search
                .QueryAsync(new(Capabilities.SymbolSearch, query, Limit: 5), deep, "", token)
                .ConfigureAwait(false);
            foreach (var hit in answer.Hits)
            {
                if (
                    hit.Member.Id == search.Current.Id
                    || !seen.Add(hit.Member.Name + ":" + hit.Item.Path + "#" + hit.Item.Name)
                )
                {
                    continue;
                }

                var score = Score(names, hit);
                if (score > 0)
                {
                    hits.Add((hit, score));
                }
            }
            AddUnique(notes, answer.Notes.Concat(answer.Errors));
        }
        return hits;
    }

    /// <summary>2 when the hit's last name segment is a wanted one, 1 when it contains one of 4+ letters.</summary>
    private static int Score(string[] names, WorkspaceHit hit)
    {
        var name = WorkspaceReferences.LastSegment(hit.Item.Name ?? "");
        if (
            names.Any(want =>
                string.Equals(WorkspaceReferences.LastSegment(want), name, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return 2;
        }

        return names.Any(want =>
            WorkspaceReferences.LastSegment(want).Length >= 4
            && name.Contains(WorkspaceReferences.LastSegment(want), StringComparison.OrdinalIgnoreCase)
        )
            ? 1
            : 0;
    }

    /// <summary>The context's project groups, filled in order while their JSON fits in the reserve.</summary>
    private sealed class ContextBudget(int reserve)
    {
        private readonly List<object> _output = [];
        internal Dictionary<string, Dictionary<string, object?>> Groups { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, List<string>> Paths { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, WorkspaceMember> Members { get; } = new(StringComparer.Ordinal);
        internal int Dropped { get; private set; }

        /// <summary>Adds the hit to its member's group, opening the group, unless that overflows the reserve.</summary>
        internal void AddCode(WorkspaceHit hit)
        {
            var opened = !Groups.TryGetValue(hit.Member.Id, out var group);
            if (opened)
            {
                group = new() { ["project"] = hit.Member.Name, ["code"] = new List<object>() };
                _output.Add(group);
                Groups.Add(hit.Member.Id, group);
            }
            var code = (List<object>)group!["code"]!;
            var item = Item(hit, false, false);
            item.Remove("project");
            item.Remove("snippet");
            item.Remove("depth");
            code.Add(item);
            if (ContextJson.Size(_output) > reserve)
            {
                code.RemoveAt(code.Count - 1);
                Dropped++;
                if (opened)
                {
                    _output.RemoveAt(_output.Count - 1);
                    Groups.Remove(hit.Member.Id);
                }
                return;
            }
            Members[hit.Member.Id] = hit.Member;
            if (!Paths.TryGetValue(hit.Member.Id, out var files))
            {
                files = [];
                Paths.Add(hit.Member.Id, files);
            }
            if (!files.Contains(hit.Item.Path ?? "", StringComparer.Ordinal))
            {
                files.Add(hit.Item.Path ?? "");
            }
        }

        /// <summary>Adds the member's memories that still fit in the reserve.</summary>
        internal void AddMemories(string member, IReadOnlyList<MemoryRecord> found)
        {
            var group = Groups[member];
            List<object> memories = [];
            foreach (var memory in found)
            {
                var row = new Dictionary<string, object?>
                {
                    ["id"] = memory.Id,
                    ["type"] = memory.Type,
                    ["content"] = memory.Content,
                };
                if (memory.Scope.Kind is "path" && memory.Scope.Value.Length > 0)
                {
                    row["path"] = memory.Scope.Value;
                }

                memories.Add(row);
                group["memory"] = memories;
                if (ContextJson.Size(_output) > reserve)
                {
                    memories.RemoveAt(memories.Count - 1);
                    if (memories.Count is 0)
                    {
                        group.Remove("memory");
                    }

                    Dropped++;
                }
            }
        }
    }

    private IReadOnlyList<CapabilityInfo> CapabilitiesOfCurrent(bool deep) =>
        !deep && currentRegistry is IContextProviderRegistry context
            ? context.ContextCapabilities()
            : currentRegistry?.Capabilities() ?? [];

    private static void AddUnique(List<string> destination, IEnumerable<string> values)
    {
        foreach (var value in values.Where(value => !destination.Contains(value, StringComparer.Ordinal)))
        {
            destination.Add(value);
        }
    }

    private static Dictionary<string, object?> Item(WorkspaceHit hit, bool debug, bool impact)
    {
        var item = hit.Item;
        var row = new Dictionary<string, object?>
        {
            ["project"] = hit.Member.Name,
            ["kind"] = item.Kind,
            ["provider"] = item.Provider,
        };
        Add("name", item.Name);
        Add("label", item.Label);
        Add("path", item.Path);
        Add("snippet", item.Snippet);
        if (item.StartLine is not 0)
        {
            row["start_line"] = item.StartLine;
        }

        if (item.EndLine is not 0)
        {
            row["end_line"] = item.EndLine;
        }

        if (item.Depth is not 0)
        {
            row["depth"] = item.Depth;
        }

        if (debug)
        {
            Add("qualified_name", item.QualifiedName);
            Add("tool", item.Tool);
            if (item.Score is not 0)
            {
                row["score"] = item.Score;
            }
        }
        if (impact)
        {
            row["via"] = hit.Via;
            row["evidence"] = hit.Exact ? "exact" : "approximate";
        }
        return row;
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                row[name] = value;
            }
        }
    }

    private static string Describe(SymbolHome home, string query, string current)
    {
        var shortName = WorkspaceReferences.LastSegment(query);
        var description = home.Path.Length is 0
            ? shortName + " was not found in " + current + ", so other members are matched by name only"
            : shortName + " is defined in " + current + ":" + home.Path;
        if (home.Path.Length > 0)
        {
            if (home.Namespace.Length > 0)
            {
                description += ", namespace " + home.Namespace;
            }

            if (home.Package.Length > 0)
            {
                description += ", package " + home.Package;
            }
        }
        return description
            + "; other members cannot resolve this symbol: their results are references to the name, package dependents, namespace users and name matches, approximate unless marked exact";
    }
}
