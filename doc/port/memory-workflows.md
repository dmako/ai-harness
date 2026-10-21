# Memory MCP and CLI workflows

TASK-0009 / PORT-009 exposes the [memory storage](memory-storage.md) through ten
MCP tools and seven CLI commands. The server now advertises 14 implemented tools
including the four bootstrap tools. All ten memory tool definitions (descriptions,
input schemas and output schemas) are frozen from the pinned Go `tools/list`.
No package or schema migration was added.

## Commands and tools

| Surface | Implemented behavior |
| --- | --- |
| `memory_put` | Store a statement or report exact/near duplicates; optional expiry, confidence, provenance, force and explicit supersession. Accept project, path, subsystem, branch, member workspace or user scope. |
| `memory_get` | Find a project/wider memory, optionally including chain and selected memory's audit trail. |
| `memory_search` | Any-word FTS, type filters, applicable scope plus current branch, inactive inclusion, default 20/max 100. An undefined convention can suggest other projects' conventions. |
| `memory_supersede` | Replace at the existing level; require a reason. Moving project knowledge wider uses explicit CLI promotion. |
| `goals_current`, `constraints_current`, `decisions_for` | Read current applicable knowledge, including wider levels. Unscoped current views include all project scopes; scoped views also refresh the branch. |
| `memory_propose` | Record project-local pending knowledge, report equivalent existing memory/proposal and related current memories. Proposals are never active memory. |
| `memory_proposals`, `memory_review` | List pending/approved/rejected/all; approve with optional edits, type/scope changes, supersedes or keep_both, or reject. Record reviewer identity and note. |
| `harness memory show <id>` | Inspect selected memory, history and audit. |
| `harness memory retire <id> --reason <text>` | End validity without deleting history. |
| `harness memory promote <id> --to workspace:<name>\|user [--force]` | Gather other-project evidence, create/reaffirm wider knowledge, then retire the project statement. |
| `harness proposals list\|show\|approve\|reject` | Human review with `--status`, `--content`, repeatable `--supersede`, `--keep-both` and `--note` where appropriate. |

All CLI commands accept `--project` and `--json`. Original Go single-dash flag
spellings are also accepted. Approval conflicts return exit 1 and guidance (or
the original raw approval JSON); successful approval/rejection returns exit 0.

```powershell
harness memory show mem_<id> --project C:/repo --json
harness proposals list --project C:/repo
harness proposals approve prop_<id> --supersede mem_<old> --note "user confirmed" --project C:/repo
harness memory retire mem_<id> --reason "no longer applies" --project C:/repo
harness memory promote mem_<id> --to workspace:home --project C:/repo
```

## Ownership, provenance and atomic review

Core's `IMemorySession` is the storage-independent boundary consumed by MCP.
`MemoryService` in Infrastructure performs the shared use cases; CLI composes it
with the existing project lifecycle. `HarnessStateSession` reuses its lazy
project database and cached registered identity, keeping repeated MCP reads off
extra registry/database discovery. Unscoped reads do not launch Git. Scoped
reads refresh the branch. Wider membership and stores are opened per operation
so membership edits affect existing sessions on their next request; leases close
all stores, including partially opened levels on failure.

MCP provenance comes from initialize's client name and version. Proposal approval
keeps the proposer's source_ref and recorded_by on the resulting memory; the
reviewer is recorded separately on the proposal. Supersession audit events also
retain the original proposer's actor, matching Go. Human CLI operations use the
OS account as `human:<user>` (including the Windows domain/account name).

Proposal lookup, validation, memory creation/reaffirmation, predecessor updates,
audit and approval status update share one immediate transaction. A near-duplicate
conflict returns needs_decision while leaving the proposal pending. Invalid edits,
missing/stale predecessors, credential content and repeated reviews roll back.
Rejected proposals retain provenance and notes without creating a memory.
Rationale is trimmed and redacted before being stored. Exact proposal/memory
deduplication is a no-op, without a reaffirmation event. Pending counts use a
single aggregate query.

Go's precedence is preserved: keep_both ignores a supersession named by the
proposal, but an explicit reviewer supersedes list still applies when keep_both
is true. Approving wording that exactly matches an existing memory reaffirms
that memory and links the approved proposal to it.

## Wider levels and boundaries

Workspace discovery reads `workspaces.json` beside global `config.toml`.
Knowledge lives in `knowledge/user.db` and `knowledge/workspace-<workspace-id>.db`.
Workspace management commands remain PORT-020; this task only reads membership.
Missing wider stores are skipped on reads. Malformed workspace metadata or a
failed wider-store open falls back to project knowledge, as in Go.

