# SQLite storage implementation and validation

TASK-0004 implements the database boundary in `Harness.Infrastructure.Storage`.
The migration scripts and embedded MIT notice come from Go commit
`9cb401dd2de4f85dd089fea86a0b139d81b178c6`. Tests hash all 19 scripts against the
frozen inventory, copy synthetic historical fixtures, and never open a user's
`.harness/harness.db`.

From `test/Harness.Integration`, with .NET SDK 10.0.402 installed:

```powershell
dotnet restore Harness.Integration.csproj -p:RuntimeIdentifiers=win-x64 -bl:{{}}
dotnet test --project Harness.Integration.csproj --no-restore -p:RuntimeIdentifiers=win-x64 -bl:{{}} --report-trx
```

Select the relevant RID on other hosts. Every invocation must get a fresh binlog.
The root `global.json` selects the stable SDK and native MTP runner;
TASK-0005 provides the complete [solution bootstrap](bootstrap.md). The infrastructure, integration and
process-worker projects are necessary to run these storage tests; other product
assemblies and CLI composition remain bootstrap work.

For the Go round trip, run `Storage/Go/build-oracle.ps1` from this directory (or
its repository-relative path). It archives the exact pinned source into a fresh
ignored `artifacts/storage-oracle/source`, injects only the test bridge command,
and builds against the original storage package. Pass `-Go` to select the Go
compiler when needed. Source checkout contents are not copied and the source
repository is not modified. Set `HARNESS_GO_STORAGE_ORACLE` to the resulting
executable before running tests. Go 1.27.1 was used for validation; Go modules
and caches are isolated under `artifacts/storage-oracle`.

The suite covers new/current/historical schemas, ledger and file-name validation,
STRICT types, task-table rebuilds with foreign keys temporarily disabled,
transaction and failed-migration rollback, consistent backups and manual restore,
five-backup retention, read-only inspection, four competing processes, a held
writer with nonblocking readers/current-schema opens, one-second and 100 ms lock
timeouts, and Unicode data persisted across Go/.NET/Go processes. One Unix mode
test is explicitly skipped on Windows; the Go interoperability test is explicitly
skipped if its helper is not configured. Validation for this task configured the
helper and ran the Go test successfully.

`HarnessDatabase` is a pooled connection factory. Each operation owns a distinct
connection; callers must dispose connections, commands, readers and transactions.
Write transactions begin immediately. Read transactions use a deferred snapshot
with `query_only` enforced and restored before returning the connection to its
pool. Keep a database factory alive across repeated operations; dispose it to
clear its pool. Never share a connection concurrently between requests.

The native `PRAGMA busy_timeout` is configured in milliseconds on every opened
connection, with the Go default of five seconds for nonpositive options. The
pinned Microsoft.Data.Sqlite 10.0.13 implementation also has a managed retry loop
whose command timeout is in whole seconds. The adapter sets `DefaultTimeout=-1`
on the connection to stop that additional retry loop and let the native handler
own the budget. This is version-specific behavior verified against the
[provider source](https://github.com/dotnet/efcore/blob/v10.0.13/src/Microsoft.Data.Sqlite.Core/SqliteDataReader.cs)
and process-contention tests; retain those tests when upgrading the package.
Overriding a returned command's timeout changes this behavior.

SQL operations are synchronous. Cancellation is checked before work, between
migrations, at transaction commit boundaries, and during WAL backoff. It cannot
interrupt an already executing provider operation, which remains bounded by the
native busy timeout for lock contention. Schedule lengthy maintenance away from
MCP/UI request paths rather than treating ADO.NET async methods as asynchronous
SQLite I/O. See [Microsoft's SQLite guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async).

On Unix, new directories use 0700; new databases and completed backups use 0600.
SQLite inherits the database mode for WAL/shared-memory sidecars. Existing file
and directory modes are preserved. On Windows, files inherit the parent ACL,
matching the pinned Go behavior; Unix mode bits do not express Windows ACLs.
Native Unix/macOS permission and filesystem tests remain to be executed on those
hosts.

An existing ledger is checked before changing journal mode. A database with a
newer or mismatched ledger is rejected without schema/data changes; nonpositive
ledger versions are rejected as invalid data. An upgrade backs up with parameterized
`VACUUM INTO` before applying scripts. Each script and its ledger row commit in
one immediate transaction. A failed script or foreign-key check rolls back that
migration and reports the recovery backup; earlier successful migrations stay
committed. The foreign-key marker is honored only for the affected connection,
and enforcement is restored on both success and failure. Current schemas take no
write transaction. Backups use the source `schema-NN-UTC-xxxx.db` naming and
descending filename order; pruning beyond five is best effort, as in Go. A
completed backup is atomically renamed from a private temporary name; a final-name
collision never overwrites or removes an existing backup.

The storage API uses .NET exceptions and nullable inspection errors rather than
Go error values. Higher-level CLI/MCP adapters must map them to the frozen wire
contracts. No public transport behavior is implemented by this task.

Local validation used SDK 10.0.402, Microsoft.Data.Sqlite 10.0.13, the bundled
SQLitePCLRaw 2.1.13 native library, and all repository analyzers with warnings as
errors. The machine's inherited NuGet mapping required the isolated public-source
config from the package spike; user/global NuGet configuration was unchanged.
The final suite passed 21 tests with the Unix permission test skipped on Windows.
Successful binlogs and TRX reports are under `artifacts/package-spike`, and the
Go helper is under `artifacts/storage-oracle`. The
[validation receipt](inventory/storage-validation.json) records test outcomes,
migration hashes, the native SQLite version, and binary/binlog hashes.
