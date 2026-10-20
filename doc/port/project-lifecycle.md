# Configuration and project lifecycle

TASK-0006 / PORT-006 adds `init`, `status` and `doctor`. Core holds identity
records; Infrastructure handles TOML, Git, SQLite registration and the machine
registry. Cli composes these through `ProjectLifecycle`.

```powershell
dotnet src/Harness.Cli/bin/Debug/net10.0/harness.dll init --project C:/path/to/repository --dry-run
dotnet src/Harness.Cli/bin/Debug/net10.0/harness.dll init --project C:/path/to/repository
dotnet src/Harness.Cli/bin/Debug/net10.0/harness.dll status --project C:/path/to/repository --json
dotnet src/Harness.Cli/bin/Debug/net10.0/harness.dll doctor --project C:/path/to/repository --json
dotnet src/Harness.Cli/bin/Debug/net10.0/harness.dll doctor --all --json
```

Single-dash Go spellings such as `-project`, `-dry-run` and `-json` are accepted.
Operational failures return 1, usage errors 2, cooperative cancellation 130.
As in Go, status returns 0 inside a discovered repository even when its report
contains a configuration/database problem; doctor returns 1 for failed checks.
Uninitialized repositories are inspectable; init requires a Git worktree.
`LocateAsync(..., allowNonGit: true)` supports the stdio MCP server outside Git.

## Configuration

Resolution is defaults, global TOML, then the current worktree's
`.harness/config.toml`. Missing layers are optional. Existing malformed files,
unknown keys, wrong types and invalid values reject the load before init writes.
An invalid global layer cannot be masked by a project layer. Files are strict
UTF-8 with an optional UTF-8 BOM. Tomlyn 2.10.1 parses TOML; explicit models and
validation check nested keys, provider types/capabilities, routing and mappings.

| Platform | Default global file |
| --- | --- |
| Windows | `%APPDATA%/harness/config.toml` |
| macOS | `$HOME/Library/Application Support/harness/config.toml` |
| Linux | `$XDG_CONFIG_HOME/harness/config.toml`, otherwise `$HOME/.config/harness/config.toml` |

Nonempty `HARNESS_CONFIG_DIR` replaces the directory on every platform and also
locates neighboring `projects.json`. OS selection is unit-tested with explicit
environment inputs; native Linux/macOS execution remains a release requirement.

Storage/log/sync settings overlay by key. A project provider table replaces its
global table completely. Five frozen Go packs are embedded with their MIT license
under `Configuration/Packs`: codebase-memory, graphify-csharp, nuget, rg-fd and
ripwire. A table selects a pack explicitly or implicitly through its name when
no type is supplied. Explicit keys override defaults, including false and empty
arrays. Capabilities merge by capability name, replacing each supplied mapping
completely. Loading packs does not start providers. Regex validation translates
Go-style `(?P<name>...)` groups for .NET and applies a bounded regex timeout;
provider mapping execution/conformance remains PORT-013/014.

## Git and identities

Git is resolved from absolute PATH entries and executed with `ArgumentList`,
without a shell. Git supplies root, Git dir, common dir, branch and HEAD. Directory
overrides inherited from hooks are removed. Prompts, pagers and optional index
locks are disabled. Subprocesses have cancellation and a ten-second deadline,
with child cleanup on cancellation. Git must be installed; this is an intentional
implementation choice from PORT-003, replacing Go's direct metadata parsing.

For conventional `<main>/.git` repositories, linked worktrees share the main
tree's database, while reading their own config. Relative DB paths resolve
against the state root. Separate Git directories/submodules fall back to the
current root when the main root is indeterminate, matching Go.

Registration uses one immediate SQLite transaction. New IDs retain `prj_`/`wt_`
plus 32 lowercase hex characters; imported IDs remain intact. Worktree identity
uses the Git directory, surviving linked worktree moves. If the original DB no
longer exists, moving a repository keeps its project ID. If a different original
DB still exists, a copy gets a new ID and `copied_from`, retaining knowledge while
dropping old worktree/cache rows. Existing init files/comments are preserved.
Dry-run never opens the DB, creates directories or updates the registry.

Inspection retains deleted worktree records; mutating sync pruning belongs to
PORT-016. Detached HEAD has an empty branch; unborn HEAD has an empty commit.

## Inspection and registry safety

Status/doctor open existing DBs read-only and never migrate, register, rewrite
config, update registry/timestamps or create missing state directories. Old
schemas are reported as pending; identity queries require a current ledger.
Lifecycle JSON preserves Go's optional/null/empty semantics, including
`schema_latest = 0` before a database is inspected.

SQLite may create `-wal`/`-shm` sidecars even for a read-only WAL connection, as in
the Go path. Tests distinguish that coordination from application-state writes:
database/config/registry bytes and write times remain unchanged, no migrations
run, and live committed WAL data stays readable without appending frames. Using
`immutable=1` would risk ignoring a live WAL, so it is not used. See
[SQLite's read-only WAL guidance](https://www.sqlite.org/wal.html#read_only_databases).

Registry updates hold an exclusive OS-backed file-sharing lock on
`projects.json.lock`, with cancellation and a ten-second wait limit. The file
remains, but its handle owns the lease, so a crash leaves no stale lease. Updates
preserve names, titles, other event timestamps and unknown future fields, flush
a same-directory temp file, compare original bytes, then atomically replace the
registry. Malformed data is preserved; registry failure does not fail project
init. Unix registry files are owner-only. Windows serialization improves Go's
no-op Windows lock. C# competing-process tests cover this; the lock cannot
serialize a Go writer that ignores it.

Doctor currently checks binary, config, Git, identity and database. Full client,
instructions/hook detection, provider probing and sync reports remain assigned
to PORT-025/026, PORT-012–015 and PORT-016. UI overview/report features remain in
their later tasks; this lifecycle task does not claim full command parity yet.

## Validation

Unit and real Git/process tests cover configuration precedence/strictness/packs,
idempotence, dry-run, old-schema inspection, non-Git directories, moved/copied
projects, detached/unborn HEAD, moved/deleted linked worktrees, spaces/Unicode,
inherited Git overrides, live WAL, corrupt registries, cancellation and competing
registry writers.

Validation passed 67 tests with one Unix-permissions test skipped on Windows,
plus strict Release/CI build and locked restore. The Windows self-contained
single-file package passed isolated dry-run/init/status/doctor, repeated identity,
SQLite schema 19 and embedded-pack checks. Native Linux/macOS execution remains
outstanding.

After building, run the differential comparison:

```powershell
python test/Harness.Compatibility/lifecycle.py --oracle artifacts/compatibility/oracle/harness.exe --candidate src/Harness.Cli/bin/Debug/net10.0/harness.dll --output artifacts/lifecycle-differential.json
```

Eight scenarios compare lifecycle status fields exactly against Go commit
`9cb401dd2de4f85dd089fea86a0b139d81b178c6` and verify identity interoperability.
Only client detection and sync reports are excluded as later tasks. Local
artifacts remain ignored; the retained [receipt](inventory/lifecycle-validation.json)
records commands, results, source/lock hashes and limits. No personal DB or
registry is used.