Project operations require an initialized database; they never initialize one
implicitly. A user-scope put may operate without project initialization. Workspace
writes require a registered project that belongs to that workspace. Proposals
and their review stay in the project store; editing a proposal into wider scope
is rejected. A wider memory can be superseded only at its own level. Filesystem
paths derived from corrupt workspace IDs are rejected before opening a store.

Other-project conventions and promotion evidence are read through read-only
connections. Those projects are not created, migrated or modified. Promotion
accepts only current project-wide statements. Its new wider memory records human
provenance and evidence references; it uses the original Go behavior of not
copying confidence or the project's validity window. It then retires the local
statement. The two database commits are separate, as in Go: a failure between
them can leave the wider statement stored and the local one still active. A
retry reaffirms the wider duplicate before attempting retirement again.

## Wire compatibility and diagnostics

MCP and CLI memory details share `MemoryViews`: RFC3339 seconds, optional fields
omitted and no updated_at. The proposals CLI intentionally retains Go's different
PascalCase domain JSON, including raw timestamp precision, nullable fields,
scope/source objects and full similarity values. Its empty list is `null`;
MCP's proposal list is `[]`. CLI wider-memory history retains stored project
scopes while its selected memory is relabeled; MCP relabels the entire chain.

The argument validator covers the actual flat object, scalar, nullable and
string-array schemas advertised by these tools. It preserves required/optional
argument hints, known path/scope corrections and type rejection messages.
It is not a general JSON Schema evaluator; future tool schemas need corresponding
validation and tests. Integral numeric spellings such as `1.0` follow the Go SDK's
float64-to-int conversion; limit handling preserves its rounding and overflow
behavior near the signed 64-bit boundary.
Platform filesystem error prose can differ; contextual prefixes/envelopes are
preserved and the differential fixture normalizes only the uninitialized OS error.

Content rejection includes only the redacted statement, matching Go. As an
intentional diagnostic safety correction, schema-error values are also redacted
before MCP returns them; the original Go SDK can quote those raw values. No
secret-like value is needed for the expected-arguments hint. A redaction timeout
returns a generic rejection. This does not turn the credential heuristic into an
exhaustive detector.

## Verification

The .NET suite includes atomic proposal approval/rejection, provenance, defaults,
near-duplicate decisions, keep-both/supersedes precedence, invalid-edit rollback,
concurrent approval, read-only legacy evidence, corrupt IDs, failed-level cleanup,
credential diagnostics and two independent official SDK clients. Task-0008's
live Go storage and Unicode tests remain enabled.

The isolated differential runner compares all ten exact schemas, structured and
text result envelopes, both agents' identities, scope/branch/expiry behavior,
proposal/review conflicts, CLI text and both JSON shapes, repeatable flags,
retirement, promotion, external evidence and membership refresh. Rejected calls
snapshot every memory/proposal/event row in the project and existing wider
databases. Normalization is restricted to random IDs, temporary roots,
creation/audit timestamps, explicitly retired IDs' validity timestamps and
labeled CLI timestamps. Retirement validity must also equal its audit timestamp.

```powershell
python test/Harness.Compatibility/Memory/contracts.py --oracle artifacts/compatibility/oracle/harness.exe --candidate src/Harness.Cli/bin/Debug/net10.0/harness.dll --output artifacts/memory-tools-differential.json
dotnet test --solution Harness.slnx --no-restore -bl:artifacts/memory-tools-tests-unique.binlog --report-trx --results-directory artifacts/memory-tools-results
```

Set `HARNESS_GO_STORAGE_ORACLE` and `HARNESS_GO_MEMORY_ORACLE` as described in
[memory storage](memory-storage.md) to enable both live Go test bridges. Local
Windows sandbox restrictions can prevent the original Go executable from
resolving temporary Git paths; the differential runner then needs normal
filesystem access. All its repositories, homes, databases and config are synthetic
and temporary. Native Linux/macOS and actual Claude Code/Codex UI dogfooding
remain release checks. Detailed evidence is recorded in
[memory-workflows-validation.json](inventory/memory-workflows-validation.json).

Acceptance validation passed 143 .NET tests with one Unix-permissions skip on
Windows. All 199 memory workflow checks and 43 bootstrap transport regressions
passed against both the managed build and Windows self-contained executable.
Strict Release build, locked restores and publish completed without warnings.
