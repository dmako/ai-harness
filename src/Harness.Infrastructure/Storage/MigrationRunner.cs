using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Storage;

internal static class MigrationRunner
{
    private const string ForeignKeysOffMarker = "-- harness: foreign keys off";

    internal static bool HasLedger(SqliteConnection connection) => Convert.ToInt64(Scalar(connection,
        "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations'"), System.Globalization.CultureInfo.InvariantCulture) > 0;

    internal static int Version(SqliteConnection connection) => Convert.ToInt32(
        Scalar(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations"), System.Globalization.CultureInfo.InvariantCulture);

    internal static void CheckApplied(SqliteConnection connection, IReadOnlyList<Migration> migrations)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, name FROM schema_migrations ORDER BY version";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var version = rows.GetInt64(0);
            if (version > migrations.Count)
            {
                throw new SchemaTooNewException(checked((int)version), migrations.Count);
            }

            if (version < 1 || rows.GetString(1) != migrations[(int)version - 1].Name)
            {
                throw new InvalidDataException($"schema_migrations version {version} does not match this binary; refusing to continue.");
            }
        }
    }

    internal static void Run(SqliteConnection connection, string path, IReadOnlyList<Migration> migrations, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasLedger(connection) && Version(connection) == migrations.Count)
        {
            CheckApplied(connection, migrations);
            return; // Current databases need no write lock, even while another process is writing.
        }

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            ) STRICT
            """);
        CheckApplied(connection, migrations);
        var applied = Version(connection);
        var saved = applied > 0 && applied < migrations.Count ? DatabaseBackups.Create(connection, path, applied) : null;
        foreach (var migration in migrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Apply(connection, migration, cancellationToken);
            }
            catch (Exception exception) when (exception is SqliteException or InvalidDataException)
            {
                var recovery = saved is null ? string.Empty : $"; the database before the upgrade is saved as {saved}";
                throw new IOException($"Migration {migration.Name} failed, schema left at previous version{recovery}.", exception);
            }
        }

        CheckApplied(connection, migrations);
    }

    private static void Apply(SqliteConnection connection, Migration migration, CancellationToken cancellationToken)
    {
        var disableForeignKeys = migration.Sql.Contains(ForeignKeysOffMarker, StringComparison.Ordinal);
        if (disableForeignKeys)
        {
            Execute(connection, "PRAGMA foreign_keys = OFF");
        }

        try
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT name FROM schema_migrations WHERE version = $version";
            check.Parameters.AddWithValue("$version", migration.Version);
            if (check.ExecuteScalar() is string name)
            {
                if (name != migration.Name)
                {
                    throw new InvalidDataException($"schema_migrations version {migration.Version} changed concurrently; refusing to continue.");
                }

                transaction.Commit();
                return;
            }

            Execute(connection, migration.Sql, transaction);
            if (disableForeignKeys && Scalar(connection, "SELECT \"table\" FROM pragma_foreign_key_check LIMIT 1", transaction) is string table)
            {
                throw new InvalidDataException($"Migration {migration.Name} failed: foreign key violation in {table}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_migrations (version, name, applied_at) VALUES ($version, $name, $time)";
            record.Parameters.AddWithValue("$version", migration.Version);
            record.Parameters.AddWithValue("$name", migration.Name);
            record.Parameters.AddWithValue("$time", HarnessDatabase.FormatTime(DateTimeOffset.UtcNow));
            record.ExecuteNonQuery();
            transaction.Commit();
        }
        finally
        {
            if (disableForeignKeys)
            {
                Execute(connection, "PRAGMA foreign_keys = ON");
            }
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Internal callers supply constant SQL only; external values use command parameters.")]
    internal static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Internal callers supply embedded migrations, constant SQL, or an invariant-formatted integer pragma; no external SQL is accepted.")]
    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
