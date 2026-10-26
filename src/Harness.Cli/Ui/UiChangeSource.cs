using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Web;
using Harness.Infrastructure.Projects;
using Microsoft.Data.Sqlite;

namespace Harness.Cli.Ui;

/// <summary>One read-only connection per project; unchanged ticks read data_version only.</summary>
internal sealed class UiChangeSource(ProjectLifecycle lifecycle, UiProjectCatalog catalog) : IUiChangeSource
{
    private static readonly (string Kind, string[] Tables, bool AppendOnly)[] Kinds =
    [
        ("tasks", ["tasks", "task_events", "task_dependencies", "task_acceptance", "milestones"], false),
        ("knowledge", ["memories", "memory_events"], false),
        ("proposals", ["memory_proposals", "finding_proposals", "task_proposals"], false),
        ("findings", ["findings", "finding_snapshots", "finding_marks"], false),
        ("activity", ["tool_calls", "provider_calls", "context_calls", "context_outcomes", "tool_events"], true),
        ("sync", ["sync_state", "sync_runs", "files", "worktrees"], false),
    ];
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
        private readonly Dictionary<string, string> _queries = new(StringComparer.Ordinal);
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
                    Probes();
                    _version = Convert.ToInt64(Scalar("PRAGMA data_version"), CultureInfo.InvariantCulture);
                    _prints = Fingerprints();
                    var recovered = _failed;
                    _failed = false;
                    return recovered ? ["all"] : [];
                }
                var version = Convert.ToInt64(Scalar("PRAGMA data_version"), CultureInfo.InvariantCulture);
                if (version == _version)
                {
                    return [];
                }
                _version = version;
                var prints = Fingerprints();
                var changed = Kinds
                    .Where(k => prints.GetValueOrDefault(k.Kind) != _prints.GetValueOrDefault(k.Kind))
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

        [SuppressMessage(
            "Security",
            "CA2100:Review SQL queries for security vulnerabilities",
            Justification = "Queries use a fixed table allowlist and quoted schema column identifiers; no request values enter SQL."
        )]
        private object? Scalar(string sql)
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        private Dictionary<string, string> Fingerprints() =>
            _queries.ToDictionary(
                p => p.Key,
                p => Convert.ToString(Scalar(p.Value), CultureInfo.InvariantCulture) ?? "",
                StringComparer.Ordinal
            );

        private void Probes()
        {
            _queries.Clear();
            foreach (var kind in Kinds)
            {
                List<string> parts = [];
                foreach (var table in kind.Tables)
                {
                    using var command = _connection!.CreateCommand();
                    command.CommandText = "SELECT name FROM pragma_table_info($table)";
                    command.Parameters.AddWithValue("$table", table);
                    using var reader = command.ExecuteReader();
                    List<string> columns = [];
                    while (reader.Read())
                    {
                        columns.Add(reader.GetString(0));
                    }
                    if (columns.Count is 0)
                    {
                        continue;
                    }
                    var expression = new StringBuilder(
                        kind.AppendOnly ? "COALESCE(MAX(rowid), 0)" : "COUNT(*) || ':' || COALESCE(MAX(rowid), 0)"
                    );
                    if (!kind.AppendOnly)
                    {
                        foreach (var column in columns.Where(c => c.EndsWith("_at", StringComparison.Ordinal)))
                        {
                            expression
                                .Append(" || ':' || COALESCE(MAX(\"")
                                .Append(column.Replace("\"", "\"\"", StringComparison.Ordinal))
                                .Append("\"), '')");
                        }
                    }
                    parts.Add("(SELECT " + expression + " FROM " + table + ")");
                }
                if (parts.Count > 0)
                {
                    _queries[kind.Kind] = "SELECT " + string.Join(" || '|' || ", parts);
                }
            }
        }

        public void Dispose()
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
