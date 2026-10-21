using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Storage;

/// <summary>A connection factory for a compatible harness SQLite database. Own each returned connection for one operation.</summary>
public sealed class HarnessDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly StorageOptions _options;
    private bool _disposed;

    private HarnessDatabase(string path, StorageOptions options, bool readOnly)
    {
        Path = path;
        _options = options;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            ForeignKeys = !readOnly,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    public static int LatestSchemaVersion => MigrationCatalog.All.Count;

    public static HarnessDatabase Open(string path, StorageOptions? options = null, CancellationToken cancellationToken = default) =>
        Open(path, options ?? new StorageOptions(), MigrationCatalog.All, cancellationToken);

    internal static HarnessDatabase Open(string path, StorageOptions options, IReadOnlyList<Migration> migrations, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains('?', StringComparison.Ordinal))
        {
            throw new ArgumentException("Database paths must not contain '?'.", nameof(path));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var absolute = System.IO.Path.GetFullPath(path);
        StorageFiles.CreateDirectory(System.IO.Path.GetDirectoryName(absolute)!);
        StorageFiles.CreateDatabase(absolute);
        HarnessDatabase? database = new(absolute, options, readOnly: false);
        try
        {
            using var connection = database.OpenConnection();
            if (MigrationRunner.HasLedger(connection))
            {
                MigrationRunner.CheckApplied(connection, migrations);
            }

            database.EnableWal(connection, cancellationToken);
            MigrationRunner.Run(connection, absolute, migrations, cancellationToken);
            var opened = database;
            database = null;
            return opened;
        }
        finally
        {
            database?.Dispose();
        }
    }

    internal static HarnessDatabase OpenExisting(string path, StorageOptions? options = null)
    {
        var absolute = System.IO.Path.GetFullPath(path);
        if (!File.Exists(absolute))
            throw new FileNotFoundException("Database does not exist.", absolute);
        return new(absolute, options ?? new StorageOptions(), readOnly: false);
    }

    public static HarnessDatabase OpenReadOnly(string path, StorageOptions? options = null)
    {
        var absolute = System.IO.Path.GetFullPath(path);
        if (!File.Exists(absolute))
        {
            throw new FileNotFoundException("Database does not exist.", absolute);
        }

        HarnessDatabase? database = new(absolute, options ?? new StorageOptions(), readOnly: true);
        try
        {
            using var connection = database.OpenConnection();
            MigrationRunner.Scalar(connection, "SELECT 1");
            var opened = database;
            database = null;
            return opened;
        }
        finally
        {
            database?.Dispose();
        }
    }

    public SqliteConnection OpenConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // In the pinned 10.0.13 provider, -1 stops its additional whole-second BUSY retry loop.
        // SQLite's native busy_timeout then owns the budget, including sub-second timeouts.
        // The contention tests protect this provider-specific behavior on package upgrades.
        var connection = new SqliteConnection(_connectionString) { DefaultTimeout = -1 };
        try
        {
            connection.Open();
            var milliseconds = checked((int)_options.EffectiveBusyTimeout.TotalMilliseconds);
            MigrationRunner.Execute(connection, $"PRAGMA busy_timeout = {milliseconds.ToString(CultureInfo.InvariantCulture)}");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public int SchemaVersion()
    {
        using var connection = OpenConnection();
        return MigrationRunner.Version(connection);
    }

    public MigrationState InspectMigrationState()
    {
        using var connection = OpenConnection();
        if (!MigrationRunner.HasLedger(connection))
        {
            return new MigrationState(0, LatestSchemaVersion, null);
        }

        var applied = MigrationRunner.Version(connection);
        try
        {
            MigrationRunner.CheckApplied(connection, MigrationCatalog.All);
            return new MigrationState(applied, LatestSchemaVersion, null);
        }
        catch (Exception exception) when (exception is SchemaTooNewException or InvalidDataException)
        {
            return new MigrationState(applied, LatestSchemaVersion, exception);
        }
    }

    public T WithTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var result = action(connection, transaction);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return result;
    }

    public T ReadTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        MigrationRunner.Execute(connection, "PRAGMA query_only = ON");
        try
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var result = action(connection, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            MigrationRunner.Execute(connection, "PRAGMA query_only = OFF");
        }
    }

    public void PutDiagnostic(string key, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO diagnostics (key, value, updated_at) VALUES ($key, $value, $time)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$time", FormatTime(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    public string GetDiagnostic(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM diagnostics WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string value ? value : throw new KeyNotFoundException("not found");
    }

    public static string FormatTime(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'00Z'", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private void EnableWal(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var delay = 5;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (MigrationRunner.Scalar(connection, "PRAGMA journal_mode = WAL") is not string mode || mode != "wal")
                {
                    throw new IOException("Could not enable WAL journal mode.");
                }

                return;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 5 && timer.Elapsed < _options.EffectiveBusyTimeout)
            {
                if (cancellationToken.WaitHandle.WaitOne(delay))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                delay = Math.Min(delay * 2, 200);
            }
        }
    }
}
