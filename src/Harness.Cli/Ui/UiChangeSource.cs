using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Web;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Cli.Ui;

/// <summary>One read-only connection per project; unchanged ticks read data_version only.</summary>
internal sealed class UiChangeSource(ProjectLifecycle lifecycle, UiProjectCatalog catalog, UiAgents? agents = null)
    : IUiChangeSource
{
    private readonly Dictionary<string, DatabaseWatch> _databases = new(StringComparer.Ordinal);
    private Dictionary<string, JsonObject> _entries = new(StringComparer.Ordinal);
    private string _registryStamp = "";
    private string _workspacesStamp = "";
    private bool _started;
    private string DirectoryPath => Path.GetDirectoryName(Path.GetFullPath(lifecycle.GlobalConfigurationPath))!;

    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists
            ? file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                + ":"
                + file.Length.ToString(CultureInfo.InvariantCulture)
            : "missing";
    }

    public Task<JsonObject> PollAsync(CancellationToken token)
    {
        JsonObject result = [];
        JsonObject kinds = [];
        var registry = Stamp(Path.Combine(DirectoryPath, "projects.json"));
        if (!_started || registry != _registryStamp)
        {
            try
            {
                if (Reload(registry, kinds))
                {
                    result["registry"] = true;
                }
            }
            catch (Exception exception) when (ReadFailure(exception))
            { /* Preserve existing watches until a readable registry is available. */
            }
        }
        var workspaces = Stamp(Path.Combine(DirectoryPath, "workspaces.json"));
        if (_workspacesStamp != workspaces)
        {
            result["workspaces"] = true;
            _workspacesStamp = workspaces;
        }
        foreach (var (id, database) in _databases)
        {
            token.ThrowIfCancellationRequested();
            var changed = database.Check();
            if (changed.Length > 0)
            {
                Add(kinds, id, changed);
            }
        }
        if (kinds.Count > 0)
        {
            result["kinds"] = kinds;
            result["projects"] = new JsonArray(
                kinds
                    .Select(p => p.Key)
                    .Order(StringComparer.Ordinal)
                    .Select(s => (JsonNode?)JsonValue.Create(s))
                    .ToArray()
            );
        }
        if (!_started)
        {
            result.Clear();
            _started = true;
        }
        if (result.Count > 0)
        {
            catalog.Forget();
        }
        // what the agent hosts did since the last tick (TASK-0116): sent as "agent" events, not as a change
        if (agents?.Drain() is { Count: > 0 } drained)
        {
            result["agents"] = drained;
        }
        return Task.FromResult(result);
    }

    /// <summary>
    /// Reads the registry, notes a project's summary change and watches each project's database; true when
    /// projects came, went, moved or were renamed.
    /// </summary>
    private bool Reload(string stamp, JsonObject kinds)
    {
        var entries = lifecycle.Registry.Load()["projects"]!
            .AsArray()
            .OfType<JsonObject>()
            .ToDictionary(p => p["id"]!.GetValue<string>(), p => (JsonObject)p.DeepClone(), StringComparer.Ordinal);
        var structural = entries.Count != _entries.Count;
        foreach (var (id, project) in entries)
        {
            if (Moved(id, project))
            {
                structural = true;
            }
            else if (!JsonNode.DeepEquals(_entries[id], project))
            {
                Add(kinds, id, ["summary"]);
            }
            Watch(id, project["db_path"]?.GetValue<string>() ?? "");
        }
        foreach (var id in _databases.Keys.Where(id => !entries.ContainsKey(id)).ToArray())
        {
            _databases[id].Dispose();
            _databases.Remove(id);
        }
        _entries = entries;
        _registryStamp = stamp;
        return structural;
    }

    /// <summary>A new project, or one whose root, database, name or title changed.</summary>
    private bool Moved(string id, JsonObject project) =>
        !_entries.TryGetValue(id, out var old)
        || new[] { "root", "db_path", "name", "title" }.Any(key => !JsonNode.DeepEquals(old[key], project[key]));

    private void Watch(string id, string path)
    {
        if (!_databases.TryGetValue(id, out var database) || database.Path != path)
        {
            database?.Dispose();
            _databases[id] = new(path);
        }
    }

    private static void Add(JsonObject kinds, string id, IEnumerable<string> values)
    {
        var previous = kinds[id]?.AsArray().Select(n => n!.GetValue<string>()) ?? [];
        kinds[id] = new JsonArray(
            previous
                .Concat(values)
                .Distinct(StringComparer.Ordinal)
                .Select(s => (JsonNode?)JsonValue.Create(s))
                .ToArray()
        );
    }

    private static bool ReadFailure(Exception exception) =>
        exception
            is IOException
                or UnauthorizedAccessException
                or SqliteException
                or InvalidOperationException
                or JsonException;

    public ValueTask DisposeAsync()
    {
        foreach (var database in _databases.Values)
        {
            database.Dispose();
        }
        _databases.Clear();
        return ValueTask.CompletedTask;
    }

    private sealed class DatabaseWatch(string path) : IDisposable
    {
        internal string Path { get; } = path;
        private SqliteConnection? _connection;
        private long _version;
        private bool _failed;
        private DateTimeOffset _retryAt;
        private Dictionary<string, string> _queries = new(StringComparer.Ordinal);
        private Dictionary<string, string> _prints = new(StringComparer.Ordinal);

        internal string[] Check()
        {
            try
            {
                if (_connection is null)
                {
                    if (Path.Length is 0 || DateTimeOffset.UtcNow < _retryAt)
                    {
                        return [];
                    }
                    _connection = new(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = Path,
                            Mode = SqliteOpenMode.ReadOnly,
                            Pooling = false,
                            DefaultTimeout = 1,
                        }.ToString()
                    );
                    _connection.Open();
                    _queries = DatabaseFingerprint.Probes(_connection);
                    _version = Convert.ToInt64(
                        DatabaseFingerprint.Scalar(_connection, "PRAGMA data_version"),
                        CultureInfo.InvariantCulture
                    );
                    _prints = DatabaseFingerprint.Prints(_connection, _queries);
                    var recovered = _failed;
                    _failed = false;
                    return recovered ? ["all"] : [];
                }
                var version = Convert.ToInt64(
                    DatabaseFingerprint.Scalar(_connection, "PRAGMA data_version"),
                    CultureInfo.InvariantCulture
                );
                if (version == _version)
                {
                    return [];
                }
                _version = version;
                var prints = DatabaseFingerprint.Prints(_connection, _queries);
                var changed = DatabaseFingerprint
                    .Kinds.Where(k => prints.GetValueOrDefault(k.Kind) != _prints.GetValueOrDefault(k.Kind))
                    .Select(k => k.Kind)
                    .ToArray();
                _prints = prints;
                return changed;
            }
            catch (Exception exception) when (ReadFailure(exception))
            {
                Dispose();
                _failed = true;
                _retryAt = DateTimeOffset.UtcNow.AddSeconds(10);
                return [];
            }
        }

        public void Dispose()
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
