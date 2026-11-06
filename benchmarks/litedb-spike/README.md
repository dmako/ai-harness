# Spike: replace SQLite with LiteDB? (TASK-0110)

**Recommendation: no.** Stay on SQLite. The findings and the reasoning are in [doc/architecture/litedb-spike.md](../../doc/architecture/litedb-spike.md). This folder holds the code and the raw results.

`LiteDbSpike` is a standalone project. It is not in `harness.slnx`, and the empty `Directory.*` files keep the repository's build rules, analyzers and central package versions out. It is published as trimmed Native AOT, like harness. It references LiteDB 5.0.21, the latest stable release, and Microsoft.Data.Sqlite 10.0.14, the version harness pins.

## Commands

| Command | What it does |
|---|---|
| `aot` | Tries each way harness could talk to LiteDB: manual `BsonDocument` mapping, `BsonExpression` predicates, partial updates, aggregates, typed collections through `BsonMapper` (LINQ predicates included), ORDER BY on long strings, transactions, and the engine mapper below on both the direct and the shared engine. Prints PASS or FAIL per step. |
| `bench <harness.db> <runs>` | Copies a real `harness.db` (`VACUUM INTO` for SQLite, an import for LiteDB) and times harness's hot operations three ways: SQLite as `HarnessDatabase` uses it, LiteDB shared and LiteDB direct. The engines alternate order on each run, and the first run is discarded. |
| `contend <sqlite\|shared\|direct> <processes> <writes>` | Several processes open one new database at once. Each applies the same 5 migrations, which must happen once each, then makes `writes` transactions with a read-modify-write counter. A lost update shows as a short count. Mirrors `StorageTests.CompetingProcessesMigrateExactlyOnceAndKeepAllWrites`. |
| `hold <sqlite\|shared> [seconds]` | Another process holds a write transaction while this one reads and then writes. Shows what waits, for how long, and whether it times out. |
| `mutex` | Holds a named `Mutex`, the lock LiteDB's shared mode relies on, and asks a second process to take it. |

`size/` holds four minimal programs that open a database, insert one record and read it back. Publishing them shows what each engine adds to a Native AOT executable:

- `SqliteOnly`: Microsoft.Data.Sqlite.
- `LiteDbOnly`: LiteDB through `LiteDatabase`.
- `LiteDbEngineOnly`: LiteDB through `ILiteEngine` only.
- `CommunityAot`: the `Community.LiteDB.Aot` package with its source generator.

`EngineMapping.cs` applies the principles of [Community.LiteDB.Aot](https://github.com/mrdevrobot/Community-LiteDb-AOT) by hand:

- A mapper per type converts to and from `BsonDocument`. This is what that project's source generator emits.
- A typed collection works over `ILiteEngine` directly, so `LiteDatabase` and `BsonMapper` are never used.
- Predicates are strings with `BsonValue` parameters, not LINQ lambdas.

Unlike that package's `LiteDbContext`, which always builds a direct `LiteEngine`, it also runs on `SharedEngine`.

```
dotnet publish -c Release -r win-x64 -o <out>          # Windows: needs vswhere on PATH (VS Installer folder)
dotnet publish -c Release -r linux-x64 -o <out>        # Linux: clang and zlib1g-dev, as for harness
<out>/LiteDbSpike aot
<out>/LiteDbSpike bench <copy of .harness/harness.db> 15
<out>/LiteDbSpike contend shared 6 100
<out>/LiteDbSpike hold shared 75
<out>/LiteDbSpike mutex
dotnet publish -c Release -r win-x64 -p:DefineConstants=PRESERVE -o <out>             # BsonMapper types kept by [DynamicDependency]
dotnet publish -c Release -r win-x64 -p:LiteDbVersion=6.0.0-prerelease.355 -o <out>   # the LiteDB 6 prerelease
```

**Machines**
- Windows 11 (10.0.26300).
- WSL Ubuntu 26.10, with the files on the Linux file system.

Both used .NET SDK 10.0.403. The data is ai-harness-cs's own `harness.db`, schema 25:
- 602 file hashes in the busiest worktree;
- 241 memories;
- 367 findings.

The raw output of every run is in [`results/`](results/).
