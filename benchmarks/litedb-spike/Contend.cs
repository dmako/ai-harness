using System.Diagnostics;
using System.Globalization;
using LiteDB;
using Microsoft.Data.Sqlite;

namespace LiteDbSpike;

/// <summary>
/// The guarantees harness's StorageTests check for SQLite, asked of each engine: several processes open one new
/// database at once, apply the same migrations (each exactly once), then each writes in its own transactions with a
/// read-modify-write counter (a lost update shows as a short count) and reads after every write.
/// </summary>
internal static class Contend
{
    private const int Migrations = 5;
    private const int MigrationRows = 200;

    public static int Run(string engine, int processes, int writes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "litedb-spike-contend-" + Environment.ProcessId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, engine is "sqlite" ? "c.sqlite" : "c.litedb");
        var timer = Stopwatch.StartNew();
        var children = Enumerable
            .Range(0, processes)
            .Select(i =>
                Process.Start(
                    new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        ArgumentList = { "contend-child", engine, path, "p" + i, writes.ToString(CultureInfo.InvariantCulture) },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    }
                )!
            )
            .ToList();
        var lines = children.Select(child => (Out: child.StandardOutput.ReadToEndAsync(), Err: child.StandardError.ReadToEndAsync())).ToList();
        foreach (var child in children)
        {
            child.WaitForExit();
        }

        var elapsed = timer.Elapsed;
        var failed = 0;
        foreach (var (child, output) in children.Zip(lines))
        {
            var text = output.Out.Result.Trim();
            var error = output.Err.Result.Trim();
            Console.WriteLine($"  child exit {child.ExitCode}: {text}{(error.Length > 0 ? " | " + error.Split('\n')[0] : "")}");
            failed += child.ExitCode is 0 ? 0 : 1;
        }

        var (ledger, migrated, rows, counter) = Verify(engine, path);
        var expected = processes * writes;
        var ok = failed is 0 && ledger == Migrations && migrated == Migrations * MigrationRows && rows == expected && counter == expected;
        Console.WriteLine(
            $"{engine}: {processes} processes x {writes} writes in {elapsed.TotalSeconds:F2} s; "
                + $"ledger {ledger}/{Migrations}, migration rows {migrated}/{Migrations * MigrationRows}, "
                + $"rows {rows}/{expected}, counter {counter}/{expected}, failed processes {failed} => {(ok ? "PASS" : "FAIL")}"
        );
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException) { }

        return ok ? 0 : 1;
    }

    public static int Child(string engine, string path, string id, int writes)
    {
        var latencies = new List<double>();
        var errors = new List<string>();
        try
        {
            using IContendStore store = Open(engine, path);
            var applied = store.Migrate();
            for (var i = 0; i < writes; i++)
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    store.Write(id, i);
                    _ = store.Count();
                }
                catch (Exception error) when (error is LiteException or SqliteException or IOException)
                {
                    errors.Add(error.GetType().Name + ": " + error.Message);
                }

                latencies.Add(timer.Elapsed.TotalMilliseconds);
            }

            latencies.Sort();
            Console.WriteLine(
                $"{id} applied {applied} migration(s), {writes - errors.Count} writes ok, {errors.Count} errors, "
                    + $"write p50 {latencies[latencies.Count / 2]:F1} ms, max {latencies[^1]:F1} ms"
                    + (errors.Count > 0 ? " first error: " + errors[0] : "")
            );
            return errors.Count is 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.WriteLine($"{id} FAILED: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    /// <summary>
    /// A writer and a reader while another process holds a write transaction for some seconds: SQLite waits up to
    /// its busy_timeout (harness: 5 s) and reads run beside it (WAL); this shows what LiteDB does.
    /// </summary>
    public static int Hold(string engine, int seconds)
    {
        var directory = Path.Combine(Path.GetTempPath(), "litedb-spike-hold-" + Environment.ProcessId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, engine is "sqlite" ? "h.sqlite" : "h.litedb");
        using (var setup = Open(engine, path))
        {
            setup.Migrate();
            setup.Write("setup", 0);
        }

        using var store = Open(engine, path);
        using var holder = Process.Start(
            new ProcessStartInfo(Environment.ProcessPath!)
            {
                ArgumentList = { "hold-child", engine, path, seconds.ToString(CultureInfo.InvariantCulture) },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }
        )!;
        var started = holder.StandardOutput.ReadLine();
        Console.WriteLine($"holder: {started} (holds its write transaction {seconds} s)");
        Measure("read ", () => store.Count().ToString(CultureInfo.InvariantCulture));
        Measure("write", () =>
        {
            store.Write("waiter", 1);
            return "written";
        });
        holder.WaitForExit();
        Console.WriteLine("holder: " + holder.StandardOutput.ReadToEnd().Trim());
        return 0;

        static void Measure(string what, Func<string> action)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                var result = action();
                Console.WriteLine($"{what} during the hold: {result} after {timer.Elapsed.TotalSeconds:F2} s");
            }
            catch (Exception error)
            {
                Console.WriteLine($"{what} during the hold: {error.GetType().Name}: {error.Message} after {timer.Elapsed.TotalSeconds:F2} s");
            }
        }
    }

    public static int HoldChild(string engine, string path, int seconds)
    {
        using var store = Open(engine, path);
        store.HoldWrite(
            () =>
            {
                Console.WriteLine("transaction open");
                Console.Out.Flush();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
            }
        );
        Console.WriteLine("committed");
        return 0;
    }

    private static IContendStore Open(string engine, string path) =>
        engine switch
        {
            "sqlite" => new SqliteStore(path),
            "shared" => new LiteStore(path, ConnectionType.Shared),
            "direct" => new LiteStore(path, ConnectionType.Direct),
            _ => throw new ArgumentException("engine: sqlite, shared or direct"),
        };

    private static (long Ledger, long Migrated, long Rows, long Counter) Verify(string engine, string path)
    {
        if (engine is "sqlite")
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            long Scalar(string sql)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
            }

            return (
                Scalar("SELECT count(*) FROM ledger"),
                Scalar("SELECT count(*) FROM migrated"),
                Scalar("SELECT count(*) FROM rows"),
                Scalar("SELECT coalesce(max(n),0) FROM counter")
            );
        }

        using var db = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Shared });
        return (
            db.GetCollection("ledger").Count(),
            db.GetCollection("migrated").Count(),
            db.GetCollection("rows").Count(),
            db.GetCollection("counter").FindById("c")?["n"].AsInt64 ?? 0
        );
    }

    private interface IContendStore : IDisposable
    {
        int Migrate();
        void Write(string process, int i);
        long Count();
        void HoldWrite(Action whileHeld);
    }

    /// <summary>SQLite configured as HarnessDatabase does: pooled, WAL, busy_timeout 5 s, immediate write transactions.</summary>
    private sealed class SqliteStore : IContendStore
    {
        private readonly string _connectionString;

        public SqliteStore(string path)
        {
            _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
            using var connection = Connection();
            var timer = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    Execute(connection, null, "PRAGMA journal_mode = WAL");
                    break;
                }
                catch (SqliteException) when (timer.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(5);
                }
            }

            Immediate(
                (c, t) =>
                {
                    Execute(c, t, "CREATE TABLE IF NOT EXISTS ledger(version INTEGER PRIMARY KEY)");
                    Execute(c, t, "CREATE TABLE IF NOT EXISTS migrated(id INTEGER PRIMARY KEY, version INTEGER)");
                    Execute(c, t, "CREATE TABLE IF NOT EXISTS rows(process TEXT, i INTEGER)");
                    Execute(c, t, "CREATE TABLE IF NOT EXISTS counter(id TEXT PRIMARY KEY, n INTEGER)");
                    return 0;
                }
            );
        }

        public int Migrate()
        {
            var applied = 0;
            for (var version = 1; version <= Migrations; version++)
            {
                applied += Immediate(
                    (c, t) =>
                    {
                        if (Scalar(c, t, $"SELECT count(*) FROM ledger WHERE version={version}") > 0)
                        {
                            return 0;
                        }

                        for (var row = 0; row < MigrationRows; row++)
                        {
                            Execute(c, t, $"INSERT INTO migrated(version) VALUES({version})");
                        }

                        Execute(c, t, $"INSERT INTO ledger(version) VALUES({version})");
                        return 1;
                    }
                );
            }

            return applied;
        }

        public void Write(string process, int i) =>
            Immediate(
                (c, t) =>
                {
                    Execute(c, t, $"INSERT INTO rows(process,i) VALUES('{process}',{i})");
                    var n = Scalar(c, t, "SELECT coalesce(max(n),0) FROM counter WHERE id='c'");
                    Execute(c, t, $"INSERT INTO counter(id,n) VALUES('c',{n + 1}) ON CONFLICT(id) DO UPDATE SET n=excluded.n");
                    return 0;
                }
            );

        public long Count()
        {
            using var connection = Connection();
            return Scalar(connection, null, "SELECT count(*) FROM rows");
        }

        public void HoldWrite(Action whileHeld) =>
            Immediate(
                (c, t) =>
                {
                    Execute(c, t, "INSERT INTO rows(process,i) VALUES('holder',0)");
                    whileHeld();
                    return 0;
                }
            );

        public void Dispose() => SqliteConnection.ClearAllPools();

        private SqliteConnection Connection()
        {
            var connection = new SqliteConnection(_connectionString) { DefaultTimeout = -1 };
            connection.Open();
            Execute(connection, null, "PRAGMA busy_timeout = 5000");
            return connection;
        }

        private int Immediate(Func<SqliteConnection, SqliteTransaction, int> action)
        {
            using var connection = Connection();
            using var transaction = connection.BeginTransaction(deferred: false);
            var result = action(connection, transaction);
            transaction.Commit();
            return result;
        }

        private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static long Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>LiteDB with one long-lived LiteDatabase per process, as harness's MCP server and ui would keep it.</summary>
    private sealed class LiteStore(string path, ConnectionType connection) : IContendStore
    {
        private readonly LiteDatabase _db = new(new ConnectionString { Filename = path, Connection = connection });

        public int Migrate()
        {
            var applied = 0;
            for (var version = 1; version <= Migrations; version++)
            {
                applied += Transaction(() =>
                {
                    var ledger = _db.GetCollection("ledger");
                    if (ledger.FindById(version) is not null)
                    {
                        return 0;
                    }

                    var migrated = _db.GetCollection("migrated", BsonAutoId.Int64);
                    for (var row = 0; row < MigrationRows; row++)
                    {
                        migrated.Insert(new BsonDocument { ["version"] = version });
                    }

                    ledger.Insert(new BsonDocument { ["_id"] = version });
                    return 1;
                });
            }

            return applied;
        }

        public void Write(string process, int i) =>
            Transaction(() =>
            {
                _db.GetCollection("rows", BsonAutoId.Int64).Insert(new BsonDocument { ["process"] = process, ["i"] = i });
                var counter = _db.GetCollection("counter");
                var n = counter.FindById("c")?["n"].AsInt64 ?? 0;
                counter.Upsert(new BsonDocument { ["_id"] = "c", ["n"] = n + 1 });
                return 0;
            });

        public long Count() => _db.GetCollection("rows").LongCount();

        public void HoldWrite(Action whileHeld) =>
            Transaction(() =>
            {
                _db.GetCollection("rows", BsonAutoId.Int64).Insert(new BsonDocument { ["process"] = "holder", ["i"] = 0 });
                whileHeld();
                return 0;
            });

        public void Dispose() => _db.Dispose();

        private int Transaction(Func<int> action)
        {
            _db.BeginTrans();
            try
            {
                var result = action();
                _db.Commit();
                return result;
            }
            catch
            {
                _db.Rollback();
                throw;
            }
        }
    }
}
