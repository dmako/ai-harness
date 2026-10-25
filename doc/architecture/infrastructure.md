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

The complexity reader counts each function's cyclomatic complexity from the
tokens the clone extractor already holds. It reads no file a second time and
skips the same test, generated and vendor files. A function nested in another
(a lambda, closure, local function, arrow or function expression) counts toward
the one around it. Each language follows its usual tool:

| Language | Follows | One plus each |
| --- | --- | --- |
| C# methods and constructors | Microsoft.CodeAnalysis.Metrics | `if`, `case` label, `for`/`foreach`/`while` (do-while once), `?:`, `?.`/`?[]`, `??`, `&&`, `\|\|`; not switch-expression arms, `catch`, `when`, patterns or `??=` |
| Go functions and methods | gocyclo | `if`, `for`, `case` clause of a `switch` or `select` (not `default`), `&&`, `\|\|` |
| TypeScript/JavaScript functions, methods and named arrows | ESLint `complexity` | `if`, `for`/`for-in`/`for-of`, `while` (do-while once), `case`, `catch`, `?:`, `&&`, `\|\|`, `??`, `?.`, `&&=`/`\|\|=`/`??=` |

Branches inside C# interpolated strings and JavaScript template literals count
too. `[architecture.complexity]` turns each value into a level for the languages
it lists (`languages`, by default C# and Go; add `typescript` to include
TypeScript and JavaScript). By default, 11–20 is a note, 21–50 a warning and 51
or more a problem; below the note threshold nothing is reported. A language's
own table, such as `[architecture.complexity.typescript]`, overrides any of the
three thresholds. A finding names its language. Its key names the member and
not the level, so a changed threshold or value shows the same finding as
changed.

With `[architecture.complexity.metrics] enabled = true`, .NET members can get
exact values. When a sync (run by the git hooks) finds no finished run for HEAD,
it starts `harness complexity measure` detached and does not wait for it. On
other platforms it reports the worker as unsupported. The worker:

1. holds a per-project lock, so one run per project at a time (a newer sync
   stops a worker that is still measuring an older commit);
2. runs Microsoft.CodeAnalysis.Metrics from a cache next to the global
   configuration, on the non-test C# and Visual Basic projects that changed
   since the last finished run, lowest dependency first, and keeps the other
   projects' members;
3. stores the members (complexity and maintainability index) in one file per
   commit in that machine directory, never in the repository or the database;
4. refreshes the findings snapshot if HEAD is still that commit.

A run whose commit is no longer HEAD is dropped. Findings for HEAD use the
measured values for the C# members at the same path and line (source tool
`Microsoft.CodeAnalysis.Metrics`) and add members only the run reports, such as
properties. Any other commit keeps harness's own count. A failed run (no SDK,
NuGet unreachable, projects that do not load, timeout) keeps the counted
findings. `doctor` and `provider_status` report the worker's state and its fix,
and architecture coverage names it under `arch.complexity`.
[Code metrics](code-metrics.md) records the counting rules, their accuracy
against `Microsoft.CodeAnalysis.Metrics`, and the planned exact-value worker.

Findings retain snapshots, evidence, drift and marks. Agent proposals remain
pending; approval can create tasks or deliberate-design marks. Task generation
is explicit and preserves the author and source finding.
