# Task execution and proposals

TASK-0011 / PORT-011 adds six MCP tools to the task planning/storage from
[PORT-010](tasks-and-backlogs.md). The server now advertises twenty tools.
The six task tool descriptions and input/output schemas are copied from the
frozen Go `tools/list` at `9cb401dd2de4f85dd089fea86a0b139d81b178c6`.
No package or schema migration was added.

## Surfaces and boundaries

| Surface | Behavior |
| --- | --- |
| `task_next` | Peek at the next ready task, or atomically select and claim it. Optional milestone and lease. |
| `task_get` | Read one task, optionally with audit events. Never expose the stored token. |
| `task_claim` | Claim ready work with a random capability token and a bounded lease. |
| `task_update` | Report partial acceptance, append a redacted note, change status and renew the lease. Ready releases a claimed/in-progress task; review follows the frozen workflow matrix. |
| `task_complete` | Validate all reported completion evidence and commit the task, evidence, audit and newly unlocked dependants together. Optional knowledge proposals follow as best effort. |
| `task_propose` | Save a proposed definition for human approval. It does not create a task. |
| `tasks claim`, `release`, `complete` | Human CLI execution, using a privileged actor and recording identity in the audit. |
| `tasks proposals`, `approve`, `reject` | List proposals, create one task on approval, or close a proposal with a note. |

`ITaskSession` is a Core port implemented by the existing `HarnessStateSession`.
The MCP adapter depends only on Core; SQLite remains in Infrastructure. Existing
project state is required and task tools never initialize a missing database.
CLI and MCP share `TaskStore` operations and public `TaskViews`.

The recursive argument validator covers the advertised nested objects, nullable
arrays and completion validation's string/object `anyOf`. Missing nested fields,
unknown fields and wrong types fail before dispatch, with Go's diagnostic paths
and completion-format hint. It is a validator for these implemented schemas,
not a general JSON Schema engine. Schema error values retain the existing
credential-redaction safeguard from PORT-009.

## Claims, progress and completion

Each claim generates 12 random bytes with `RandomNumberGenerator`, returned as
`clm_` plus 24 hex digits. Agent identity comes from MCP initialization's client
name/version. Ownership is proved by the token, rather than by the client name:
two sessions with the same name cannot mutate each other's work without it.
A valid token may be used by a different caller, whose activity is audited under
that caller's identity, matching Go. Reads never return the token. Human CLI
operations are privileged, as in Go.

MCP leases default to 120 minutes and cap at 1440 minutes. Nonpositive values use
the default. Progress activity renews to the default two hours, even when the
original claim used a shorter lease. Expired claimed/in-progress/review work is
recovered with a `lease_expired` event before readiness recomputation. Reclaim
produces a fresh token; the old one stops authorizing updates. Explicit release
without the token requires a privileged actor and a reason; the CLI supplies
`released by claimant` for its own claim, matching Go's force-release audit.

Claim, progress, release and completion use immediate SQLite transactions. Rejected
operations roll back any refresh, notes, evidence and audit changes in that attempt.
Completion requires a nonempty summary, an optional valid lowercase hex commit,
every dependency done, exactly one report per criterion, pass or explained n/a
for each criterion, and pass for every supplied validation check. Prior partial
results do not substitute for complete reports. Duplicate/unknown indices and
failed checks reject the whole operation. Go permits no validation entries,
including when no commands were run; the port preserves that behavior. Commands
are reported evidence and are never executed by the completion handler.

On successful completion, claimant/token/lease are cleared and the original
`claimed_at` is retained in storage, as in Go. Acceptance evidence, notes,
completion summary and validation command/output use the existing credential
redactor. New dependent readiness and its events commit with completion.

Optional `task_complete.proposals` are processed after the task commits. Their
default source reference is `task:<id>`. Invalid proposals return `proposal_errors`
without undoing completion; valid ones remain pending knowledge. Cancellation
continues through the existing SDK transport cancellation/EOF handling. Commit
context-scoring integration belongs to the later telemetry port.

## Task proposals and concurrency correction

Task proposals require title, goal and acceptance criteria, normalize the planning
definition, validate existing dependencies and store provenance. There is no
deduplication; IDs are `tpr_` plus eight random bytes in hex. Approval may use an
edited definition at the store boundary; CLI approval accepts the proposal and an
optional prefix. Task creation and proposal decision commit in one transaction.
The task's source is `proposal:<id>` and its creation event identifies proposer and
reviewer. A decided proposal cannot be decided again. Rejection creates no task.

The frozen Go MCP handler calls `Next` and `Claim` separately. This port deliberately
makes `task_next(claim=true)` atomic, as TASK-0011 requires: refresh, selection and
claim share one write transaction. Concurrent callers claim distinct ready tasks
or receive the normal empty result; none loses a select/claim race. Dependency
validation and proposal insertion also share one transaction in the port. The
existing fast read path and SQL ranking are retained for read-only next calls.

CLI durations use Go-style `h`, `m`, `s`, `ms`, `us`/`µs`/`μs`, and `ns`, with
compound/fractional values. CLR clock/duration precision is 100 ns; positive
sub-tick CLI durations round up to a tick. MCP whole-minute leases are exact.
Stored imported nanosecond timestamps retain their full precision. New execution
writes use CLR clock precision within the existing nine-digit storage format.

## Validation and reproduction

The .NET tests cover token/actor behavior, lease renewal and exact-boundary expiry,
reclaim, forced release, every completion rejection category, proposal atomicity,
ten nested-schema cases and four independent official-SDK MCP clients racing for
one claim and then one atomically selected next task. Existing memory/storage Go
bridge tests remain enabled in the final full suite.

`test/Harness.Compatibility/Tasks/execution.py` compares all six frozen schemas,
canonical MCP text/structured results, nested schema diagnostics, bounded leases,
progress/review/release, rejected-write snapshots, completion/unlocking and
best-effort memory proposals against the real pinned Go executable. It compares
CLI proposal review/claim/release/completion JSON and human text. Candidate-only
process races verify atomic next/claim and one-time CLI proposal approval; those
checks test the documented concurrency correction rather than requiring Go's race.
Only volatile identities, timestamps and isolated temporary roots are normalized.

```powershell
python test/Harness.Compatibility/Tasks/execution.py `
  --oracle artifacts/compatibility/oracle/harness.exe `
  --candidate src/Harness.Cli/bin/Debug/net10.0/harness.dll `
  --output artifacts/task-tools-managed-validation.json
```

The [validation receipt](inventory/task-execution-validation.json) records source,
schema, lock and executable hashes plus the checks actually run. Validation is on
Windows x64; other release targets still require platform validation. `harness
doctor` reports no failures and the existing warning that `CLAUDE.md` does not
import `AGENTS.md`; code-search providers remain unconfigured and optional.
