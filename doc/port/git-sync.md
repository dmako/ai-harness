# Incremental Git synchronization and freshness

TASK-0016 / PORT-016 adds `harness sync`, the Core sync models/ignore/freshness
rules, and Infrastructure Git, file, state and project synchronization services.
It uses the existing external Git, Microsoft.Data.Sqlite and provider refresh
contracts. No new packages, CPM versions or SQL migrations are required.

## Scan behavior

The first sync, an incremental branch/detached transition, a missing previous
commit, or `--full` scans all Git-tracked and nonignored untracked files.
Incremental runs compare the previous synchronized commit with the working
tree, including committed, staged, unstaged and untracked changes. `--since`
selects an explicit existing base; it is mutually exclusive with `--full`.
An unborn worktree is supported. A rewritten but still available base can be
diffed normally; an unavailable base falls back to a full scan.

Git applies `.gitignore`; harness additionally excludes `.harness/`, `.git/`
and `[sync].ignore`. Directory, basename and path glob rules follow the pinned
source. Renames crossing an ignore boundary become an addition or deletion.
Unstaged identical-content moves are paired with Git blob hashes. Previously
dirty paths are reconsidered even after they disappear from the Git diff, so
reverted edits and deleted untracked files cannot leave cached hashes behind.

The stored set is per worktree. Content hashes are SHA-256; symlink hashes use
the UTF-8 text `symlink:` plus the link target. Normal files with matching size
and modification time are not opened. A touched file is hashed once; identical
content updates metadata without refreshing a provider. Ignore matchers are
prepared once per run. Incremental candidate lists, rather than the whole
repository, are hashed. These are correctness/performance properties tested by
read counts; numerical latency budgets remain PORT-030 work.

Each file update batch commits in one transaction. The synchronized state
advances only after file updates, provider refreshes and the final Git status
read. A file-batch failure rolls that batch back. As in Go, a later failure
(including cancellation during refresh) can leave already committed file hashes
with the older synchronized state; the unchanged base and pending list allow
the next run to recover. `sync_runs` retains running/ok/partial/failed outcomes,
commits, counts, provider outcomes and errors. A cancelled run is finalized using
an uncancelled database operation.

## Locks, providers and worktrees

Every project sync holds `<worktree-git-dir>/harness-sync.lock` through registration,
scanning, refresh and state persistence. HEAD is read after acquiring that lock.
The file lease works across processes on Windows and Linux; contention waits
are cancellable, permanent I/O failures are reported, and missing/pruned Git
directories are never recreated. This intentionally corrects the pinned Go
Windows no-op lock. macOS code paths are present but have not been executed.

An incremental run queued before another successful run started can reuse that
run when it recorded the same HEAD. Explicit full/since runs are never skipped.
The coalescing key is the pinned source's worktree, start time and HEAD, rather
than a snapshot of dirty file contents. Separate worktrees use separate locks,
IDs and file/state/run rows in their shared project database. Sync prunes the
cache rows of other worktrees only when their Git directories are verifiably
missing; inaccessible directories and the current worktree are preserved.

Only adapters exposing the provider refresh contract receive refresh calls.
Those calls happen on content changes, full scans or a previous refresh failure;
no-op incremental runs do not initialize providers to refresh them. Each refresh
has its own cancellation/deadline (ten minutes by default). Failed refreshes
record partial success and `providers_stale`, causing a retry at the next sync.
Skipped refreshes are retained distinctly. The live graphify pack is excluded
from sync composition; its watch maintains itself and must not start merely to
discover that it has no index to refresh.

The shared freshness rules report never-synced, cross-branch and moved-HEAD
states. The provider facade continues withholding indexed search/overview
results on an unsynchronized branch transition while permitting live results;
a successful sync clears that branch mismatch. As in the pinned source,
`providers_stale` controls retries, while branch withholding compares the last
recorded sync branch. It is not an independent per-provider index-generation
ledger.

## Validation and compatibility limits

Isolated fixtures cover edit/revert, committed and untracked renames/deletions,
configured ignores, metadata shortcuts, explicit full/since, unborn/detached
HEAD, real rebase, missing bases, linked-worktree pruning, transactional failure
and recovery, refresh retry/timeout/cancellation, queued runs, separate CLI
processes and branch-sensitive provider results. Unix fixtures additionally
check broken tracked symlinks and reject traversing directory links outside the
worktree. File-batch paths are checked for containment before access.

The frozen Go driver compares twelve successive states against C#: modes,
reasons, base/HEAD, ordered changes and file statistics, dirty/pending state and
every stored file hash. Only timestamps and generated IDs are excluded from
this comparison. It runs against disposable independent databases, never a
personal project database.

The port retains the source's `git hash-object` unstaged-rename pairing: Git can
reject a broken untracked link when deleted paths are being paired. Staging the
link or using a full scan avoids that source behavior. .NET file timestamps
have 100 ns precision; higher-precision imported Unix metadata may cause one
extra hash before the metadata shortcut applies. Symlinks are always rehashed
from their link text. Git revision arguments reject option injection; invalid
UTF-8 Git output is rejected instead of silently changing a path. File access
through directory links is rejected rather than following it outside the root.

Findings snapshots, context-outcome scoring and telemetry export remain in
their later port tasks. Sync currently performs the file/provider/state workflow
and best-effort machine `last_sync` bookkeeping.

Build the optional oracle with:

```powershell
python test/Harness.Compatibility/Sync/build_oracle.py --source artifacts/compatibility/oracle/source --go artifacts/compatibility/toolchain/go/bin/go.exe --modules artifacts/compatibility/oracle/modules --output artifacts/sync-oracle/sync-oracle.exe
$env:HARNESS_GO_SYNC_ORACLE="$PWD/artifacts/sync-oracle/sync-oracle.exe"
```

Pass `--goos linux` and a Linux output path to build the WSL oracle. See the
[validation receipt](inventory/git-sync-validation.json) for commands, platform
coverage and source/artifact hashes.
