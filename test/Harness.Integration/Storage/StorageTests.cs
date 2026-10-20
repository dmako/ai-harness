using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Harness.Integration.Storage;

public sealed class StorageTests
{
    [Fact]
    public void EmbeddedMigrationsMatchEveryFrozenByte()
    {
        using var inventory = JsonDocument.Parse(File.ReadAllText(Fixture("storage.json")));
        var frozen = inventory.RootElement.GetProperty("migrations").EnumerateArray().ToArray();
        Assert.Equal(19, MigrationCatalog.All.Count);
        Assert.Equal(frozen.Length, MigrationCatalog.All.Count);
        for (var index = 0; index < frozen.Length; index++)
        {
            var migration = MigrationCatalog.All[index];
            Assert.Equal(index + 1, migration.Version);
            Assert.Equal(Path.GetFileNameWithoutExtension(frozen[index].GetProperty("path").GetString()), migration.Name);
            Assert.Equal(frozen[index].GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(migration.Sql))));
        }
    }

    [Fact]
    public void MigrationFilesMustHaveValidNamesAndContiguousUniqueVersions()
    {
        Assert.Throws<InvalidDataException>(() => MigrationCatalog.Load([("readme.txt", "")]));
        Assert.Throws<InvalidDataException>(() => MigrationCatalog.Load([("0002_gap.sql", "")]));
        Assert.Throws<InvalidDataException>(() => MigrationCatalog.Load([("0000_zero.sql", "")]));
        Assert.Throws<InvalidDataException>(() => MigrationCatalog.Load([("0001_one.sql", ""), ("0001_other.sql", "")]));
        Assert.Equal([1, 2], MigrationCatalog.Load([("0002_two.sql", ""), ("0001_one.sql", "")]).Select(migration => migration.Version));
    }

    [Fact]
    public void FreshDatabaseHasPragmasStrictSchemaAndPersistentDiagnostics()
    {
        using var directory = new TestDirectory();
        using (var database = Open(directory.DatabasePath))
        {
            Assert.Equal(19, database.SchemaVersion());
            Assert.Empty(DatabaseBackups.List(directory.DatabasePath));
            using var connection = database.OpenConnection();
            Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode"));
            Assert.Equal(1L, Scalar(connection, "PRAGMA foreign_keys"));
            Assert.Equal(5000L, Scalar(connection, "PRAGMA busy_timeout"));
            Assert.Throws<SqliteException>(() => Execute(connection, "INSERT INTO schema_migrations VALUES ('text', 'bad', 'bad')"));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
            database.PutDiagnostic("quote';--", "žluťoučký 🧪");
            Assert.Throws<KeyNotFoundException>(() => database.GetDiagnostic("absent"));
        }

        using var reopened = Open(directory.DatabasePath);
        Assert.Equal("žluťoučký 🧪", reopened.GetDiagnostic("quote';--"));
        Assert.False(reopened.InspectMigrationState().Pending);
        Assert.Empty(DatabaseBackups.List(directory.DatabasePath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(13)]
    [InlineData(19)]
    public void HistoricalDatabasesUpgradeWithoutChangingDiagnosticsAndBackupsRestore(int version)
    {
        using var directory = new TestDirectory();
        File.Copy(Fixture($"schema-{version:00}.db"), directory.DatabasePath);
        using (var database = Open(directory.DatabasePath))
        {
            Assert.Equal(19, database.SchemaVersion());
            Assert.Equal("žluťoučký 🧪", database.GetDiagnostic("fixture"));
            using var connection = database.OpenConnection();
            Assert.Equal("ok", Scalar(connection, "PRAGMA integrity_check"));
        }

        var backups = DatabaseBackups.List(directory.DatabasePath);
        if (version == 19)
        {
            Assert.Empty(backups);
            return;
        }

        var backup = Assert.Single(backups);
        using (var original = HarnessDatabase.OpenReadOnly(backup))
        {
            Assert.Equal(version, original.SchemaVersion());
            Assert.Equal("žluťoučký 🧪", original.GetDiagnostic("fixture"));
        }

        var restoredPath = Path.Combine(directory.Path, "restored.db");
        File.Copy(backup, restoredPath);
        using var restored = Open(restoredPath);
        Assert.Equal(19, restored.SchemaVersion());
        Assert.Equal("žluťoučký 🧪", restored.GetDiagnostic("fixture"));
    }

    [Fact]
    public void ForeignKeyRebuildPreservesCompletedAndBlockedTasksAndReferences()
    {
        using var directory = new TestDirectory();
        using (var historical = Open(directory.DatabasePath, new StorageOptions(), MigrationCatalog.All.Take(15).ToArray()))
        {
            using var connection = historical.OpenConnection();
            Execute(connection, "INSERT INTO tasks (id, title, status, completed_at, created_at, updated_at) VALUES ('A', 'a', 'done', 'now', 'now', 'now')");
            Execute(connection, "INSERT INTO tasks (id, title, status, blocked_reason, created_at, updated_at) VALUES ('B', 'b', 'blocked', 'later', 'now', 'now')");
            Execute(connection, "INSERT INTO task_dependencies VALUES ('B', 'A')");
        }

        using var upgraded = Open(directory.DatabasePath);
        using var check = upgraded.OpenConnection();
        Assert.Equal(2L, Scalar(check, "SELECT COUNT(*) FROM tasks"));
        Assert.Equal(1L, Scalar(check, "SELECT COUNT(*) FROM task_dependencies"));
        Assert.Equal(0L, Scalar(check, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        Assert.Equal(1L, Scalar(check, "PRAGMA foreign_keys"));
    }

    [Fact]
    public void FailedMigrationRollsBackSchemaDataAndLedgerAndNamesRecoveryBackup()
    {
        using var directory = new TestDirectory();
        var initial = new Migration(1, "0001_one", "CREATE TABLE one (x INTEGER) STRICT; INSERT INTO one VALUES (7);");
        using (var created = Open(directory.DatabasePath, new StorageOptions(), [initial]))
        {
            Assert.Equal(1, created.SchemaVersion());
        }

        var error = Assert.Throws<IOException>(() => Open(directory.DatabasePath, new StorageOptions(),
            [initial, new Migration(2, "0002_bad", "DELETE FROM one; CREATE TABLE partial (x INTEGER); INSERT INTO missing VALUES (1);")]));
        Assert.Contains("saved as", error.Message, StringComparison.Ordinal);
        Assert.IsType<SqliteException>(error.InnerException);
        using var database = HarnessDatabase.OpenReadOnly(directory.DatabasePath);
        using var connection = database.OpenConnection();
        Assert.Equal(1, database.SchemaVersion());
        Assert.Equal(7L, Scalar(connection, "SELECT x FROM one"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'partial'"));
        using var backup = HarnessDatabase.OpenReadOnly(Assert.Single(DatabaseBackups.List(directory.DatabasePath)));
        Assert.Equal(1, backup.SchemaVersion());
    }

    [Fact]
    public void ForeignKeyViolationsDuringRebuildRollBackAndRestoreEnforcement()
    {
        using var directory = new TestDirectory();
        var initial = new Migration(1, "0001_one", "CREATE TABLE parent (id TEXT PRIMARY KEY) STRICT; CREATE TABLE child (id TEXT REFERENCES parent(id)) STRICT; INSERT INTO parent VALUES ('p'); INSERT INTO child VALUES ('p');");
        using (var created = Open(directory.DatabasePath, new StorageOptions(), [initial]))
        {
            Assert.Equal(1, created.SchemaVersion());
        }

        var failure = Assert.Throws<IOException>(() => Open(directory.DatabasePath, new StorageOptions(),
            [initial, new Migration(2, "0002_bad", "-- harness: foreign keys off\nDELETE FROM parent;")]));
        Assert.IsType<InvalidDataException>(failure.InnerException);
        Assert.Contains("saved as", failure.Message, StringComparison.Ordinal);
        using var database = HarnessDatabase.OpenReadOnly(directory.DatabasePath);
        using var connection = database.OpenConnection();
        Assert.Equal(1, database.SchemaVersion());
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM parent"));
        using var writable = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = directory.DatabasePath, ForeignKeys = true, Pooling = false }.ToString());
        writable.Open();
        Assert.Throws<SqliteException>(() => Execute(writable, "INSERT INTO child VALUES ('missing')"));
    }

    [Theory]
    [InlineData("INSERT INTO schema_migrations VALUES (20, '0020_future', 'now')", true)]
    [InlineData("UPDATE schema_migrations SET name = '0001_wrong' WHERE version = 1", false)]
    [InlineData("INSERT INTO schema_migrations VALUES (0, '0000_invalid', 'now')", false)]
    public void InvalidOrNewerHistoryIsRejectedWithoutChangingTheDatabase(string mutation, bool newer)
    {
        using var directory = new TestDirectory();
        using (var database = Open(directory.DatabasePath))
        {
            using var connection = database.OpenConnection();
            Execute(connection, mutation);
        }

        var before = SHA256.HashData(File.ReadAllBytes(directory.DatabasePath));
        var exception = Record.Exception(() => Open(directory.DatabasePath));
        if (newer)
        {
            var tooNew = Assert.IsType<SchemaTooNewException>(exception);
            Assert.Contains("restore a backup", tooNew.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.IsType<InvalidDataException>(exception);
        }

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(directory.DatabasePath)));
        Assert.Empty(DatabaseBackups.List(directory.DatabasePath));
        using var inspected = HarnessDatabase.OpenReadOnly(directory.DatabasePath);
        Assert.NotNull(inspected.InspectMigrationState().Error);
        Assert.False(inspected.InspectMigrationState().Pending);
    }

    [Fact]
    public void ReadOnlyInspectionDoesNotCreateMigrateOrModifyFiles()
    {
        using var directory = new TestDirectory();
        var missing = Path.Combine(directory.Path, "missing", "h.db");
        Assert.Throws<FileNotFoundException>(() => HarnessDatabase.OpenReadOnly(missing));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        File.Copy(Fixture("schema-01.db"), directory.DatabasePath);
        var before = SHA256.HashData(File.ReadAllBytes(directory.DatabasePath));
        using (var database = HarnessDatabase.OpenReadOnly(directory.DatabasePath))
        {
            Assert.True(database.InspectMigrationState().Pending);
            Assert.Equal(1, database.InspectMigrationState().Applied);
            Assert.Equal("žluťoučký 🧪", database.GetDiagnostic("fixture"));
            Assert.Throws<SqliteException>(() => database.PutDiagnostic("fixture", "changed"));
        }

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(directory.DatabasePath)));
        Assert.False(Directory.Exists(DatabaseBackups.DirectoryPath(directory.DatabasePath)));
    }

    [Fact]
    public void BackupFailureAbortsUpgradeAndRetentionKeepsTheLastFive()
    {
        using var directory = new TestDirectory();
        var migrations = new List<Migration> { new(1, "0001_first", "CREATE TABLE t1 (x INTEGER) STRICT;") };
        using (var created = Open(directory.DatabasePath, new StorageOptions(), migrations))
        {
            Assert.Equal(1, created.SchemaVersion());
        }

        var backupDirectory = DatabaseBackups.DirectoryPath(directory.DatabasePath);
        File.WriteAllText(backupDirectory, "blocking-file");
        migrations.Add(new Migration(2, "0002_second", "CREATE TABLE t2 (x INTEGER) STRICT;"));
        Assert.Throws<IOException>(() => Open(directory.DatabasePath, new StorageOptions(), migrations));
        using (var unchanged = HarnessDatabase.OpenReadOnly(directory.DatabasePath))
        {
            Assert.Equal(1, unchanged.SchemaVersion());
        }

        File.Delete(backupDirectory);
        for (var version = 2; version <= 8; version++)
        {
            if (version > 2)
            {
                migrations.Add(new Migration(version, $"{version:0000}_next", $"CREATE TABLE t{version} (x INTEGER) STRICT;"));
            }

            using var upgraded = Open(directory.DatabasePath, new StorageOptions(), migrations);
            Assert.Equal(version, upgraded.SchemaVersion());
        }

        var backups = DatabaseBackups.List(directory.DatabasePath);
        Assert.Equal(DatabaseBackups.KeepBackups, backups.Count);
        Assert.StartsWith("schema-07-", Path.GetFileName(backups[0]), StringComparison.Ordinal);
        Assert.StartsWith("schema-03-", Path.GetFileName(backups[^1]), StringComparison.Ordinal);
        foreach (var backup in backups)
        {
            using var database = HarnessDatabase.OpenReadOnly(backup);
            using var connection = database.OpenConnection();
            Assert.Equal("ok", Scalar(connection, "PRAGMA integrity_check"));
        }
    }

    [Fact]
    public void TransactionsCommitOrRollBackAndReadersCannotWrite()
    {
        using var directory = new TestDirectory();
        using var database = Open(directory.DatabasePath);
        WriteTransaction(database, (connection, transaction) =>
        {
            Execute(connection, "INSERT INTO diagnostics VALUES ('committed', 'yes', 'now')", transaction);
            return 0;
        });
        Assert.Throws<InvalidOperationException>(() => WriteTransaction<int>(database, (connection, transaction) =>
        {
            Execute(connection, "DELETE FROM diagnostics", transaction);
            throw new InvalidOperationException("rollback");
        }));
        Assert.Equal("yes", database.GetDiagnostic("committed"));
        Assert.Throws<SqliteException>(() => ReadTransaction(database, (connection, transaction) =>
        {
            Execute(connection, "DELETE FROM diagnostics", transaction);
            return 0;
        }));
        Assert.Equal("yes", ReadTransaction(database, (connection, transaction) => Scalar(connection, "SELECT value FROM diagnostics", transaction)));
        database.PutDiagnostic("after-reader", "still writable");
        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => WriteTransaction(database, (connection, transaction) =>
        {
            Execute(connection, "DELETE FROM diagnostics", transaction);
            cancel.Cancel();
            return 0;
        }, cancel.Token));
        Assert.Equal("yes", database.GetDiagnostic("committed"));
    }

    [Fact]
    public void NewFilesAndBackupsAreOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix mode bits do not apply on Windows; as in Go, Windows files inherit the containing directory ACL.");
            return;
        }

        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "private", "h.db");
        using var database = Open(path, new StorageOptions(), MigrationCatalog.All.Take(1).ToArray());
        database.PutDiagnostic("permission", "test");
        using var connection = database.OpenConnection();
        var backup = DatabaseBackups.Create(connection, path, 1);
        foreach (var file in new[] { path, path + "-wal", path + "-shm", backup })
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(DatabaseBackups.DirectoryPath(path)));
    }

    [Fact]
    public void PathsCannotInjectOptionsAndTimestampsKeepNineFractionDigits()
    {
        using var directory = new TestDirectory();
        Assert.Throws<ArgumentException>(() => Open(string.Empty));
        Assert.Throws<ArgumentException>(() => Open(Path.Combine(directory.Path, "bad?mode=memory")));
        var path = Path.Combine(directory.Path, "with space # ; ž", "h.db");
        using (var database = Open(path))
        {
            database.PutDiagnostic("k", "v");
        }

        using var read = HarnessDatabase.OpenReadOnly(path);
        Assert.Equal("v", read.GetDiagnostic("k"));
        Assert.Equal("2026-01-02T03:04:05.123456700Z", HarnessDatabase.FormatTime(DateTimeOffset.Parse("2026-01-02T05:04:05.1234567+02:00", CultureInfo.InvariantCulture)));
        var zero = DateTimeOffset.Parse("2026-01-02T03:04:05Z", CultureInfo.InvariantCulture);
        Assert.True(string.CompareOrdinal(HarnessDatabase.FormatTime(zero), HarnessDatabase.FormatTime(zero.AddMilliseconds(100))) < 0);
    }

    [Fact]
    public async Task CompetingProcessesMigrateExactlyOnceAndKeepAllWrites()
    {
        using var directory = new TestDirectory();
        var children = new List<Process>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                children.Add(StartWorker("put", directory.DatabasePath, $"worker-{index}", "persisted"));
            }

            foreach (var child in children)
            {
                await ExpectSuccess(child);
            }
        }
        finally
        {
            foreach (var child in children)
            {
                Stop(child);
                child.Dispose();
            }
        }

        using var database = HarnessDatabase.OpenReadOnly(directory.DatabasePath);
        using var connection = database.OpenConnection();
        Assert.Equal(19L, Scalar(connection, "SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(4L, Scalar(connection, "SELECT COUNT(*) FROM diagnostics"));
        Assert.Equal("ok", Scalar(connection, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task CurrentSchemaAndReadersDoNotWaitForWriterButImmediateWritersTimeOut()
    {
        using var directory = new TestDirectory();
        using (var created = Open(directory.DatabasePath))
        {
            Assert.Equal(19, created.SchemaVersion());
        }

        using var child = StartWorker("hold", directory.DatabasePath);
        try
        {
            Assert.Equal("LOCKED", await child.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            using var current = Open(directory.DatabasePath, new StorageOptions { BusyTimeout = TimeSpan.FromSeconds(1) });
            Assert.Equal(19, ReadTransaction(current, (connection, transaction) => Convert.ToInt32(Scalar(connection, "SELECT MAX(version) FROM schema_migrations", transaction), CultureInfo.InvariantCulture)));
            var timer = Stopwatch.StartNew();
            var locked = Assert.Throws<SqliteException>(() => WriteTransaction(current, (connection, transaction) => 0));
            Assert.Equal(5, locked.SqliteErrorCode);
            Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(850), TimeSpan.FromSeconds(3));
            using var shortBudget = Open(directory.DatabasePath, new StorageOptions { BusyTimeout = TimeSpan.FromMilliseconds(100) });
            using (var connection = shortBudget.OpenConnection())
            {
                Assert.Equal(100L, Scalar(connection, "PRAGMA busy_timeout"));
            }

            timer.Restart();
            Assert.Throws<SqliteException>(() => WriteTransaction(shortBudget, (connection, transaction) => 0));
            Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(750));
            await child.StandardInput.WriteLineAsync("release");
            await child.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            await ExpectSuccess(child);
        }
        finally
        {
            Stop(child);
        }
    }

    [Fact]
    public async Task GoThenCSharpThenGoPreservesSchemaAndUnicodeData()
    {
        var helper = Environment.GetEnvironmentVariable("HARNESS_GO_STORAGE_ORACLE");
        if (string.IsNullOrEmpty(helper))
        {
            Assert.Skip("Set HARNESS_GO_STORAGE_ORACLE to the helper built by Storage/Go/build-oracle.ps1.");
            return;
        }

        using var directory = new TestDirectory();
        using (var go = Start(helper, "put", directory.DatabasePath, "go", "Go → C# 🧪"))
        {
            await ExpectSuccess(go);
        }

        using (var database = Open(directory.DatabasePath))
        {
            Assert.Equal("Go → C# 🧪", database.GetDiagnostic("go"));
            database.PutDiagnostic("csharp", "C# → Go ž");
        }

        using (var go = Start(helper, "get", directory.DatabasePath, "csharp"))
        {
            Assert.Contains("C# → Go ž", await ExpectSuccess(go), StringComparison.Ordinal);
        }

        using var final = HarnessDatabase.OpenReadOnly(directory.DatabasePath);
        Assert.Equal(19, final.SchemaVersion());
        Assert.Equal("Go → C# 🧪", final.GetDiagnostic("go"));
    }

    private static HarnessDatabase Open(string path, StorageOptions? options = null) =>
        HarnessDatabase.Open(path, options, TestContext.Current.CancellationToken);

    private static HarnessDatabase Open(string path, StorageOptions options, IReadOnlyList<Migration> migrations) =>
        HarnessDatabase.Open(path, options, migrations, TestContext.Current.CancellationToken);

    private static T WriteTransaction<T>(HarnessDatabase database, Func<SqliteConnection, SqliteTransaction, T> action) =>
        database.WithTransaction(action, TestContext.Current.CancellationToken);

    private static T WriteTransaction<T>(HarnessDatabase database, Func<SqliteConnection, SqliteTransaction, T> action, CancellationToken cancellationToken) =>
        database.WithTransaction(action, cancellationToken);

    private static T ReadTransaction<T>(HarnessDatabase database, Func<SqliteConnection, SqliteTransaction, T> action) =>
        database.ReadTransaction(action, TestContext.Current.CancellationToken);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only helper executes fixed test SQL, never external input.")]
    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only helper executes fixed test SQL and deliberate invalid-schema fixtures.")]
    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static Process StartWorker(params string[] arguments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        var worker = Path.Combine(root.FullName, "test", "Harness.Integration", "Storage", "Worker", "bin", Configuration, "net10.0", "Harness.StorageWorker.dll");
        return Start("dotnet", [worker, .. arguments]);
    }

    private static Process Start(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("Could not start storage worker.");
    }

    private static async Task<string> ExpectSuccess(Process child)
    {
        try
        {
            var output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken);
            Assert.True(child.ExitCode == 0, await error);
            return await output;
        }
        finally
        {
            Stop(child);
        }
    }

    private static void Stop(Process child)
    {
        if (!child.HasExited)
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"harness-storage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string DatabasePath => System.IO.Path.Combine(Path, "h.db");

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
