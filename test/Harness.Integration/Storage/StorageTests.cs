using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
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
        MigrationCatalog.All.Count.Should().Be(19);
        MigrationCatalog.All.Count.Should().Be(frozen.Length);
        for (var index = 0; index < frozen.Length; index++)
        {
            var migration = MigrationCatalog.All[index];
            migration.Version.Should().Be(index + 1);
            migration.Name.Should().Be(Path.GetFileNameWithoutExtension(frozen[index].GetProperty("path").GetString()));
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(migration.Sql))).Should().Be(frozen[index].GetProperty("sha256").GetString());
        }
    }

    [Fact]
    public void MigrationFilesMustHaveValidNamesAndContiguousUniqueVersions()
    {
        FluentActions.Invoking(() => MigrationCatalog.Load([("readme.txt", "")])).Should().ThrowExactly<InvalidDataException>();
        FluentActions.Invoking(() => MigrationCatalog.Load([("0002_gap.sql", "")])).Should().ThrowExactly<InvalidDataException>();
        FluentActions.Invoking(() => MigrationCatalog.Load([("0000_zero.sql", "")])).Should().ThrowExactly<InvalidDataException>();
        FluentActions.Invoking(() => MigrationCatalog.Load([("0001_one.sql", ""), ("0001_other.sql", "")])).Should().ThrowExactly<InvalidDataException>();
        MigrationCatalog.Load([("0002_two.sql", ""), ("0001_one.sql", "")]).Select(migration => migration.Version).Should().Equal(1, 2);
    }

    [Fact]
    public void FreshDatabaseHasPragmasStrictSchemaAndPersistentDiagnostics()
    {
        using var directory = new TestDirectory();
        using (var database = Open(directory.DatabasePath))
        {
            database.SchemaVersion().Should().Be(19);
            DatabaseBackups.List(directory.DatabasePath).Should().BeEmpty();
            using var connection = database.OpenConnection();
            Scalar(connection, "PRAGMA journal_mode").Should().Be("wal");
            Scalar(connection, "PRAGMA foreign_keys").Should().Be(1L);
            Scalar(connection, "PRAGMA busy_timeout").Should().Be(5000L);
            FluentActions.Invoking(() => Execute(connection, "INSERT INTO schema_migrations VALUES ('text', 'bad', 'bad')")).Should().ThrowExactly<SqliteException>();
            Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check").Should().Be(0L);
            database.PutDiagnostic("quote';--", "žluťoučký 🧪");
            FluentActions.Invoking(() => database.GetDiagnostic("absent")).Should().ThrowExactly<KeyNotFoundException>();
        }

        using var reopened = Open(directory.DatabasePath);
        reopened.GetDiagnostic("quote';--").Should().Be("žluťoučký 🧪");
        reopened.InspectMigrationState().Pending.Should().BeFalse();
        DatabaseBackups.List(directory.DatabasePath).Should().BeEmpty();
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
            database.SchemaVersion().Should().Be(19);
            database.GetDiagnostic("fixture").Should().Be("žluťoučký 🧪");
            using var connection = database.OpenConnection();
            Scalar(connection, "PRAGMA integrity_check").Should().Be("ok");
        }

        var backups = DatabaseBackups.List(directory.DatabasePath);
        if (version == 19)
        {
            backups.Should().BeEmpty();
            return;
        }

        var backup = backups.Should().ContainSingle().Which;
        using (var original = HarnessDatabase.OpenReadOnly(backup))
        {
            original.SchemaVersion().Should().Be(version);
            original.GetDiagnostic("fixture").Should().Be("žluťoučký 🧪");
        }

        var restoredPath = Path.Combine(directory.Path, "restored.db");
        File.Copy(backup, restoredPath);
        using var restored = Open(restoredPath);
        restored.SchemaVersion().Should().Be(19);
        restored.GetDiagnostic("fixture").Should().Be("žluťoučký 🧪");
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
        Scalar(check, "SELECT COUNT(*) FROM tasks").Should().Be(2L);
        Scalar(check, "SELECT COUNT(*) FROM task_dependencies").Should().Be(1L);
        Scalar(check, "SELECT COUNT(*) FROM pragma_foreign_key_check").Should().Be(0L);
        Scalar(check, "PRAGMA foreign_keys").Should().Be(1L);
    }

    [Fact]
    public void FailedMigrationRollsBackSchemaDataAndLedgerAndNamesRecoveryBackup()
    {
        using var directory = new TestDirectory();
        var initial = new Migration(1, "0001_one", "CREATE TABLE one (x INTEGER) STRICT; INSERT INTO one VALUES (7);");
        using (var created = Open(directory.DatabasePath, new StorageOptions(), [initial]))
        {
            created.SchemaVersion().Should().Be(1);
        }

        var error = FluentActions.Invoking(() => Open(directory.DatabasePath, new StorageOptions(),
            [initial, new Migration(2, "0002_bad", "DELETE FROM one; CREATE TABLE partial (x INTEGER); INSERT INTO missing VALUES (1);")])).Should().ThrowExactly<IOException>().Which;
        error.Message.Should().Contain("saved as");
        error.InnerException.Should().BeOfType<SqliteException>();
        using var database = HarnessDatabase.OpenReadOnly(directory.DatabasePath);
        using var connection = database.OpenConnection();
        database.SchemaVersion().Should().Be(1);
        Scalar(connection, "SELECT x FROM one").Should().Be(7L);
        Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'partial'").Should().Be(0L);
        using var backup = HarnessDatabase.OpenReadOnly(DatabaseBackups.List(directory.DatabasePath).Should().ContainSingle().Which);
        backup.SchemaVersion().Should().Be(1);
    }

    [Fact]
    public void ForeignKeyViolationsDuringRebuildRollBackAndRestoreEnforcement()
    {
        using var directory = new TestDirectory();
        var initial = new Migration(1, "0001_one", "CREATE TABLE parent (id TEXT PRIMARY KEY) STRICT; CREATE TABLE child (id TEXT REFERENCES parent(id)) STRICT; INSERT INTO parent VALUES ('p'); INSERT INTO child VALUES ('p');");
        using (var created = Open(directory.DatabasePath, new StorageOptions(), [initial]))
        {
            created.SchemaVersion().Should().Be(1);
        }

        var failure = FluentActions.Invoking(() => Open(directory.DatabasePath, new StorageOptions(),
            [initial, new Migration(2, "0002_bad", "-- harness: foreign keys off\nDELETE FROM parent;")])).Should().ThrowExactly<IOException>().Which;
        failure.InnerException.Should().BeOfType<InvalidDataException>();
        failure.Message.Should().Contain("saved as");
        using var database = HarnessDatabase.OpenReadOnly(directory.DatabasePath);
        using var connection = database.OpenConnection();
        database.SchemaVersion().Should().Be(1);
        Scalar(connection, "SELECT COUNT(*) FROM parent").Should().Be(1L);
        using var writable = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = directory.DatabasePath, ForeignKeys = true, Pooling = false }.ToString());
        writable.Open();
        FluentActions.Invoking(() => Execute(writable, "INSERT INTO child VALUES ('missing')")).Should().ThrowExactly<SqliteException>();
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
            var tooNew = exception.Should().BeOfType<SchemaTooNewException>().Which;
            tooNew.Message.Should().Contain("restore a backup");
        }
        else
        {
            exception.Should().BeOfType<InvalidDataException>();
        }

        SHA256.HashData(File.ReadAllBytes(directory.DatabasePath)).Should().Equal(before);
        DatabaseBackups.List(directory.DatabasePath).Should().BeEmpty();
        using var inspected = HarnessDatabase.OpenReadOnly(directory.DatabasePath);
        inspected.InspectMigrationState().Error.Should().NotBeNull();
        inspected.InspectMigrationState().Pending.Should().BeFalse();
    }

    [Fact]
    public void ReadOnlyInspectionDoesNotCreateMigrateOrModifyFiles()
    {
        using var directory = new TestDirectory();
        var missing = Path.Combine(directory.Path, "missing", "h.db");
        FluentActions.Invoking(() => HarnessDatabase.OpenReadOnly(missing)).Should().ThrowExactly<FileNotFoundException>();
        Directory.Exists(Path.GetDirectoryName(missing)).Should().BeFalse();
        File.Copy(Fixture("schema-01.db"), directory.DatabasePath);
        var before = SHA256.HashData(File.ReadAllBytes(directory.DatabasePath));
        using (var database = HarnessDatabase.OpenReadOnly(directory.DatabasePath))
        {
            database.InspectMigrationState().Pending.Should().BeTrue();
            database.InspectMigrationState().Applied.Should().Be(1);
            database.GetDiagnostic("fixture").Should().Be("žluťoučký 🧪");
            FluentActions.Invoking(() => database.PutDiagnostic("fixture", "changed")).Should().ThrowExactly<SqliteException>();
        }

        SHA256.HashData(File.ReadAllBytes(directory.DatabasePath)).Should().Equal(before);
        Directory.Exists(DatabaseBackups.DirectoryPath(directory.DatabasePath)).Should().BeFalse();
    }

    [Fact]
    public void BackupFailureAbortsUpgradeAndRetentionKeepsTheLastFive()
    {
        using var directory = new TestDirectory();
        var migrations = new List<Migration> { new(1, "0001_first", "CREATE TABLE t1 (x INTEGER) STRICT;") };
        using (var created = Open(directory.DatabasePath, new StorageOptions(), migrations))
        {
            created.SchemaVersion().Should().Be(1);
        }

        var backupDirectory = DatabaseBackups.DirectoryPath(directory.DatabasePath);
        File.WriteAllText(backupDirectory, "blocking-file");
        migrations.Add(new Migration(2, "0002_second", "CREATE TABLE t2 (x INTEGER) STRICT;"));
        FluentActions.Invoking(() => Open(directory.DatabasePath, new StorageOptions(), migrations)).Should().ThrowExactly<IOException>();
        using (var unchanged = HarnessDatabase.OpenReadOnly(directory.DatabasePath))
        {
            unchanged.SchemaVersion().Should().Be(1);
        }

        File.Delete(backupDirectory);
        for (var version = 2; version <= 8; version++)
        {
            if (version > 2)
            {
                migrations.Add(new Migration(version, $"{version:0000}_next", $"CREATE TABLE t{version} (x INTEGER) STRICT;"));
            }

            using var upgraded = Open(directory.DatabasePath, new StorageOptions(), migrations);
            upgraded.SchemaVersion().Should().Be(version);
        }

        var backups = DatabaseBackups.List(directory.DatabasePath);
        backups.Count.Should().Be(DatabaseBackups.KeepBackups);
        Path.GetFileName(backups[0]).Should().StartWith("schema-07-");
        Path.GetFileName(backups[^1]).Should().StartWith("schema-03-");
        foreach (var backup in backups)
        {
            using var database = HarnessDatabase.OpenReadOnly(backup);
            using var connection = database.OpenConnection();
            Scalar(connection, "PRAGMA integrity_check").Should().Be("ok");
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
        FluentActions.Invoking(() => WriteTransaction<int>(database, (connection, transaction) =>
        {
            Execute(connection, "DELETE FROM diagnostics", transaction);
            throw new InvalidOperationException("rollback");
        })).Should().ThrowExactly<InvalidOperationException>();
        database.GetDiagnostic("committed").Should().Be("yes");
        FluentActions.Invoking(() => ReadTransaction(database, (connection, transaction) =>
        {
            Execute(connection, "DELETE FROM diagnostics", transaction);
            return 0;
        })).Should().ThrowExactly<SqliteException>();
        ReadTransaction(database, (connection, transaction) => Scalar(connection, "SELECT value FROM diagnostics", transaction)).Should().Be("yes");
        database.PutDiagnostic("after-reader", "still writable");
        using var cancel = new CancellationTokenSource();
        FluentActions.Invoking(() => WriteTransaction(database, (connection, transaction) =>
        {
            Execute(connection, "DELETE FROM diagnostics", transaction);
            cancel.Cancel();
            return 0;
        }, cancel.Token)).Should().ThrowExactly<OperationCanceledException>();
        database.GetDiagnostic("committed").Should().Be("yes");
    }

    [Fact]
    public void NewFilesAndBackupsAreOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Unix mode bits do not apply on Windows; as in Go, Windows files inherit the containing directory ACL.");
        }

        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "private", "h.db");
        using var database = Open(path, new StorageOptions(), MigrationCatalog.All.Take(1).ToArray());
        database.PutDiagnostic("permission", "test");
        using var connection = database.OpenConnection();
        var backup = DatabaseBackups.Create(connection, path, 1);
        foreach (var file in new[] { path, path + "-wal", path + "-shm", backup })
        {
            File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.GetUnixFileMode(Path.GetDirectoryName(path)!).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(DatabaseBackups.DirectoryPath(path)).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void PathsCannotInjectOptionsAndTimestampsKeepNineFractionDigits()
    {
        using var directory = new TestDirectory();
        FluentActions.Invoking(() => Open(string.Empty)).Should().ThrowExactly<ArgumentException>();
        FluentActions.Invoking(() => Open(Path.Combine(directory.Path, "bad?mode=memory"))).Should().ThrowExactly<ArgumentException>();
        var path = Path.Combine(directory.Path, "with space # ; ž", "h.db");
        using (var database = Open(path))
        {
            database.PutDiagnostic("k", "v");
        }

        using var read = HarnessDatabase.OpenReadOnly(path);
        read.GetDiagnostic("k").Should().Be("v");
        HarnessDatabase.FormatTime(DateTimeOffset.Parse("2026-01-02T05:04:05.1234567+02:00", CultureInfo.InvariantCulture)).Should().Be("2026-01-02T03:04:05.123456700Z");
        var zero = DateTimeOffset.Parse("2026-01-02T03:04:05Z", CultureInfo.InvariantCulture);
        (string.CompareOrdinal(HarnessDatabase.FormatTime(zero), HarnessDatabase.FormatTime(zero.AddMilliseconds(100))) < 0).Should().BeTrue();
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
        Scalar(connection, "SELECT COUNT(*) FROM schema_migrations").Should().Be(19L);
        Scalar(connection, "SELECT COUNT(*) FROM diagnostics").Should().Be(4L);
        Scalar(connection, "PRAGMA integrity_check").Should().Be("ok");
    }

    [Fact]
    public async Task CurrentSchemaAndReadersDoNotWaitForWriterButImmediateWritersTimeOut()
    {
        using var directory = new TestDirectory();
        using (var created = Open(directory.DatabasePath))
        {
            created.SchemaVersion().Should().Be(19);
        }

        using var child = StartWorker("hold", directory.DatabasePath);
        try
        {
            (await child.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Should().Be("LOCKED");
            using var current = Open(directory.DatabasePath, new StorageOptions { BusyTimeout = TimeSpan.FromSeconds(1) });
            ReadTransaction(current, (connection, transaction) => Convert.ToInt32(Scalar(connection, "SELECT MAX(version) FROM schema_migrations", transaction), CultureInfo.InvariantCulture)).Should().Be(19);
            var timer = Stopwatch.StartNew();
            var locked = FluentActions.Invoking(() => WriteTransaction(current, (connection, transaction) => 0)).Should().ThrowExactly<SqliteException>().Which;
            locked.SqliteErrorCode.Should().Be(5);
            timer.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(850)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(3));
            using var shortBudget = Open(directory.DatabasePath, new StorageOptions { BusyTimeout = TimeSpan.FromMilliseconds(100) });
            using (var connection = shortBudget.OpenConnection())
            {
                Scalar(connection, "PRAGMA busy_timeout").Should().Be(100L);
            }

            timer.Restart();
            FluentActions.Invoking(() => WriteTransaction(shortBudget, (connection, transaction) => 0)).Should().ThrowExactly<SqliteException>();
            timer.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(75)).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(750));
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
            throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_STORAGE_ORACLE to the helper built by Storage/Go/build-oracle.ps1.");
        }

        using var directory = new TestDirectory();
        using (var go = Start(helper, "put", directory.DatabasePath, "go", "Go → C# 🧪"))
        {
            await ExpectSuccess(go);
        }

        using (var database = Open(directory.DatabasePath))
        {
            database.GetDiagnostic("go").Should().Be("Go → C# 🧪");
            database.PutDiagnostic("csharp", "C# → Go ž");
        }

        using (var go = Start(helper, "get", directory.DatabasePath, "csharp"))
        {
            (await ExpectSuccess(go)).Should().Contain("C# → Go ž");
        }

        using var final = HarnessDatabase.OpenReadOnly(directory.DatabasePath);
        final.SchemaVersion().Should().Be(19);
        final.GetDiagnostic("go").Should().Be("Go → C# 🧪");
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
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Harness.slnx")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        var worker = Path.Combine(root.FullName, "test", "Harness.Integration", "Storage", "Worker", "bin", Configuration, "net10.0", "Harness.StorageWorker.dll");
        File.Exists(worker).Should().BeTrue($"Missing storage worker: {worker}");
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
            (child.ExitCode == 0).Should().BeTrue(await error);
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
