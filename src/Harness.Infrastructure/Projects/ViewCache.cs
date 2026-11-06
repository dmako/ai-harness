using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Harness.Infrastructure.Architecture.Metrics;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Projects;

/// <summary>A project or workspace member a cached view reads: its working tree and its database.</summary>
public sealed record ViewMember(string Id, string Root, string Database);

/// <summary>
/// harness ui's architecture and workspace views as last computed, kept in its process while what they read is
/// unchanged: each member's HEAD commit (read from its refs, no Git process), its working tree (a watcher counts
/// changes outside .git, harness's state and build output), its database (everything but activity), its exact
/// metrics, and the machine's registry, workspaces and global config. Bounded; nothing is written anywhere.
/// </summary>
public sealed class ViewCache(ProjectLifecycle lifecycle) : IDisposable
{
    public const int Capacity = 128;

    /// <summary>Shared analyses kept at once: each holds its members' file lists and the text it read.</summary>
    public const int Analyses = 4;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TreeWatch> _trees = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GitRepository?> _repositories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry<object>> _analyses = new(StringComparer.Ordinal);
    private readonly Lock _analysisLock = new();
    private long _clock;

    private sealed record Entry(string Key, JsonNode Value)
    {
        public long Used { get; set; }
    }

    private sealed record Entry<T>(string Key, T Value)
    {
        public long Used { get; set; }
    }

    /// <summary>The view named, from the cache while its members are unchanged, else computed and kept.</summary>
    public async Task<JsonNode?> GetAsync(
        string name,
        IReadOnlyList<ViewMember> members,
        Func<Task<JsonNode?>> compute,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(compute);
        // the key is taken before computing: a change while it runs makes the next request recompute
        return await GetAsync(name, await KeyAsync(members, token).ConfigureAwait(false), compute)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A view read only from these members' working trees (TASK-0092: a project's stack badge), kept while their
    /// HEADs and working trees are unchanged; written databases and registry changes do not recompute it.
    /// </summary>
    public async Task<JsonNode?> TreeViewAsync(
        string name,
        IReadOnlyList<ViewMember> members,
        Func<Task<JsonNode?>> compute,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(compute);
        return await GetAsync("tree:" + name, await TreesKeyAsync(members, token).ConfigureAwait(false), compute)
            .ConfigureAwait(false);
    }

    private async Task<JsonNode?> GetAsync(string name, string key, Func<Task<JsonNode?>> compute)
    {
        if (_entries.TryGetValue(name, out var cached) && cached.Key == key)
        {
            cached.Used = Interlocked.Increment(ref _clock);
            return cached.Value.DeepClone();
        }

        var value = await compute().ConfigureAwait(false);
        if (value is null)
        {
            return null;
        }

        _entries[name] = new(key, value.DeepClone()) { Used = Interlocked.Increment(ref _clock) };
        while (_entries.Count > Capacity)
        {
            var oldest = _entries.MinBy(pair => pair.Value.Used).Key;
            _entries.TryRemove(oldest, out _);
        }

        return value;
    }

    public int Count => _entries.Count;

    public int AnalysisCount
    {
        get
        {
            lock (_analysisLock)
            {
                return _analyses.Count;
            }
        }
    }

