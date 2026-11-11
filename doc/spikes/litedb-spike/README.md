# Spike: replace SQLite with LiteDB? (TASK-0110)

**Recommendation: stay on SQLite.** Do not migrate, not even in part.

LiteDB's one real benefit is a fully managed engine without the native `e_sqlite3` sidecar. Even that benefit doesn't hold up in the measurements:

1. **Several processes cannot use it safely.** Harness's processes (a CLI sync, the MCP server, harness ui, git-hook syncs) open the same database at once.
   - Under Native AOT on Linux, LiteDB's shared mode has no lock between processes at all. It corrupted the database and lost 474 of 600 writes.
   - On Windows it stays correct but makes every write wait in line: about 130 ms per write instead of 2.4 ms.
   - It also blocks every reader behind a writer, with no timeout.
   - Direct mode is worse: on Windows it locks other processes out entirely, and on Linux it silently loses data.
2. **It is slower.** Shared mode is 2–12× slower than SQLite on harness's hot operations.
3. **It is not smaller.** A Native AOT executable with LiteDB is 0.9–1.5 MB *larger* than one with SQLite and its sidecar.
4. **It is not clean under AOT.** LiteDB's own query parser raises AOT warnings that harness's warnings-as-errors build would reject. Its reflection mapper fails at run time unless every type is preserved by hand.
5. **It lacks what harness relies on.** There is no full-text search. Index keys are capped at 1023 bytes, and finding keys reach 1.3 KB. ORDER BY fails on strings longer than about 250–400 characters. There are no joins, CHECK constraints or partial indexes.
6. **Its future is uncertain.** The last stable release, 5.0.21, is from July 2024. The 6.0 prerelease refuses to open a database unless the caller acknowledges that "broken databases and data loss are expected".

How it was measured, and the raw results, are in [measurements.md](measurements.md) and [results/](results/).

**Machines**
- Windows 11.
- WSL Ubuntu 26.10.

Both used .NET 10.0.403 and LiteDB 5.0.21 against Microsoft.Data.Sqlite 10.0.14. Builds were Release and Native AOT unless marked. macOS was not tested: no host was available.

## 1. How harness uses SQLite today

- **Three kinds of database**, each opened through `HarnessDatabase`:
  - the project's `.harness/harness.db`;
  - one knowledge database per workspace, `<global>/knowledge/workspace-<id>.db`;
  - the user's `<global>/knowledge/user.db`.

  The global registry is not SQLite: it is `projects.json` and `workspaces.json`, behind `.lock` files.
- **Connection model** ([HarnessDatabase.cs](../../../src/Harness.Infrastructure/Storage/HarnessDatabase.cs)):
  - pooled connections, one per operation, with `DefaultTimeout = -1`;
  - `PRAGMA busy_timeout` set per connection, 5 s by default;
  - WAL enabled on open, with a retry loop;
  - writes in immediate transactions (`WithTransaction`, 51 call sites);
  - reads deferred under `PRAGMA query_only` (`ReadTransaction`, 22 call sites);
  - `foreign_keys` on.
- **Migrations** ([MigrationRunner.cs](../../../src/Harness.Infrastructure/Storage/MigrationRunner.cs)):
  - 25 SQL files in [Storage/Migrations](../../../src/Harness.Infrastructure/Storage/Migrations), recorded in the `schema_migrations` ledger with checksums;
  - each applied exactly once, even when processes open a new database at the same moment;
  - retries while another process makes progress, and `SchemaTooNewException` for databases newer than the binary;
  - a backup (`VACUUM INTO`) before upgrading ([DatabaseBackups.cs](../../../src/Harness.Infrastructure/Storage/DatabaseBackups.cs)).

  The two storage warnings in harness memory record why the busy timeout and immediate transactions matter.
- **Schema:**
  - 30 tables (32 `STRICT` declarations), 23 indexes (2 unique, 3 partial);
  - 46 `CHECK` constraints and 17 foreign keys;
  - 14 `ALTER TABLE` statements and 3 table rebuilds;
  - one FTS5 table, `memories_fts`, with the `porter unicode61` tokenizer, kept in sync by 3 triggers.
