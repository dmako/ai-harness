using System.Diagnostics;
using System.Globalization;
using System.Text;
using LiteDB;
using Microsoft.Data.Sqlite;

namespace LiteDbSpike;

/// <summary>
/// Harness's hot storage operations on the same data, three ways: SQLite as HarnessDatabase uses it (pooled
/// connection per operation, WAL, busy_timeout, immediate write transactions, the stores' SQL), LiteDB shared (the
/// only mode several processes can open) and LiteDB direct (one process only; the engine's best case). The data is
/// a copy of a real harness.db: SQLite gets it by VACUUM INTO, LiteDB by import. Engines alternate order per run.
/// </summary>
internal static class Bench
{
    private static readonly string[] Searches =
    [
        "sqlite busy timeout",
        "commit message format",
        "native aot trimming release",
    ];

    public static int Run(string source, int runs)
    {
        var directory = Path.Combine(Path.GetTempPath(), "litedb-spike-bench-" + Environment.ProcessId);
        Directory.CreateDirectory(directory);
        try
        {
            var sqlitePath = Path.Combine(directory, "bench.sqlite");
            using (var from = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False"))
            {
                from.Open();
                using var vacuum = from.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $path";
                vacuum.Parameters.AddWithValue("$path", sqlitePath);
                vacuum.ExecuteNonQuery();
            }

            var sqlite = new SqliteEngine(sqlitePath);
            var timer = Stopwatch.StartNew();
            var sharedPath = Path.Combine(directory, "shared.litedb");
            var directPath = Path.Combine(directory, "direct.litedb");
            LiteEngine.Import(sqlite, sharedPath);
            Console.WriteLine($"import into LiteDB: {timer.Elapsed.TotalSeconds:F2} s");
            File.Copy(sharedPath, directPath);
            using var shared = new LiteEngine(sharedPath, ConnectionType.Shared);
            using var direct = new LiteEngine(directPath, ConnectionType.Direct);
            IEngine[] engines = [sqlite, shared, direct];
            var worktree = sqlite.BusiestWorktree();
            var paths = sqlite.Paths(worktree);
            Console.WriteLine(
                $"data: {paths.Count} files in worktree {worktree}, {sqlite.Scalar("SELECT count(*) FROM memories")} memories, "
                    + $"{sqlite.Scalar("SELECT count(*) FROM findings")} findings"
            );

            // Same answers first: what LiteDB would return for harness's own queries.
            foreach (var text in Searches)
            {
                var a = sqlite.Search(text);
                var b = shared.Search(text);
                var common = a.Intersect(b).Count();
                Console.WriteLine(
                    $"search \"{text}\": sqlite {a.Count} hits, litedb {b.Count} hits, {common} in both; "
                        + $"top 5 equal: {a.Take(5).SequenceEqual(b.Take(5))}"
                );
            }

            var operations = new (string Name, Action<IEngine, int> Run)[]
            {
                ("open + first read", (e, _) => e.Open()),
                ("file hashes read (all)", (e, _) => e.FileHashes(worktree)),
                ("file hashes write 50", (e, r) => e.WriteHashes(worktree, paths.Take(50), r)),
                ($"file hashes write all {paths.Count}", (e, r) => e.WriteHashes(worktree, paths, r)),
                (
                    "memory search x3",
                    (e, _) =>
                    {
                        foreach (var text in Searches)
                        {
                            e.Search(text);
                        }
                    }
                ),
                ("findings take (snapshot)", (e, r) => e.TakeFindings(r)),
                ("findings read current", (e, _) => e.CurrentFindings()),
            };
            var samples = engines.ToDictionary(
                e => e.Name,
                _ => operations.ToDictionary(o => o.Name, _ => new List<double>())
            );
            for (var run = 0; run < runs + 1; run++)
            {
                var order = run % 2 is 0 ? engines : engines.Reverse().ToArray();
                foreach (var engine in order)
                {
                    foreach (var (name, operation) in operations)
                    {
                        var watch = Stopwatch.StartNew();
                        operation(engine, run);
                        if (run > 0)
                        {
                            samples[engine.Name][name].Add(watch.Elapsed.TotalMilliseconds);
                        }
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"median ms over {runs} runs (first run discarded as warm-up)");
            Console.WriteLine($"{"operation", -30}" + string.Concat(engines.Select(e => $"{e.Name, 16}")));
            foreach (var (name, _) in operations)
            {
                Console.WriteLine(
                    $"{name, -30}" + string.Concat(engines.Select(e => $"{Median(samples[e.Name][name]), 16:F2}"))
                );
            }

            Console.WriteLine();
            foreach (var engine in engines)
            {
                engine.Dispose();
            }

            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(directory).Order(StringComparer.Ordinal))
            {
                Console.WriteLine($"{Path.GetFileName(file), -24} {new FileInfo(file).Length / 1024.0, 10:F0} KiB");
            }

            return 0;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException) { }
        }
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values.Count is 0 ? double.NaN : values[values.Count / 2];
    }

    /// <summary>harness's MemoryText.FullTextQuery tokens, lower-cased (FTS5 unicode61 folds case).</summary>
    internal static IReadOnlyList<string> Words(string text)
    {
        List<string> words = [];
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '_' or >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or > 127)
            {
                word.Append(rune.ToString().ToLowerInvariant());
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    private interface IEngine : IDisposable
    {
        string Name { get; }
        void Open();
        IReadOnlyDictionary<string, string> FileHashes(string worktree);
        void WriteHashes(string worktree, IEnumerable<string> paths, int run);
        List<string> Search(string text);
        void TakeFindings(int run);
        int CurrentFindings();
    }

    private sealed class SqliteEngine(string path) : IEngine
    {
        private readonly string _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();

        public string Name => "sqlite";

        public SqliteConnection Connection()
        {
            var connection = new SqliteConnection(_connectionString) { DefaultTimeout = -1 };
            connection.Open();
            Execute(connection, null, "PRAGMA busy_timeout = 5000");
            return connection;
        }

        public void Open()
        {
            // A new process: no pooled connection yet.
            SqliteConnection.ClearAllPools();
            using var connection = Connection();
            Execute(connection, null, "PRAGMA journal_mode = WAL");
            Execute(connection, null, "SELECT max(version) FROM schema_migrations");
        }

        public long Scalar(string sql)
        {
            using var connection = Connection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public string BusiestWorktree()
        {
            using var connection = Connection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT worktree_id FROM files GROUP BY worktree_id ORDER BY count(*) DESC LIMIT 1";
            return (string)command.ExecuteScalar()!;
        }

        public List<string> Paths(string worktree) => [.. FileHashes(worktree).Keys];

        public IReadOnlyDictionary<string, string> FileHashes(string worktree)
        {
            using var connection = Connection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path,hash FROM files WHERE worktree_id=$wt ORDER BY path";
            command.Parameters.AddWithValue("$wt", worktree);
            using var reader = command.ExecuteReader();
            Dictionary<string, string> hashes = new(StringComparer.Ordinal);
            while (reader.Read())
            {
                hashes.Add(reader.GetString(0), reader.GetString(1));
            }

            return hashes;
        }

        public void WriteHashes(string worktree, IEnumerable<string> paths, int run)
        {
            using var connection = Connection();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO files(worktree_id,path,hash,size,mtime_ns,updated_at) VALUES($wt,$path,$hash,$size,$mtime,$at)
                ON CONFLICT(worktree_id,path) DO UPDATE SET hash=excluded.hash,size=excluded.size,mtime_ns=excluded.mtime_ns,updated_at=excluded.updated_at
                """;
            var wt = command.Parameters.Add("$wt", SqliteType.Text);
            var p = command.Parameters.Add("$path", SqliteType.Text);
            var hash = command.Parameters.Add("$hash", SqliteType.Text);
            var size = command.Parameters.Add("$size", SqliteType.Integer);
            var mtime = command.Parameters.Add("$mtime", SqliteType.Integer);
            var at = command.Parameters.Add("$at", SqliteType.Text);
            foreach (var path in paths)
            {
                wt.Value = worktree;
                p.Value = path;
                hash.Value = HashOf(path, run);
                size.Value = run;
                mtime.Value = run;
                at.Value = Now();
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public List<string> Search(string text)
        {
            var terms = Words(text);
            using var connection = Connection();
            using var command = connection.CreateCommand();
            // MemoryStore.Search with a text and no other filter.
            command.CommandText = """
                SELECT m.id FROM memories_fts f JOIN memories m ON m.rowid=f.rowid
                WHERE memories_fts MATCH $fts AND m.superseded_by IS NULL
                  AND (m.valid_from IS NULL OR m.valid_from <= $now) AND (m.valid_until IS NULL OR m.valid_until > $now)
                ORDER BY bm25(memories_fts) LIMIT 20
                """;
            command.Parameters.AddWithValue("$fts", string.Join(" OR ", terms.Select(t => "\"" + t + "\"")));
            command.Parameters.AddWithValue("$now", Now());
            using var reader = command.ExecuteReader();
            List<string> ids = [];
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }

            return ids;
        }

        public void TakeFindings(int run)
        {
            // FindingStore.Take for a changed snapshot: every finding upserted with its status, then marked.
            using var connection = Connection();
            using var transaction = connection.BeginTransaction(deferred: false);
            var current =
                new List<(
                    string Key,
                    string Kind,
                    string Level,
                    string Title,
                    long Measure,
                    string Finding,
                    string Hash,
                    string First
                )>();
            using (
                var read = Command(
                    connection,
                    transaction,
                    "SELECT key,kind,level,title,measure,finding,hash,first_seen FROM findings"
                )
            )
            using (var reader = read.ExecuteReader())
            {
                while (reader.Read())
                {
                    current.Add(
                        (
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetInt64(4),
                            reader.GetString(5),
                            reader.GetString(6),
                            reader.GetString(7)
                        )
                    );
                }
            }

            using (
                var insert = Command(
                    connection,
                    transaction,
                    "INSERT INTO finding_snapshots(commit_sha,taken_at,found,added,changed,gone) VALUES($c,$now,$f,0,$f,0)"
                )
            )
            {
                insert.Parameters.AddWithValue("$c", "run" + run);
                insert.Parameters.AddWithValue("$now", Now());
                insert.Parameters.AddWithValue("$f", current.Count);
                insert.ExecuteNonQuery();
            }

            long id;
            using (var identity = Command(connection, transaction, "SELECT last_insert_rowid()"))
            {
                id = (long)identity.ExecuteScalar()!;
            }

            using var upsert = Command(
                connection,
                transaction,
                """
                INSERT INTO findings(key,kind,level,title,measure,finding,hash,status,snapshot,first_seen,last_seen)
                VALUES($key,$kind,$level,$title,$measure,$finding,$hash,'changed',$snapshot,$first,$now)
                ON CONFLICT(key) DO UPDATE SET kind=excluded.kind,level=excluded.level,title=excluded.title,measure=excluded.measure,
                    finding=excluded.finding,hash=excluded.hash,status=excluded.status,snapshot=excluded.snapshot,last_seen=excluded.last_seen
                """
            );
            foreach (var f in current)
            {
                upsert.Parameters.Clear();
                upsert.Parameters.AddWithValue("$key", f.Key);
                upsert.Parameters.AddWithValue("$kind", f.Kind);
                upsert.Parameters.AddWithValue("$level", f.Level);
                upsert.Parameters.AddWithValue("$title", f.Title);
                upsert.Parameters.AddWithValue("$measure", f.Measure);
                upsert.Parameters.AddWithValue("$finding", f.Finding);
                upsert.Parameters.AddWithValue("$hash", HashOf(f.Key, run));
                upsert.Parameters.AddWithValue("$snapshot", id);
                upsert.Parameters.AddWithValue("$first", f.First);
                upsert.Parameters.AddWithValue("$now", Now());
                upsert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public int CurrentFindings()
        {
            using var connection = Connection();
            using var transaction = connection.BeginTransaction(deferred: true);
            var n = 0;
            using (
                var read = Command(
                    connection,
                    transaction,
                    "SELECT key,kind,level,title,measure,finding,hash,status,snapshot,first_seen,last_seen FROM findings WHERE status<>'gone' ORDER BY key"
                )
            )
            using (var reader = read.ExecuteReader())
            {
                while (reader.Read())
                {
                    _ = reader.GetString(5);
                    n++;
                }
            }

            using var latest = Command(
                connection,
                transaction,
                "SELECT id,commit_sha,taken_at,found,added,changed,gone FROM finding_snapshots ORDER BY id DESC LIMIT 1"
            );
            using var row = latest.ExecuteReader();
            return row.Read() ? n : -1;
        }

        public void Dispose() => SqliteConnection.ClearAllPools();

        private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return command;
        }

        private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            using var command = Command(connection, transaction, sql);
            command.ExecuteNonQuery();
        }
    }

    private sealed class LiteEngine(string path, ConnectionType connection) : IEngine
    {
        private readonly ConnectionString _connection = new() { Filename = path, Connection = connection };
        private LiteDatabase _db = new(new ConnectionString { Filename = path, Connection = connection });

        public string Name => connection is ConnectionType.Shared ? "litedb shared" : "litedb direct";

        /// <summary>Manual BsonDocument mapping, the only one that is safe under Native AOT (see AotCheck).</summary>
        public static void Import(SqliteEngine sqlite, string path)
        {
            using var db = new LiteDatabase(
                new ConnectionString { Filename = path, Connection = ConnectionType.Direct }
            );
            using var connection = sqlite.Connection();
            db.BeginTrans();
            db.GetCollection("schema").Insert(new BsonDocument { ["_id"] = "version", ["value"] = 25 });
            var files = db.GetCollection("files");
            foreach (var row in Rows(connection, "SELECT worktree_id,path,hash,size,mtime_ns,updated_at FROM files"))
            {
                files.Insert(
                    new BsonDocument
                    {
                        ["_id"] = (string)row[0] + "\n" + (string)row[1],
                        ["wt"] = (string)row[0],
                        ["path"] = (string)row[1],
                        ["hash"] = (string)row[2],
                        ["size"] = (long)row[3],
                        ["mtime_ns"] = (long)row[4],
                        ["updated_at"] = (string)row[5],
                    }
                );
            }

            files.EnsureIndex("wt");
            var memories = db.GetCollection("memories");
            foreach (
                var row in Rows(
                    connection,
                    "SELECT id,type,content,scope_kind,scope_value,superseded_by,valid_from,valid_until,updated_at FROM memories"
                )
            )
            {
                var content = (string)row[2];
                memories.Insert(
                    new BsonDocument
                    {
                        ["_id"] = (string)row[0],
                        ["type"] = (string)row[1],
                        ["content"] = content,
                        // No full-text search in LiteDB: a multikey index over the content's distinct words stands in.
                        ["words"] = new BsonArray(Words(content).Distinct().Select(w => new BsonValue(w))),
                        ["scope_kind"] = (string)row[3],
                        ["scope_value"] = (string)row[4],
                        ["superseded_by"] = row[5] is string s ? s : BsonValue.Null,
                        ["valid_from"] = row[6] is string f ? f : BsonValue.Null,
                        ["valid_until"] = row[7] is string u ? u : BsonValue.Null,
                        ["updated_at"] = (string)row[8],
                    }
                );
            }

            memories.EnsureIndex("words", "$.words[*]");
            memories.EnsureIndex("type");
            var findings = db.GetCollection("findings");
            foreach (
                var row in Rows(
                    connection,
                    "SELECT key,kind,level,title,measure,finding,hash,status,snapshot,first_seen,last_seen FROM findings"
                )
            )
            {
                findings.Insert(
                    new BsonDocument
                    {
                        // Index keys (and so _id) are limited to 1023 bytes; template finding keys reach 1.3 KB.
                        ["_id"] = KeyId((string)row[0]),
                        ["key"] = (string)row[0],
                        ["kind"] = (string)row[1],
                        ["level"] = (string)row[2],
                        ["title"] = (string)row[3],
                        ["measure"] = (long)row[4],
                        ["finding"] = (string)row[5],
                        ["hash"] = (string)row[6],
                        ["status"] = (string)row[7],
                        ["snapshot"] = (long)row[8],
                        ["first_seen"] = (string)row[9],
                        ["last_seen"] = (string)row[10],
                    }
                );
            }

            findings.EnsureIndex("status");
            var snapshots = db.GetCollection("finding_snapshots", BsonAutoId.Int64);
            foreach (
                var row in Rows(
                    connection,
                    "SELECT id,commit_sha,taken_at,found,added,changed,gone FROM finding_snapshots"
                )
            )
            {
                snapshots.Insert(
                    new BsonDocument
                    {
                        ["_id"] = (long)row[0],
                        ["commit"] = (string)row[1],
                        ["taken_at"] = (string)row[2],
                        ["found"] = (long)row[3],
                        ["added"] = (long)row[4],
                        ["changed"] = (long)row[5],
                        ["gone"] = (long)row[6],
                    }
                );
            }

            db.Commit();
            db.Checkpoint();
        }

        public void Open()
        {
            // A new process: a new LiteDatabase reading the schema version.
            _db.Dispose();
            _db = new LiteDatabase(_connection);
            _ = _db.GetCollection("schema").FindById("version");
        }

        public IReadOnlyDictionary<string, string> FileHashes(string worktree)
        {
            Dictionary<string, string> hashes = new(StringComparer.Ordinal);
            foreach (var doc in _db.GetCollection("files").Find(Query.EQ("wt", worktree)))
            {
                hashes.Add(doc["path"].AsString, doc["hash"].AsString);
            }

            return hashes;
        }

        public void WriteHashes(string worktree, IEnumerable<string> paths, int run)
        {
            var files = _db.GetCollection("files");
            _db.BeginTrans();
            foreach (var path in paths)
            {
                files.Upsert(
                    new BsonDocument
                    {
                        ["_id"] = worktree + "\n" + path,
                        ["wt"] = worktree,
                        ["path"] = path,
                        ["hash"] = HashOf(path, run),
                        ["size"] = (long)run,
                        ["mtime_ns"] = (long)run,
                        ["updated_at"] = Now(),
                    }
                );
            }

            _db.Commit();
        }

        public List<string> Search(string text)
        {
            // OR of the words, ranked by how many matched (no stemming, no bm25), newest first on ties.
            var memories = _db.GetCollection("memories");
            var now = Now();
            var hits = new Dictionary<string, (int Count, string Updated)>(StringComparer.Ordinal);
            foreach (var word in Words(text).Distinct())
            {
                foreach (var doc in memories.Find(BsonExpression.Create("$.words[*] ANY = @0", word)))
                {
                    if (
                        !doc["superseded_by"].IsNull
                        || doc["valid_from"] is { IsString: true } from && string.CompareOrdinal(from.AsString, now) > 0
                        || doc["valid_until"] is { IsString: true } until
                            && string.CompareOrdinal(until.AsString, now) <= 0
                    )
                    {
                        continue;
                    }

                    var id = doc["_id"].AsString;
                    hits[id] = (hits.GetValueOrDefault(id).Count + 1, doc["updated_at"].AsString);
                }
            }

            return
            [
                .. hits.OrderByDescending(h => h.Value.Count)
                    .ThenByDescending(h => h.Value.Updated, StringComparer.Ordinal)
                    .Take(20)
                    .Select(h => h.Key),
            ];
        }

        public void TakeFindings(int run)
        {
            var findings = _db.GetCollection("findings");
            var snapshots = _db.GetCollection("finding_snapshots", BsonAutoId.Int64);
            _db.BeginTrans();
            var current = findings.FindAll().ToList();
            var id = snapshots.Insert(
                new BsonDocument
                {
                    ["commit"] = "run" + run,
                    ["taken_at"] = Now(),
                    ["found"] = current.Count,
                    ["added"] = 0,
                    ["changed"] = current.Count,
                    ["gone"] = 0,
                }
            );
            foreach (var doc in current)
            {
                doc["hash"] = HashOf(doc["key"].AsString, run);
                doc["status"] = "changed";
                doc["snapshot"] = id;
                doc["last_seen"] = Now();
                findings.Update(doc);
            }

            _db.Commit();
        }

        public int CurrentFindings()
        {
            // Sorted here: LiteDB's ORDER BY fails on strings over ~1 KB (AotCheck "ORDER BY a long string").
            var n = 0;
            foreach (
                var doc in _db.GetCollection("findings")
                    .Find("$.status != 'gone'")
                    .OrderBy(d => d["key"].AsString, StringComparer.Ordinal)
            )
            {
                _ = doc["finding"].AsString;
                n++;
            }

            var latest = _db.GetCollection("finding_snapshots")
                .Query()
                .OrderByDescending("_id")
                .Limit(1)
                .FirstOrDefault();
            return latest is null ? -1 : n;
        }

        public void Dispose() => _db.Dispose();

        private static IEnumerable<object[]> Rows(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new object[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                }

                yield return row;
            }
        }
    }

    private static string KeyId(string key) =>
        Encoding.UTF8.GetByteCount(key) <= 1000
            ? key
            : "sha256:"
                + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string HashOf(string text, int run) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text + run)));

    private static string Now() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'00Z'", CultureInfo.InvariantCulture);
}
