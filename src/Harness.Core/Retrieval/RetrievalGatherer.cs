using Harness.Core.Memory;
using Harness.Core.Providers;

namespace Harness.Core.Retrieval;

public static class RetrievalGatherer
{
    public static Task<Gathered> GatherAsync(RetrievalSources sources, RetrievalTask task, CancellationToken cancellationToken = default) => new Gathering(sources, task).RunAsync(cancellationToken);

    private sealed class Gathering(RetrievalSources sources, RetrievalTask task)
    {
        private readonly List<Candidate> _candidates = [];
        private readonly List<string> _errors = [], _notes = [];
        private readonly List<RetrievalCall> _calls = [];
        private readonly IReadOnlyList<string> _symbols = TaskParsing.Symbols(task.Text);

        public async Task<Gathered> RunAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var phrases = TaskParsing.Phrases(task.Text).Where(phrase => !_symbols.Any(symbol => string.Equals(TaskParsing.LastName(symbol), phrase, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (sources.Registry is { } registry)
            {
                await CodeAsync(registry, phrases, token).ConfigureAwait(false);
                if (!TaskParsing.TaskAboutTests(task.Text))
                    foreach (var candidate in _candidates.Where(candidate => candidate.Source is "code" && TaskParsing.IsTestFile(candidate.Path)))
                        candidate.Scores["test"] = 1;
            }
            token.ThrowIfCancellationRequested();
            if (sources.Memory is not null)
                await MemoriesAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (sources.RecentChanges is not null)
                await ChangesAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(_candidates, _symbols, phrases, _errors, _notes, _calls);
        }

        private async Task CodeAsync(IProviderRegistry registry, IReadOnlyList<string> phrases, CancellationToken token)
        {
            var duration = task.CodeTimeout > TimeSpan.Zero ? task.CodeTimeout : TimeSpan.FromSeconds(10);
            using var deadline = new CancellationTokenSource(duration);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var context = registry as IContextProviderRegistry;
            Dictionary<string, CapabilityInfo> information = new(StringComparer.Ordinal);
            HashSet<string> failed = new(StringComparer.Ordinal);
            try
            {
                await BoundedAsync(Task.Run(() => context is null ? registry.DiscoverAsync(linked.Token) : context.DiscoverContextAsync(linked.Token), CancellationToken.None), linked.Token).ConfigureAwait(false);
                var capabilities = await BoundedAsync(Task.Run(() => context is null ? registry.Capabilities() : context.ContextCapabilities(), linked.Token), linked.Token).ConfigureAwait(false);
                foreach (var info in capabilities)
                    information[info.Provider + "\0" + info.Capability] = info;
                await AskAsync(Capabilities.SymbolSearch, task.Text, loose: true).ConfigureAwait(false);
                await AskAsync(Capabilities.Context, task.Text).ConfigureAwait(false);
                foreach (var symbol in _symbols)
                {
                    List<string> defined = [];
                    foreach (var name in new[] { symbol }.Concat(TaskParsing.NamingVariants(symbol)))
                    {
                        var before = _candidates.Count;
                        await AskAsync(Capabilities.SymbolSearch, name, name).ConfigureAwait(false);
                        Qualify(before, name);
                        if (_candidates.Skip(before).Any(candidate => candidate.Scores.GetValueOrDefault("name") is 1))
                            defined.Add(name);
                    }
                    foreach (var name in defined)
                    {
                        await AskAsync(Capabilities.Callers, name).ConfigureAwait(false);
                        await AskAsync(Capabilities.Impact, name).ConfigureAwait(false);
                    }
                }
                foreach (var phrase in phrases)
                {
                    await AskAsync(Capabilities.SymbolSearch, phrase, phrase: phrase).ConfigureAwait(false);
                    var before = _candidates.Count;
                    await AskAsync(Capabilities.FileSearch, phrase, phrase: phrase).ConfigureAwait(false);
                    var head = TaskParsing.HeadNoun(phrase);
                    if (_candidates.Count == before && head.Length >= 4)
                        await AskAsync(Capabilities.FileSearch, head, phrase: head).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { token.ThrowIfCancellationRequested(); }
            catch (Exception error) { Add(_errors, "code: " + CredentialRedactor.Redact(error.Message)); }
            finally
            {
                if (deadline.IsCancellationRequested)
                    Add(_notes, "code search stopped after " + Duration(duration) + "; the answer may miss code (a provider was slow)");
            }

            async Task AskAsync(string capability, string query, string name = "", string phrase = "", bool loose = false)
            {
                if (linked.IsCancellationRequested)
                    return;
                var before = _candidates.Count;
                // The registry bounds each adapter and returns completed peers when the shared deadline expires.
                var results = await registry.CallAsync(new(capability, query, task.Path, task.Limit > 0 ? task.Limit : 10), provider => failed.Contains(provider) || context?.InContext(provider) is false, linked.Token).ConfigureAwait(false);
                foreach (var result in results)
                {
                    _calls.Add(new(result.Provider, result.Capability, query, result.Items.Count, (long)result.Latency.TotalMilliseconds, result.Bytes, result.Error?.Message ?? ""));
                    if (result.Error is { } error)
                    {
                        failed.Add(result.Provider);
                        if (!linked.IsCancellationRequested)
                            Add(error is ProviderNotIndexedException ? _notes : _errors, CredentialRedactor.Redact(error.Message));
                        continue;
                    }
                    var info = information.GetValueOrDefault(result.Provider + "\0" + result.Capability) ?? new CapabilityInfo(result.Capability, "");
                    for (var i = 0; i < result.Items.Count; i++)
                    {
                        var item = result.Items[i];
                        var candidate = Candidate.FromItem(info with { Capability = result.Capability }, i, item);
                        if (name.Length > 0 && TaskParsing.LastName(item.Name ?? "") == TaskParsing.LastName(name))
                            candidate.Scores["name"] = 1;
                        if (loose)
                            candidate.Scores["loose"] = 1;
                        var filename = candidate.Path[(candidate.Path.LastIndexOf('/') + 1)..];
                        if (phrase.Length > 0 && (candidate.Name + " " + filename).Contains(phrase, StringComparison.OrdinalIgnoreCase))
                            candidate.Scores["phrase"] = 1;
                        _candidates.Add(candidate);
                    }
                }
                if (_candidates.Skip(before).Count(candidate => candidate.Scores.GetValueOrDefault("phrase") is 1) > 3)
                    foreach (var candidate in _candidates.Skip(before))
                        candidate.Scores.Remove("phrase");
            }
        }

        private void Qualify(int from, string name)
        {
            var qualifier = TaskParsing.Qualifier(name);
            if (qualifier.Length is 0 || !_candidates.Skip(from).Any(candidate => candidate.Scores.GetValueOrDefault("name") is 1 && TaskParsing.InQualifier(candidate, qualifier)))
                return;
            foreach (var candidate in _candidates.Skip(from).Where(candidate => candidate.Scores.GetValueOrDefault("name") is 1 && !TaskParsing.InQualifier(candidate, qualifier)))
                candidate.Scores["name"] = 0.3;
        }

        private async Task MemoriesAsync(CancellationToken token)
        {
            Dictionary<string, int> seen = new(StringComparer.Ordinal);
            string[] types = ["decision", "convention", "warning", "constraint"];
            foreach (var path in Paths(10))
                foreach (var memory in await SearchAsync(new() { Types = types, Scope = new("path", path), Branch = sources.Branch, Limit = 20 }).ConfigureAwait(false))
                    AddMemory(memory, ScopeScore(memory.Scope, path), "memory (applies to " + path + ")");
            var words = TaskParsing.ContentWords(task.Text);
            if (words.Length > 0)
            {
                var matches = await SearchAsync(new() { Text = words, Types = types, Limit = 10 }).ConfigureAwait(false);
                for (var i = 0; i < matches.Count; i++)
                    AddMemory(matches[i], 0.6 / (1 + i), "memory (matches the task)");
            }
            foreach (var goal in await SearchAsync(new() { Types = ["goal"], Limit = 5 }).ConfigureAwait(false))
                AddMemory(goal, 0.2, "memory (project goal)");

            async Task<IReadOnlyList<MemoryRecord>> SearchAsync(MemoryQuery query)
            {
                try
                {
                    var result = await sources.Memory!(query, token).ConfigureAwait(false);
                    foreach (var error in result.Errors)
                        Add(_errors, "memory: " + CredentialRedactor.Redact(error));
                    return result.Memories;
                }
                catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    Add(_errors, "memory: " + CredentialRedactor.Redact(error.Message));
                    return [];
                }
            }
            void AddMemory(MemoryRecord memory, double score, string why)
            {
                if (seen.TryGetValue(memory.Id, out var index))
                { _candidates[index].Scores["scope"] = Math.Max(_candidates[index].Scores["scope"], score); return; }
                var candidate = Candidate.FromMemory(memory, score) with { Provenance = [new("harness", why, "memory")] };
                seen.Add(memory.Id, _candidates.Count);
                _candidates.Add(candidate);
            }
        }

        private async Task ChangesAsync(CancellationToken token)
        {
            var paths = Paths(3);
            try
            {
                var commits = await sources.RecentChanges!(paths, 5, token).ConfigureAwait(false);
                for (var i = 0; i < commits.Count; i++)
                    foreach (var path in commits[i].Paths)
                        _candidates.Add(Candidate.FromCommit(commits[i], path, 1d / (1 + i)));
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (paths.Length > 0)
                    Add(_errors, "git: " + CredentialRedactor.Redact(error.Message));
            }
        }
        private string[] Paths(int count) => _candidates.Select((candidate, position) => (candidate, position)).Where(pair => pair.candidate.Source is "code" && pair.candidate.Path.Length > 0)
            .GroupBy(pair => pair.candidate.Path, StringComparer.Ordinal).Select(group => group.OrderBy(pair => FirstRank(pair.candidate)).ThenBy(pair => pair.position).First())
            .OrderBy(pair => FirstRank(pair.candidate)).ThenBy(pair => pair.position).Take(count).Select(pair => pair.candidate.Path).ToArray();
        private static int FirstRank(Candidate candidate) => candidate.Provenance.Count > 0 ? candidate.Provenance[0].Rank : 0;
        private static double ScopeScore(MemoryScope scope, string path) => scope.Kind switch { "path" => scope.Value == path ? 1 : 0.8, "subsystem" or "branch" => 0.6, "workspace" => 0.25, "user" => 0.2, _ => 0.3 };
        private static void Add(List<string> values, string value) { if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value); }
        private static string Duration(TimeSpan duration) => duration.TotalSeconds >= 1 ? duration.TotalSeconds.ToString("0.#########", System.Globalization.CultureInfo.InvariantCulture) + "s" : duration.TotalMilliseconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + "ms";
        private static async Task BoundedAsync(Task operation, CancellationToken token)
        {
            Observe(operation);
            await operation.WaitAsync(token).ConfigureAwait(false);
        }
        private static async Task<T> BoundedAsync<T>(Task<T> operation, CancellationToken token)
        {
            Observe(operation);
            return await operation.WaitAsync(token).ConfigureAwait(false);
        }
        private static void Observe(Task operation) => _ = operation.ContinueWith(completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
