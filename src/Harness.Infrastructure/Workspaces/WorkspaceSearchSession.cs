using System.Collections.Concurrent;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Workspaces;

public sealed partial class WorkspaceSearchSession(
    WorkspaceMembership membership,
    IProviderRegistry? current,
    Func<WorkspaceMember, bool, IProviderRegistry> build,
    TimeSpan lightCap = default,
    TimeSpan deepCap = default,
    TimeSpan warmRetry = default
) : IWorkspaceSearchSession, IWorkspaceEvidenceSession
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

        return _registries
            .GetOrAdd(
                (member.Root, deep),
                _ =>
                    new(
                        () =>
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
                        },
                        LazyThreadSafetyMode.ExecutionAndPublication
                    )
            )
            .Value;
    }

    public async Task<WorkspaceAnswer> QueryAsync(
        ProviderRequest request,
        bool deep,
        string only,
        CancellationToken cancellationToken
    )
    {
        var cap = Cap(deep);
        using var deadline = new CancellationTokenSource(cap);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        var members = Members
            .Where(member => only.Length is 0 || member.Name == only)
            .OrderByDescending(member => member.Id == Current.Id)
            .ThenBy(member => member.Name, WorkspaceNameComparer.Instance)
            .ToArray();
        var tasks = members
            .Select(member => Task.Run(() => AskMemberAsync(member, request, deep, token), CancellationToken.None))
            .ToArray();
        var all = Task.WhenAll(tasks);
        Observe(all);
        try
        {
            await all.WaitAsync(cap + TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        { /* Collect completed peers without waiting for an uncooperative member. */
        }
        cancellationToken.ThrowIfCancellationRequested();
        var answers = new MemberAnswers(cap, deadline);
        for (var i = 0; i < members.Length; i++)
        {
            answers.Add(members[i], tasks[i]);
        }
        return answers.Answer();
    }

    /// <summary>The configured time limit for a light or deep query; 10 or 60 seconds when there is none.</summary>
    private TimeSpan Cap(bool deep)
    {
        var cap = deep ? deepCap : lightCap;
        if (cap > TimeSpan.Zero)
        {
            return cap;
        }

        return TimeSpan.FromSeconds(deep ? 60 : 10);
    }

    /// <summary>The members' answers sorted into hits, calls, notes (slow, warming, not indexed) and errors.</summary>
    private sealed class MemberAnswers(TimeSpan cap, CancellationTokenSource deadline)
    {
        private readonly List<WorkspaceHit> _hits = [];
        private readonly List<WorkspaceCall> _calls = [];
        private readonly List<string> _notes = [];
        private readonly List<string> _errors = [];

        public void Add(WorkspaceMember member, Task<IReadOnlyList<(ProviderResult Result, string Evidence)>> task)
        {
            if (!task.IsCompletedSuccessfully)
            {
                _notes.Add(member.Name + " did not answer within " + Duration(cap));
                return;
            }
            foreach (var (result, evidence) in task.Result)
            {
                _calls.Add(new(member.Name, result));
                if (result.Error is { } error)
                {
                    AddError(member, result, error);
                }
                _hits.AddRange(result.Items.Select(item => new WorkspaceHit(member, item, evidence)));
            }
        }

        public WorkspaceAnswer Answer() => new(_hits, _calls, _notes, _errors);

        /// <summary>A timeout, warming provider or missing index is a note; any other failure an error.</summary>
        private void AddError(WorkspaceMember member, ProviderResult result, Exception error)
        {
            var message = member.Name + ": " + CredentialRedactor.Redact(error.Message.Split('\n')[0]);
            if (
                result.TimedOut
                || error is OperationCanceledException
                || deadline.IsCancellationRequested && error is ProviderException { Detail: "operation cancelled" }
            )
            {
                _notes.Add(member.Name + " did not answer within " + Duration(cap) + " (" + result.Provider + ")");
            }
            else if (Warming(error))
            {
                _notes.Add(message + " (still warming up; ask again shortly)");
            }
            else if (error is ProviderNotIndexedException)
            {
                _notes.Add(message);
            }
            else
            {
                _errors.Add(message);
            }
        }

        private static string Duration(TimeSpan cap) =>
            cap.TotalSeconds.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture) + "s";
    }

    /// <summary>
    /// One member's results with each one's evidence; a missing repository, the deadline or a failure
    /// becomes a failed result.
    /// </summary>
    private async Task<IReadOnlyList<(ProviderResult Result, string Evidence)>> AskMemberAsync(
        WorkspaceMember member,
        ProviderRequest request,
        bool deep,
        CancellationToken token
    )
    {
        try
        {
            if (!Directory.Exists(member.Root))
            {
                return [Failed(request, new IOException("repository not found at " + member.Root))];
            }

            var registry = Registry(member, deep);
            await DiscoverAsync(registry, deep, token).ConfigureAwait(false);
            var context = deep ? null : registry as IContextProviderRegistry;
            var infos = context is null ? registry.Capabilities() : context.ContextCapabilities();
            Func<string, bool>? skip = context is null ? null : name => !context.InContext(name);
            var results = await registry.CallAsync(request, skip, token).ConfigureAwait(false);
            if (deep)
            {
                results = await RetryWarmingAsync(registry, request, skip, results, token).ConfigureAwait(false);
            }

            return results
                .Select(result =>
                    (
                        result,
                        infos
                            .FirstOrDefault(info =>
                                info.Provider == result.Provider && info.Capability == result.Capability
                            )
                            ?.EvidenceOf()
                            ?? Capabilities.DefaultEvidence(request.Capability)
                    )
                )
                .ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return [Failed(request, new OperationCanceledException("deadline"), timedOut: true)];
        }
        catch (Exception error)
        {
            return [Failed(request, error)];
        }
    }

    /// <summary>Deep queries discover every provider; light ones only those that serve context when they can.</summary>
    private static Task DiscoverAsync(IProviderRegistry registry, bool deep, CancellationToken token) =>
        !deep && registry is IContextProviderRegistry context
            ? context.DiscoverContextAsync(token)
            : registry.DiscoverAsync(token);

    /// <summary>Asks again, after a pause, while a provider is still warming up and the deadline allows.</summary>
    private async Task<IReadOnlyList<ProviderResult>> RetryWarmingAsync(
        IProviderRegistry registry,
        ProviderRequest request,
        Func<string, bool>? skip,
        IReadOnlyList<ProviderResult> results,
        CancellationToken token
    )
    {
        while (results.Any(result => result.Error is { } error && Warming(error)) && !token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(warmRetry > TimeSpan.Zero ? warmRetry : TimeSpan.FromSeconds(2), token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            results = await registry.CallAsync(request, skip, token).ConfigureAwait(false);
        }
        return results;
    }

    private static (ProviderResult Result, string Evidence) Failed(
        ProviderRequest request,
        Exception error,
        bool timedOut = false
    ) =>
        (
            new ProviderResult
            {
                Provider = "workspace",
                Capability = request.Capability,
                TimedOut = timedOut,
                Error = error,
            },
            ""
        );

    public Task<IReadOnlyList<MemoryRecord>> ContextMemoriesAsync(
        WorkspaceMember member,
        IReadOnlyList<string> paths,
        string words,
        CancellationToken cancellationToken
    )
    {
        List<MemoryRecord> memories = [];
        try
        {
            using var database = HarnessDatabase.OpenReadOnly(member.DatabasePath);
            var store = new MemoryStore(database);
            string[] types = ["decision", "convention", "warning", "constraint"];
            foreach (var path in paths)
            {
                memories.AddRange(
                    store
                        .Search(
                            new()
                            {
                                Types = types,
                                Scope = new("path", path),
                                Limit = 5,
                            },
                            cancellationToken
                        )
                        .Where(memory => memory.Scope.Kind is not "project")
                );
            }

            if (words.Length > 0)
            {
                foreach (
                    var memory in store.Search(
                        new()
                        {
                            Text = words,
                            Types = types,
                            Limit = 10,
                        },
                        cancellationToken
                    )
                )
                {
                    if (
                        words
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(word =>
                                word.Length >= 4 && memory.Content.Contains(word, StringComparison.OrdinalIgnoreCase)
                            ) >= 2
                        && memories.Count < 8
                    )
                    {
                        memories.Add(memory);
                    }
                }
            }
        }
        catch (Exception error) when (KnowledgeLevels.IsStorageError(error))
        { /* Cross-project reads never create or migrate member databases. */
        }
        return Task.FromResult<IReadOnlyList<MemoryRecord>>(
            memories.DistinctBy(memory => memory.Id, StringComparer.Ordinal).ToArray()
        );
    }

    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] owned;
        lock (_gate)
        {
            _disposed = true;
            owned = _owned.ToArray();
            _owned.Clear();
        }
        await Task.WhenAll(owned.Select(item => item.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    private static bool Warming(Exception error) =>
        error.Message.Contains("still loading", StringComparison.Ordinal)
        || error.Message.Contains("warm session", StringComparison.Ordinal);

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
}