    /// <summary>
    /// The analysis of these members' working trees (TASK-0093), shared by every view that reads them while their
    /// HEADs and working trees are unchanged; made by create when there is none or they changed, and the one it
    /// replaces is dropped. Unlike a view it does not follow the databases: it reads only the trees, so a task or
    /// memory written does not throw it away (the views over it still follow them). At most
    /// <see cref="Analyses"/> are kept, the least recently used dropped first.
    /// </summary>
    public async Task<T> AnalysisAsync<T>(IReadOnlyList<ViewMember> members, Func<T> create, CancellationToken token)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(create);
        var name = string.Join('\n', members.Select(member => member.Id + "@" + member.Root).Order(StringComparer.Ordinal));
        var key = await TreesKeyAsync(members, token).ConfigureAwait(false);
        lock (_analysisLock)
        {
            if (_analyses.TryGetValue(name, out var cached) && cached.Key == key && cached.Value is T value)
            {
                cached.Used = Interlocked.Increment(ref _clock);
                return value;
            }

            var created = create();
            _analyses[name] = new(key, created) { Used = Interlocked.Increment(ref _clock) };
            while (_analyses.Count > Analyses)
            {
                _analyses.Remove(_analyses.MinBy(pair => pair.Value.Used).Key);
            }

            return created;
        }
    }

    private async Task<string> TreesKeyAsync(IReadOnlyList<ViewMember> members, CancellationToken token)
    {
        List<string> parts = [];
        foreach (var root in members.Select(member => member.Root).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            parts.Add(root);
            parts.Add(await HeadAsync(root, token).ConfigureAwait(false));
            parts.Add(_trees.GetOrAdd(root, path => new TreeWatch(path)).Generation);
        }

        return string.Join('\n', parts);
    }

    private async Task<string> KeyAsync(IReadOnlyList<ViewMember> members, CancellationToken token)
    {
        var global = Path.GetDirectoryName(Path.GetFullPath(lifecycle.GlobalConfigurationPath))!;
        List<string> parts =
        [
            Stamp(lifecycle.GlobalConfigurationPath),
            Stamp(Path.Combine(global, "projects.json")),
            Stamp(Path.Combine(global, "workspaces.json")),
        ];
        foreach (var member in members.OrderBy(member => member.Root, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            parts.Add(member.Root);
            parts.Add(await HeadAsync(member.Root, token).ConfigureAwait(false));
            parts.Add(_trees.GetOrAdd(member.Root, root => new TreeWatch(root)).Generation);
            parts.Add(DatabaseFingerprint.Of(member.Database));
            parts.Add(
                member.Id.Length is 0 ? "" : Latest(MetricsStore.For(lifecycle.GlobalConfigurationPath, member.Id))
            );
        }

        return string.Join('\n', parts);
    }

    private async Task<string> HeadAsync(string root, CancellationToken token)
    {
        if (!_repositories.TryGetValue(root, out var repository))
        {
            repository = await DiscoverAsync(root, token).ConfigureAwait(false);
            _repositories[root] = repository;
        }

        if (repository is null)
        {
            return "no git";
        }

        try
        {
            var head = await repository.ReadHeadAsync(token).ConfigureAwait(false);
            return head.Branch + "@" + head.Commit;
        }
        catch (IOException)
        {
            return "unreadable:" + Guid.NewGuid().ToString("N");
        }
    }

    private static async Task<GitRepository?> DiscoverAsync(string root, CancellationToken token)
    {
        try
        {
            return await GitRepository.DiscoverAsync(root, token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The newest write in a project's exact-metrics directory, as its runs complete.</summary>
    private static string Latest(MetricsStore store)
    {
        var directory = Path.GetDirectoryName(store.Report) ?? "";
        try
        {
            return Directory.Exists(directory)
                ? Directory
                    .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Select(file => File.GetLastWriteTimeUtc(file).Ticks)
                    .DefaultIfEmpty()
                    .Max()
                    .ToString(CultureInfo.InvariantCulture)
                : "none";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "unreadable:" + Guid.NewGuid().ToString("N");
        }
    }

    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists
            ? file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                + ":"
                + file.Length.ToString(CultureInfo.InvariantCulture)
            : "missing";
    }

    public void Dispose()
    {
        foreach (var tree in _trees.Values)
        {
            tree.Dispose();
        }

        _trees.Clear();
        _entries.Clear();
        lock (_analysisLock)
        {
            _analyses.Clear();
        }
    }

    /// <summary>
    /// Counts the changes in a working tree that a view could read: not .git (HEAD is read on its own), not
    /// harness's state beside config.toml and knowledge.toml, not build output. A tree that cannot be watched
    /// is never cached.
    /// </summary>
    private sealed class TreeWatch : IDisposable
    {
        private static readonly HashSet<string> Ignored = new(
            [".git", "bin", "obj", "node_modules", ".vs", ".idea"],
            StringComparer.OrdinalIgnoreCase
        );

        private readonly string _root;
        private readonly FileSystemWatcher? _watcher;
        private long _generation;

        public TreeWatch(string root)
        {
            _root = Path.GetFullPath(root);
            _watcher = Watch();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Maintainability",
            "CA1508:Avoid dead conditional code",
            Justification = "The watcher is disposed in finally when its setup throws after it was created; the analyzer does not follow the exception path."
        )]
        private FileSystemWatcher? Watch()
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(_root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter =
                        NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Changed += (_, change) => Note(change.FullPath);
                watcher.Created += (_, change) => Note(change.FullPath);
                watcher.Deleted += (_, change) => Note(change.FullPath);
                watcher.Renamed += (_, change) =>
                {
                    Note(change.OldFullPath);
                    Note(change.FullPath);
                };
                // an overflowing buffer lost changes: count it as one
                watcher.Error += (_, _) => Interlocked.Increment(ref _generation);
                watcher.EnableRaisingEvents = true;
                var watching = watcher;
                watcher = null;
                return watching;
            }
            catch (Exception exception)
                when (exception is IOException or ArgumentException or PlatformNotSupportedException)
            {
                return null;
            }
            finally
            {
                watcher?.Dispose();
            }
        }

        public string Generation =>
            _watcher is null
                ? "unwatched:" + Guid.NewGuid().ToString("N")
                : Interlocked.Read(ref _generation).ToString(CultureInfo.InvariantCulture);

        private void Note(string path)
        {
            if (Relevant(_root, path))
            {
                Interlocked.Increment(ref _generation);
            }
        }

        internal static bool Relevant(string root, string path)
        {
            var parts = Path.GetRelativePath(root, path)
                .Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries
                );
            if (parts.Length is 0 || parts.Any(Ignored.Contains))
            {
                return false;
            }

            return !parts[0].Equals(".harness", StringComparison.OrdinalIgnoreCase)
                || parts.Length is 2
                    && (
                        parts[1].Equals("config.toml", StringComparison.OrdinalIgnoreCase)
                        || parts[1].Equals("knowledge.toml", StringComparison.OrdinalIgnoreCase)
                    );
        }

        public void Dispose() => _watcher?.Dispose();
    }
}
