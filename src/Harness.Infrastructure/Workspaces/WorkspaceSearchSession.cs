using System.Collections.Concurrent;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Workspaces;

public sealed partial class WorkspaceSearchSession(WorkspaceMembership membership, IProviderRegistry? current,
    Func<WorkspaceMember, bool, IProviderRegistry> build, TimeSpan lightCap = default, TimeSpan deepCap = default, TimeSpan warmRetry = default) : IWorkspaceSearchSession, IWorkspaceEvidenceSession
{
    private readonly ConcurrentDictionary<(string Root, bool Deep), Lazy<IProviderRegistry>> _registries = new();
    private readonly object _gate = new();
    private readonly List<IAsyncDisposable> _owned = [];
    private bool _disposed;
    public WorkspaceMember Current => membership.Current;
    public IReadOnlyList<WorkspaceMember> Members => membership.Members;
    private IProviderRegistry Registry(WorkspaceMember member, bool deep)
    {
        if (member.Id == Current.Id && current is not null)
        {
            return current;
        }

        return _registries.GetOrAdd((member.Root, deep), _ => new(() =>
        {
            var registry = build(member, deep);
            lock (_gate)
            {
                if (_disposed)
                {
                    if (registry is IAsyncDisposable disposable)
                    {
                        Observe(disposable.DisposeAsync().AsTask());
                    }

                    throw new ObjectDisposedException(nameof(WorkspaceSearchSession));
                }
                if (registry is IAsyncDisposable owned)
                {
                    _owned.Add(owned);
                }
            }
            return registry;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
    public async Task<WorkspaceAnswer> QueryAsync(ProviderRequest request, bool deep, string only, CancellationToken cancellationToken)
    {
        var cap = deep ? deepCap : lightCap;
        if (cap <= TimeSpan.Zero)
        {
            cap = TimeSpan.FromSeconds(deep ? 60 : 10);
        }

        using var deadline = new CancellationTokenSource(cap);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        var members = Members.Where(member => only.Length is 0 || member.Name == only).OrderByDescending(member => member.Id == Current.Id).ThenBy(member => member.Name, WorkspaceNameComparer.Instance).ToArray();
        var tasks = members.Select(member => Task.Run(() => AskAsync(member), CancellationToken.None)).ToArray();
        var all = Task.WhenAll(tasks);
        Observe(all);
        try
        { await all.WaitAsync(cap + TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { /* Collect completed peers without waiting for an uncooperative member. */ }
        cancellationToken.ThrowIfCancellationRequested();
        List<WorkspaceHit> hits = [];
        List<WorkspaceCall> calls = [];
        List<string> notes = [], errors = [];
        for (var i = 0; i < members.Length; i++)
        {
            if (!tasks[i].IsCompletedSuccessfully)
            { notes.Add(members[i].Name + " did not answer within " + Duration(cap)); continue; }
            foreach (var (result, evidence) in tasks[i].Result)
            {
                calls.Add(new(members[i].Name, result));
                if (result.Error is { } error)
                {
                    var message = members[i].Name + ": " + CredentialRedactor.Redact(error.Message.Split('\n')[0]);
                    if (result.TimedOut || error is OperationCanceledException || deadline.IsCancellationRequested && error is ProviderException { Detail: "operation cancelled" })
                    {
                        notes.Add(members[i].Name + " did not answer within " + Duration(cap) + " (" + result.Provider + ")");
                    }
                    else if (Warming(error))
                    {
                        notes.Add(message + " (still warming up; ask again shortly)");
                    }
                    else if (error is ProviderNotIndexedException)
                    {
                        notes.Add(message);
                    }
                    else
                    {
                        errors.Add(message);
                    }
                }
                hits.AddRange(result.Items.Select(item => new WorkspaceHit(members[i], item, evidence)));
            }
        }
        return new(hits, calls, notes, errors);

        async Task<IReadOnlyList<(ProviderResult Result, string Evidence)>> AskAsync(WorkspaceMember member)
        {
            try
            {
                if (!Directory.Exists(member.Root))
                {
                    return [(new ProviderResult { Provider = "workspace", Capability = request.Capability, Error = new IOException("repository not found at " + member.Root) }, "")];
                }

                var registry = Registry(member, deep);
                if (deep)
                {
                    await registry.DiscoverAsync(token).ConfigureAwait(false);
                }
                else if (registry is IContextProviderRegistry context)
                {
                    await context.DiscoverContextAsync(token).ConfigureAwait(false);
                }
                else
                {
                    await registry.DiscoverAsync(token).ConfigureAwait(false);
                }

                var infos = !deep && registry is IContextProviderRegistry contextRegistry ? contextRegistry.ContextCapabilities() : registry.Capabilities();
                Func<string, bool>? skip = !deep && registry is IContextProviderRegistry filter ? name => !filter.InContext(name) : null;
                var results = await registry.CallAsync(request, skip, token).ConfigureAwait(false);
                while (deep && results.Any(result => result.Error is { } error && Warming(error)) && !token.IsCancellationRequested)
                {
                    try
                    { await Task.Delay(warmRetry > TimeSpan.Zero ? warmRetry : TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    results = await registry.CallAsync(request, skip, token).ConfigureAwait(false);
                }
                return results.Select(result => (result, infos.FirstOrDefault(info => info.Provider == result.Provider && info.Capability == result.Capability)?.EvidenceOf() ?? Capabilities.DefaultEvidence(request.Capability))).ToArray();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return [(new ProviderResult { Provider = "workspace", Capability = request.Capability, TimedOut = true, Error = new OperationCanceledException("deadline") }, "")]; }
            catch (Exception error) { return [(new ProviderResult { Provider = "workspace", Capability = request.Capability, Error = error }, "")]; }
        }
    }
    public Task<IReadOnlyList<MemoryRecord>> ContextMemoriesAsync(WorkspaceMember member, IReadOnlyList<string> paths, string words, CancellationToken cancellationToken)
    {
        List<MemoryRecord> memories = [];
        try
        {
            using var database = HarnessDatabase.OpenReadOnly(member.DatabasePath);
            var store = new MemoryStore(database);
            string[] types = ["decision", "convention", "warning", "constraint"];
            foreach (var path in paths)
            {
                memories.AddRange(store.Search(new() { Types = types, Scope = new("path", path), Limit = 5 }, cancellationToken).Where(memory => memory.Scope.Kind is not "project"));
            }

            if (words.Length > 0)
            {
                foreach (var memory in store.Search(new() { Text = words, Types = types, Limit = 10 }, cancellationToken))
                {
                    if (words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).Count(word => word.Length >= 4 && memory.Content.Contains(word, StringComparison.OrdinalIgnoreCase)) >= 2 && memories.Count < 8)
                    {
                        memories.Add(memory);
                    }
                }
            }
        }
        catch (Exception error) when (KnowledgeLevels.IsStorageError(error)) { /* Cross-project reads never create or migrate member databases. */ }
        return Task.FromResult<IReadOnlyList<MemoryRecord>>(memories.DistinctBy(memory => memory.Id, StringComparer.Ordinal).ToArray());
    }
    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] owned;
        lock (_gate)
        { _disposed = true; owned = _owned.ToArray(); _owned.Clear(); }
        await Task.WhenAll(owned.Select(item => item.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
    private static bool Warming(Exception error) => error.Message.Contains("still loading", StringComparison.Ordinal) || error.Message.Contains("warm session", StringComparison.Ordinal);
    private static string Duration(TimeSpan cap) => cap.TotalSeconds.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture) + "s";
    private static void Observe(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