- **Queries:** 64 source files open SQLite, with about 120 `SELECT`, 31 `INSERT`, 43 `UPDATE` and 26 `DELETE` statements. They use:
  - 9 `ON CONFLICT` upserts;
  - 10 joins;
  - 4 `GROUP BY`, 21 `count()` and 10 `max()`;
  - 8 `EXISTS`, 2 `UNION` and 9 `LIKE`;
  - one `json_each` in task filters;
  - FTS5 `MATCH` with `bm25()` ranking in `MemoryStore`.

  27 files catch `SqliteException`.
- **SQLite-specific features outside the stores:**
  - `PRAGMA data_version`, which is harness ui's cheap change probe in [UiChangeSource.cs](../../../src/Harness.Cli/Ui/UiChangeSource.cs);
  - per-table counts and last row ids in [DatabaseFingerprint.cs](../../../src/Harness.Infrastructure/Storage/DatabaseFingerprint.cs), which key the view cache;
  - 19 read-only opens of *other* projects' databases (cross-project tasks, workspace search, duplicates, the overview).
- **Tests:** 23 test files use SQLite or `HarnessDatabase` directly.

| Area | Files | Lines | Statements | Joins, aggregates, FTS |
|---|---:|---:|---:|---:|
| Tasks (`TaskStore*`, `ExternalTaskReader`) | 9 | 3192 | 88 | 21 |
| Memory (`MemoryStore*`, knowledge levels) | 3 | 1289 | 27 | 5 (FTS5) |
| Findings (`FindingStore*`) | 2 | 667 | 20 | 1 |
| Sync (`SyncStore`, `SyncFiles`) | 2 | 455 | 13 | 0 |
| Projects (`ProjectStore`, `HarnessStateSession*`) | 3 | 745 | 17 | 8 |
| Telemetry (`TelemetryStore*`) | 4 | 762 | 26 | 19 |
| Workspaces (`ProjectOverview`) | 1 | 188 | 7 | 6 |
| Storage (database, migrations, backups, fingerprint) | 3 | 617 | 13 | 7 |
| Cli (`UiReadService.Machine`, `ExportCommands`, `UiChangeSource`) | 3 | — | — | — |

## 2. API changes

LiteDB stores documents (`BsonDocument`) in collections. It indexes them with expressions, and queries them either with `BsonExpression` strings and parameters or with LINQ, which goes through `BsonMapper`. The `aot` command showed that only the first works under Native AOT; see section 3.

| SQLite feature harness uses | LiteDB equivalent | Gap |
|---|---|---|
| Typed rows, `STRICT`, `CHECK`, foreign keys | None. Documents are untyped. | Every constraint (46 checks, 17 foreign keys) becomes C# validation in the stores. |
| Unique and partial indexes (`memories_current_unique … WHERE superseded_by IS NULL`) | Unique expression indexes, but no partial indexes | Must be emulated, e.g. a unique index on an expression that folds in the superseded flag. |
| Composite primary keys, `ON CONFLICT … DO UPDATE` | `Upsert` on `_id`. A composite key becomes a concatenated string `_id`. | `_id` and every index key are limited to **1023 bytes**. Template finding keys reach 1292 bytes (3 of 367 in this repository, 12 longer than 400 characters), so long keys must be hashed into `_id`. |
| `ORDER BY` | `Query.OrderBy` | **Fails** on strings longer than about 250–400 characters (`NotImplementedException`, `ArgumentOutOfRangeException`), under JIT too. Finding lists would have to be sorted in memory. |
| Joins (10) | None. `DbRef`/`Include` only follow a reference. | Rewrite as several queries joined in memory, or denormalize. |
| `GROUP BY`, `count`, `max`, `EXISTS`, `UNION` | `SELECT … GROUP BY` in LiteDB's SQL, `Count`, `Max` | Mostly expressible. Telemetry's aggregates (19) need rework and verification. |
| Partial updates (`UPDATE … SET status='gone' WHERE …`) | `UpdateMany(transform, predicate)` | Works under AOT. |
| FTS5 with the porter stemmer and `bm25` | **None** | The spike stood in a multikey index over each memory's words. It finds fewer and differently ranked memories: "commit message format" gave 8 hits instead of 17, and the top 5 never matched in any of the 3 searches. Parity needs a hand-written stemmer, inverted index and ranking. |
| `json_each` | `ANY`/`ALL` over arrays | Doable. |
| Immediate write and deferred read transactions | `BeginTrans`/`Commit`, one writer per process, no read-only mode | The semantics differ across processes; see section 4. |
| `PRAGMA data_version`, row-id fingerprints | None | A change counter document written by every store. |
| `VACUUM INTO` (backups) | `Checkpoint` and a file copy under the engine's lock | Doable. |
| SQL migration files with a checksummed ledger | None | Migrations become C# document transforms. The ledger logic stays. |
| `DateTime` values | `BsonMapper` returns `DateTime` as **local** time (12:00Z came back as 13:00+01:00) | Map times explicitly, e.g. as UTC ticks, as `EngineMapping.cs` does. Harness stores times as ISO text today, so it isn't affected now. |

