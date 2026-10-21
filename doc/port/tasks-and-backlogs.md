# Tasks, milestones and planning files

TASK-0010 / PORT-010 ports the planning and scheduling layer from Go commit
`9cb401dd2de4f85dd089fea86a0b139d81b178c6`. Core task definitions, workflow
rules and output views have no package dependencies. Infrastructure uses the
existing schema-19 tables and caller-owned database factory. YamlDotNet 18.1.0,
already approved in Central Package Management, is now referenced by Infrastructure.
The inherited Go MIT notice is retained in the baseline inventory and embedded
storage migration license. No migration or ORM was added.

## Surfaces

`harness tasks` (alias `task`) supports `list`, `milestones`, `show`, `next`,
`add`, `block`, `defer`, `ready`, `import` and `export`. Each supports `--project`
and `--json`; `show --events` includes audit history. Filtering supports
repeatable/comma-separated `--status` and a milestone. The default list excludes
done and deferred tasks. Task views omit internal claim tokens and retain
nanosecond timestamps in storage; public timestamps use Go's RFC3339 seconds.

Claims, lease renewal, completion evidence writes, task proposals and MCP task
tools belong to TASK-0011. This task can read their existing state, preserves it
on import, checks authorization before setting claimed tasks aside, and recovers
expired leases. The existing fourteen MCP tool definitions remain unchanged.

## Storage and scheduling

Tasks keep their definition, origin, ordinal, execution state, normalized edges,
acceptance results and append-only audit records. New manual IDs follow the
newest non-finding task's prefix and the widest numeric suffix, defaulting to
`TASK-0001`. Creation is transactional and records its actor/source/detail.

Only backlog/ready tasks participate in readiness recomputation. Every dependency
must be done. Ready work ranks by priority (critical, high, medium, low), then file
ordinal and ID. Blocked and deferred tasks require reasons; resumption recalculates
readiness. Done is terminal. Review holds a claim and can return to in-progress;
review/claimed/in-progress leases expire back to ready or backlog, with audit.

Fresh task reads use deferred query-only transactions. A stale-state probe triggers
an immediate write transaction only for expired leases or incorrect readiness.
Filters and limits run in SQLite before loading task details. Next-task selection
ranks in SQLite and loads only the winning task, avoiding a full ready-queue
materialization on repeated agent calls. Milestone progress
reads do not refresh state, matching Go. They include defined milestones with no
tasks and milestones only named by tasks, with per-status counts. An empty milestone
is not done.

## Strict import

YAML and JSON use version 1. Unknown and duplicate keys, invalid shapes and
unsupported versions fail. UTF-8 is decoded strictly, with optional BOM. YAML
aliases, merge keys and multiline strings are supported; as with Go's single
decoder call, only the first document is read. Parser-specific error wording is
not identical to yaml.v3. Definitions are normalized and then validated once at
the storage boundary. Dependency checks use the combined database/file graph,
rejecting unknown references, self edges and cycles before any task write.
The iterative graph traversal avoids stack overflow for long dependency chains.
Stored string-array JSON preserves Go's UTF-8, supplementary Unicode characters
and HTML escaping, including the byte-level comparison used for milestone exit
criteria. Reimporting an exchanged Unicode milestone does not manufacture a change.

Task import is one immediate transaction. A dry run executes the same writes and
calculations, then rolls the transaction back. File tasks replace their own edges;
database tasks absent from the file remain and are reported as orphaned. File
order updates ordinals without manufacturing definition-change events.

Reimport preserves status, claim/token/lease, completion, execution notes and
acceptance results whose criterion text survives. Duplicate criterion text uses
the last old result, matching Go. New criterion text starts pending, even on a
completed task. Initial file statuses accept backlog, ready, done and blocked;
ready is recomputed. Done imports seed completion and n/a acceptance evidence.
Deferred and live-claim statuses cannot be seeded through a planning file.

Milestone definitions are imported in a separate transaction **after tasks**, as
in Go. Consequently an invalid milestone can fail the command after task changes
commit. Dry runs commit neither transaction. Missing milestones are retained.

## Export and compatibility differences

Export adds only tasks missing from a backlog; it does not reconcile or rewrite
existing definitions. An existing YAML file retains its bytes, comments and list
indentation, except the necessary `tasks: []` to `tasks:` expansion. The tasks list
must be the final section when appending; validation reparses the proposed output
before writing. Existing JSON imports are supported, but appending into existing
JSON is rejected because it has no top-level block-style `tasks:` list, matching Go.
A new file includes defined milestones with nonempty titles and all tasks. Only
done/blocked statuses are exported; other execution states remain database-only.
Generated YAML follows YamlDotNet's scalar presentation rather than guaranteeing
byte-identical yaml.v3 emission; parsed planning values roundtrip unchanged.

One intentional Windows correction is covered separately: Go's LF-only export
regex rejects an existing CRLF `tasks:` line. The C# regex accepts CRLF, preserves
the existing byte prefix and appends valid YAML. The differential suite asserts
the Go rejection and C# success explicitly, rather than normalizing the difference.

Writes use a same-directory temporary file, flush and rename; an observed change
to the original prevents replacement. Unix mode bits are preserved for replacements
and new files use 0644. Windows inherits directory ACLs. This is optimistic
concurrency, as in Go; there is no cross-process export lock covering the interval
between the final content check and replacement.

## Validation

`test/Harness.Integration/Tasks/TaskStoreTests.cs` exercises strict parsing, merged
YAML/JSON, import normalization, DAG failures, dry-run snapshots of all five task
tables, claims/completion preservation, state/audit/authorization, fresh reads
under a writer lock, append-only export and schema-5/13/19 copied-fixture roundtrips.
The unit test checks all 64 workflow state pairs.

`test/Harness.Compatibility/Tasks/contracts.py` compares real CLI JSON, human text,
errors, audit views, priorities, reimports, partial failures and exports against
the frozen Go executable in isolated Git/configuration directories. It also checks
task-table parity and exchanges database copies in both directions. Only known
volatile timestamps and temporary roots are normalized. Parser diagnostic wording
and generated YAML scalar presentation use semantic checks; rejected inputs must
still leave task tables unchanged. CRLF correction checks remain explicit.

```powershell
python test/Harness.Compatibility/Tasks/contracts.py `
  --oracle artifacts/compatibility/oracle/harness.exe `
  --candidate src/Harness.Cli/bin/Debug/net10.0/harness.dll `
  --output artifacts/tasks-managed-validation.json
```

The [validation receipt](inventory/tasks-validation.json) records the final source,
package-lock and executable hashes and the checks actually run. Validation in this
task runs on Windows x64; Unix permissions and other publish targets require their
platform runs before release.
