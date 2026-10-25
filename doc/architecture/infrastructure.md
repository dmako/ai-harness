# Infrastructure and durable state

Source: [`src/Harness.Infrastructure`](../../src/Harness.Infrastructure).
Infrastructure depends only on Core and implements persistence and operating
system interactions. Its main packages are Microsoft.Data.Sqlite, Tomlyn and
YamlDotNet; package versions are centralized in `Directory.Packages.props`.

## Project lifecycle and configuration

Project discovery handles normal repositories, linked worktrees and explicit
non-repository locations. The machine registry locates known projects and
workspaces. `HarnessStateSession` connects a located project to knowledge, task,
retrieval and diagnostic operations without making read-only requests initialize
state. `init`, `status` and `doctor` share lifecycle services.

Configuration combines global and project TOML, validates fields before merging,
and expands the embedded provider packs. User overrides remain explicit. TOML
reads use generated metadata; syntax-aware edits preserve unrelated client
configuration, comments and values. File editing detects conflicting writes and
rejects malformed text before creating backups or changing files.

## Storage

`Storage/Migrations` contains the ordered nineteen-version SQLite schema.
Migrations run transactionally and check their ledger. Upgrades create recovery
backups, preserve foreign-key enforcement and retain the latest five backups.
Read-only inspection never creates or migrates a database. WAL, foreign keys,
strict tables and bounded busy waits are established on connections.

`Microsoft.Data.Sqlite` uses `DefaultTimeout=-1` so its whole-second managed
retry loop does not override the millisecond `PRAGMA busy_timeout` budget.
Cross-process contention tests protect this behavior on dependency upgrades.

Memory stores implement history, supersession, retirement and proposals.
Knowledge levels combine project, branch, workspace and wider stores while
preserving provenance. Failed or missing wider stores remain unknown/missing
instead of silently creating new storage.

Task stores implement dependency readiness, lease ownership, rejected-write
atomicity, milestones, proposals and completion validation. YAML backlogs have
strict fields and explicitly written representation nodes. Export checks for
concurrent file edits; re-import updates definitions without replacing execution
history.

## Synchronization and workspaces

Git discovery reads ordinary references directly for inexpensive MCP requests;
Git processes provide history, diffs and specialized repository operations.
Incremental synchronization tracks files, worktrees, branch/commit transitions
and dirty state. Cancellation or failed work does not publish a partial success.

Workspaces have their own membership/state and connect project evidence without
writing member databases during reads. Searches preserve project/provider
provenance. Light queries avoid optional compiler startup; deep queries may
request it. Dependency, adoption, repeated-code and change-coupling readers cache
physical reads within a request and refresh on subsequent requests.

## Architecture evidence and findings

File readers inspect .NET, JavaScript/TypeScript and other recognized manifests,
public declarations, imports, configuration and repository history. Static
MSBuild evaluation is bounded and does not load build tasks or execute projects.
Compiler-exact semantics belong to external providers.

Repeated-code analysis compares functions, normalized structure and statement
sequences, filters generated/test/vendor sources and reports qualified evidence.
Change coupling combines history and provider evidence to suggest missing usual
partners. Layering, service and database guesses remain labeled heuristics.
Coverage gaps do not become negative findings.

Findings retain snapshots, evidence, drift and marks. Agent proposals remain
pending; approval can create tasks or deliberate-design marks. Task generation
is explicit and preserves the author and source finding.