**Effort**, rough, for one developer who knows the stores:

| Area | Days |
|---|---:|
| Storage core: engine factory, the ledger as documents, migrations as code, backups, change counter, fingerprint | 6–8 |
| Tasks: 88 statements, joins, `json_each`, cross-project reads | 8–10 |
| Memory: FTS replacement with stemming and ranking, partial unique index, knowledge levels | 5–7 |
| Findings: hashed long keys, in-memory sort, snapshots, marks | 2–3 |
| Sync and Projects | 3–4 |
| Telemetry: aggregates | 3–4 |
| Workspaces, overview, Cli readers | 2–3 |
| Converting existing databases (section 5) | 5–7 |
| Tests: 23 files, plus new contention and parity tests | 6–8 |
| **Total** | **40–54** |

The total excludes fixing or working around the multi-process problem in section 4. There is no known fix inside LiteDB.

## 3. Native AOT and trimming

`aot` was published for win-x64 and linux-x64. The results are in `results/aot-*.txt`, `results/community-aot-*.txt` and `results/mutex-*.txt`.

| Path | Under Native AOT (both OSes) | Build warnings |
|---|---|---|
| Manual `BsonDocument` plus `Query`/`BsonExpression`, `UpdateMany`, `GROUP BY`, transactions | **Works** | 9 distinct IL3050 from LiteDB's own `BsonExpressionParser` and `LinqExpressionVisitor` (`Expression.NewArrayInit`, `Expression.Lambda`). They come from LiteDB itself, so no harness code can avoid them. |
| Typed collections through `BsonMapper` (`GetCollection<T>`, LINQ `Find(x => …)`) | **Fails**: "Type … does not have a default constructor", and `Insert` throws. The trimmer removed the type's constructor and properties. | 13 distinct trim warnings (IL2067/2070/2072/2075/2057), 4 more IL3050 |
| The same with `[DynamicDependency(All, typeof(T))]` on every mapped type | Works, but types must be listed by hand. A type left off the list fails only at run time. | Unchanged |
| Community.LiteDB.Aot's principles by hand (`EngineMapping.cs`: compile-time mapper, `ILiteEngine`, string predicates), on `LiteEngine` and `SharedEngine` | **Works**, including UTC times, lists and nulls | Only the parser's IL3050 |
| [Community.LiteDB.Aot](https://github.com/mrdevrobot/Community-LiteDb-AOT) 1.0.0-preview.1 with its source generator (`size/CommunityAot`) | **Works**: insert, `FindById`, string predicates and rollback. Its generator emits invalid code for types in the global namespace. It returns `DateTime` as local time. Its `LiteDbContext` always builds a direct `LiteEngine`, which is single-process only (section 4). | 8 IL3050 lines (5 distinct), all from the parser. Every `BsonMapper` warning is gone. |
| LiteDB 6.0.0-prerelease.355 | Refuses to open a database: "Broken databases and data loss are expected … DO NOT RUN IN PRODUCTION!" | — |

Harness builds with `IsAotCompatible` and `TreatWarningsAsErrors` ([Directory.Build.props](../../../Directory.Build.props)). Even the cleanest path keeps the parser's IL3050, which becomes one IL3053 per assembly in a normal build. Accepting LiteDB therefore means suppressing an AOT warning for the whole build, and with it the protection the setting gives harness's own code.

Community.LiteDB.Aot's approach is the right one if LiteDB is ever used: a generated mapper, the engine API and string predicates. But it removes only the mapper's reflection. It does not change the parser, the engine's locking, or the gaps in section 2.

**Size** (`size/*`). Each program opens a database, inserts one record and reads it back, published as trimmed Native AOT:

| | win-x64 | linux-x64 |
|---|---:|---:|
| SQLite: executable + `e_sqlite3` sidecar | 2.56 + 1.98 = **4.54 MB** | 2.80 + 1.47 = **4.27 MB** |
| LiteDB through `LiteDatabase` | **5.79 MB** | **5.79 MB** |
| LiteDB through `ILiteEngine` only | 5.43 MB | 5.47 MB |
| Community.LiteDB.Aot | 5.44 MB | 5.48 MB |

LiteDB is larger even without the sidecar. Its engine, its expression parser and the `System.Linq.Expressions` interpreter that Native AOT falls back to outweigh SQLite's native library. During a migration, both would ship (section 5).

## 4. Several processes on one database

Harness opens one database from several processes at once:
- a CLI `harness sync`;
- one MCP server per agent session;
- harness ui;
- git-hook syncs;
- the metrics worker.

`StorageTests.CompetingProcessesMigrateExactlyOnceAndKeepAllWrites` and the busy-timeout tests pin the guarantees: a new database is migrated exactly once, and no write is lost. On top of that, readers never wait for writers (WAL), and a writer that cannot get the lock fails after a bounded busy timeout.

LiteDB offers two modes:
- **direct:** one process holds the file;
- **shared:** every operation takes a named `Mutex`, opens the file, works and closes it.

`contend <engine> 6 100` runs 6 processes, each applying 5 migrations and then making 100 read-modify-write transactions. `hold` runs a reader and a writer while another process holds a write transaction.

| | Windows | Linux, Native AOT | Linux, JIT |
|---|---|---|---|
| SQLite (harness's settings) | **PASS**: 2.5 s, write p50 2.4 ms | **PASS**: 0.05 s | (same engine) |
| LiteDB shared | **PASS** but slow: 14.3 s, write p50 129 ms. The processes take turns. | **FAIL**: "request page must be less or equals lastest page", "ReadFull must read PAGE_SIZE bytes". 126/600 rows, counter 138/600, 998/1000 migration rows (a migration half-applied). | PASS: 1.5 s, write p50 7 ms |
| LiteDB direct | **FAIL**: the other 5 processes get `IOException`, file in use | **FAIL, silently**: every process "succeeds", the migrations run several times, and the file ends with 0/600 rows | — |

| While another process holds a write transaction | SQLite | LiteDB shared |
|---|---|---|
| A read | Answers at once (WAL) | **Waits for the whole transaction**: 8 s for an 8 s hold, 75 s for a 75 s hold. There is no timeout, even past LiteDB's 1-minute `Timeout`. |
| A write | Waits, then fails after the 5 s busy timeout ("database is locked") | Waits as long as the holder holds |
| On Linux under Native AOT | Same as above | Neither waits: the write lands inside the other process's transaction |

**Cause on Linux** (`mutex`): under Native AOT a named `Mutex` does not cross processes. A second process takes it immediately while the first still holds it. It does cross processes under JIT, and on Windows under AOT. LiteDB's shared mode depends on exactly that mutex, and direct mode opens the file without an OS lock on Unix. So with harness's release binaries on Linux, LiteDB has **no inter-process locking at all**. A newer LiteDB cannot fix this: the limitation is in the .NET runtime's Native AOT on Unix. Fixing it would need LiteDB to change its locking, for example to file locks.

**macOS** was not tested. It runs the same Native AOT Unix runtime, so the same failure should be assumed.

Even where shared mode is correct (Windows), it fails two of harness's expectations:
- One process's sync, which can hold a write transaction for seconds, would freeze every MCP call and every harness ui page, reads included, for as long as it runs.
- A hung process would freeze them indefinitely, where SQLite fails cleanly after 5 s.

## 5. Converting existing databases (if it were done)

The design below is what a migration would need. It is part of the effort estimate and argues against migrating.

- **What converts:**
  - each project's `.harness/harness.db`;
  - each workspace's `knowledge/workspace-<id>.db`;
  - `knowledge/user.db`.

  `projects.json` and `workspaces.json` are not SQLite and stay. Existing backups (`.harness/backups/schema-*.db`, `knowledge/backups/`) stay SQLite and stay readable only by SQLite.
- **One-off conversion, not dual-read.** Reading both formats while writing one leaves two sources of truth that drift apart, and every store would need both code paths. The conversion runs at the first open by a new harness:
  1. Take a cross-process **file lock** beside the database, as `projects.json.lock` already does. Do not use a named mutex: it does not cross processes under Native AOT on Linux (section 4). Other processes wait on the lock with a timeout, then open the result.
  2. Back up the SQLite file with `VACUUM INTO`, as schema upgrades already do.
  3. Read every table through Microsoft.Data.Sqlite and write `harness.litedb.tmp` in one transaction. Record the source schema version and source hash in a `schema` document, and checkpoint.
  4. Flush and atomically rename `.tmp` to `harness.litedb`.
  5. Replace `harness.db` with a **stub** whose `schema_migrations` records a version above any SQLite schema.
- **Interrupted conversion:** a leftover `.tmp` is deleted and the conversion starts again. The SQLite file is untouched until the rename, and the rename is atomic.
- **Concurrent first open:** the file lock serializes the processes. Only the first converts; the others find the finished file.
- **Downgrade:** an older harness opening the stub hits its existing `SchemaTooNewException` ("upgrade harness") instead of silently creating an empty database. Going back needs an explicit `harness migrate --to-sqlite` export, or a restore from the backup taken in step 2. Writes made since then would be lost.
- **Cross-project reads** (19 read-only opens: external task dependencies, workspace search, duplicates, overview) may meet another project's database that no new harness has opened yet. Read-only callers cannot convert it. During the transition these readers need both formats, or must convert under the other project's lock.
- **Microsoft.Data.Sqlite must keep shipping**, with its native sidecar, for at least as long as unconverted databases and SQLite backups can exist, so in practice indefinitely. That removes LiteDB's one advantage: harness would ship both engines.

## 6. Speed (`bench`, 15 runs)

Medians in ms. The engines alternate order on each run, and the first run is discarded.

| Operation | Win SQLite | Win LiteDB shared | Win LiteDB direct | Linux SQLite | Linux LiteDB shared | Linux LiteDB direct |
|---|---:|---:|---:|---:|---:|---:|
| Open and first read (new process) | 13.31 | 0.67 | 25.72 | 1.23 | 0.18 | 0.74 |
| File hashes, read all 602 | 0.51 | 1.59 | 1.00 | 0.35 | 0.96 | 0.98 |
| File hashes, write 50 (incremental sync) | 9.07 | 19.09 | 1.21 | 0.25 | 1.07 | 0.66 |
| File hashes, write all 602 (full sync) | 6.35 | 51.27 | 15.90 | 1.67 | 6.49 | 6.25 |
| Memory search ×3 | 1.49 | 13.77 | 1.63 | 0.56 | 3.33 | 1.33 |
| Findings take (snapshot of 367) | 17.81 | 43.62 | 5.96 | 3.67 | 6.64 | 5.81 |
| Findings read (current) | 1.02 | 11.79 | 1.49 | 0.64 | 1.31 | 1.36 |

- **Open** is cheap in shared mode only because shared mode defers opening the file to each operation, which is where its cost reappears.
- **Shared mode, the only multi-process option, is slower on every operation harness repeats**: 2–12× on Windows and 2–6× on Linux.
- **Direct mode is faster on some writes**, because it doesn't wait for SQLite's WAL fsync. But it can't be used by more than one process.
- **The SQLite figures are for one connection with no contention.** Under contention, the gap widens to 50× per write on Windows (section 4).

## 7. Benefits, risks, recommendation

**Benefits.** These are real but small, or they don't materialize:
- No native sidecar per RID. This doesn't materialize: SQLite must keep shipping to convert and read existing databases, and LiteDB is larger anyway.
- A document model suits the JSON already stored in columns, such as `findings.finding`.
- A managed engine with no native toolchain dependency for the storage code.

**Risks:**
- Data loss and corruption under concurrent use with harness's Linux release binaries.
- No read concurrency and no lock timeout, even where shared mode works.
- No full-text search, so `memory_search` gets worse.
- Key and ORDER BY length limits that harness's own data already exceeds.
- AOT warnings the build would have to suppress.
- A project with no stable release since July 2024, and a 6.0 that disclaims production use.
- 40–54 days of work and a one-way migration of users' data.

**Recommendation: stay on SQLite.** No follow-up migration tasks are proposed.

The findings worth keeping for SQLite's sake:
- The current guarantees (WAL readers, bounded busy timeout, migrations applied exactly once under contention) are what any replacement would have to match. `contend` and `hold` are a ready test for one.
- Under Native AOT on Unix, a named `Mutex` is process-local. Harness must keep using file locks, not named mutexes, for anything that has to exclude other processes.
