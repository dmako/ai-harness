# C# port compatibility policy

This policy defines the acceptance baseline for the implementation tasks. An
intentional difference must have a documented reason, regression scenario and
reviewed acceptance evidence before the release is described as compatible.

## Public names and defaults

Preserve `harness`, existing executable/command/alias names, flags, environment
variables, client entry keys, MCP tool names, provider capability identifiers,
HTTP method/path patterns and snake_case JSON property names. C# naming rules
apply to implementation symbols; serialized names remain explicit.

Preserve dispatcher semantics: help on stdout; operational/usage diagnostics on
stderr; exit codes 0/1/2; flags interspersed with positionals; `--` ends flag
parsing. Preserve command-specific exceptions such as best-effort hook ingestion
and automatic telemetry sends that exit zero on failure. Interactive bare
setup commands and noninteractive commands keep their distinct behavior.

Missing configuration remains optional, but an existing malformed file or
unknown configuration key is rejected before side effects. Preserve
defaults/global/project precedence, complete replacement of project provider
tables, pack overlays, and the documented environment override. Shared .NET
hosting defaults must not silently introduce new configuration precedence.

Keep current context/search defaults and limits, light versus deep provider
startup, task lease behavior, proposal review semantics and read-only versus
mutating operations. Preserve `docs/backlog.yaml` as the Go CLI's legacy default
input path when operating on existing repositories. This C# repository's own
documentation belongs in `doc`; tests belong in `test`, benchmarks in
`benchmarks`, and product code in `src`. Any new default-path behavior needs
an explicit compatibility decision; do not silently rewrite existing paths.

## Identity and time

Preserve stored project/worktree/memory/task/proposal/finding/workspace IDs and
their lookup/normalization rules. New opaque IDs may differ in value but retain
the source format and uniqueness properties. Never substitute .NET GUID strings
for established prefixes or regenerate imported IDs. Claims and tokens are
session-owned secrets: compare their relationships and rejection behavior,
never normalize away a token ownership failure.

The storage timestamp format is UTC
`yyyy-MM-ddTHH:mm:ss.fffffffffZ` (nine fractional digits). C# must preserve
existing nanosecond-precision text even where its native time types have lower
precision. Accept the legacy variable-width RFC3339 timestamps the Go readers
accept. Memory API date arguments accept RFC3339 or `yyyy-MM-dd`. JSON time
encodings must follow the actual view type, not a blanket replacement with the
storage format. Localized CLI display times remain a separate presentation
contract. Source: `internal/storage/storage.go`, `internal/mcp/server/memory_tools.go`.

## JSON schemas and response envelopes

Preserve required/optional fields, property types, omitted defaults, explicit
nulls, empty arrays, enum strings and nested shapes. A nil Go slice may encode
as `null` unless the source explicitly creates an empty slice; a property with
`omitempty` may disappear. Do not globally force all null collections to `[]`,
or omit every null/default value, in .NET serialization.

MCP success, protocol errors and tool errors are distinct. Preserve structured
content and text content, `isError`, schema validation before handler execution,
and argument hints. `task_complete.validation` accepts strings or check objects;
its hand-written schema permits a null or array value. Missing acceptance may
be valid in the schema but fail domain completion checks when the task has
criteria. The exact source override is in `inventory/mcp.json`.

Keep stdio newline-delimited JSON-RPC separate from logs. Malformed JSON gets
`-32700`; structurally invalid requests get `-32600`; the session continues.
Preserve bounded inbound frames and the up-to-30-second drain of in-flight
requests after stdin EOF. Test SDK differences explicitly: choosing the official
C# SDK does not by itself reproduce the Go transport wrapper.

HTTP error status, JSON error bodies, write validation and authorization checks
are part of the existing frontend contract. Do not replace them wholesale with
ProblemDetails during the compatibility port. Preserve loopback Host validation,
CSP, POST-only JSON writes, `X-Harness-UI: 1`, and the required same-origin
`Origin`. Source: `internal/gui/gui.go`, `internal/gui/write.go`.

## Bytes ordering and comparisons

Go string lengths count UTF-8 bytes. Use UTF-8 byte counts for input limits,
memory size limits, provider response sizes and context packing. Context has an
8000-byte default, a four-byte estimated-token conversion, 75 percent soft body
share and a default maximum body size of one quarter of the budget. Preserve
the actual response-envelope accounting from `context_tool.go`; source budget
comments alone do not define serialized size. Test emoji, non-Latin text,
escaping, clipped bodies and minimum budgets.

Compare JSON objects structurally without depending on key order. Preserve
array order and deterministic tie breaks unless a particular contract explicitly
defines a set. Use ordinal comparisons for identifiers and deterministic
rank/order behavior; do not introduce culture-dependent .NET sorting. Preserve
rank fusion, provenance, omitted-item counters and stale/partial-result warnings.

PORT-002 may normalize only enumerated nondeterminism: sandbox root paths,
generated IDs through a consistent bijection, controlled clocks, build metadata,
ephemeral listening ports and measured durations. It must keep field presence,
array order, numeric values, path relationships, identity relationships, errors,
permissions and side effects observable. Never scrub an entire payload merely
because it differs. CLI human-readable layout and OS-specific system error text
can have targeted expectations; parser behavior and machine output remain strict.

## Storage and concurrency

Keep all 19 original SQL migrations and the `schema_migrations` ledger, STRICT
tables, transactions, foreign keys, WAL, busy timeout and pre-migration backup
rules. Reject an unknown newer schema without modifying it. Status/doctor and
provider facade reads must preserve their source no-create/read-only behavior.
Persist claims, audit events, scope history and sync state atomically.

Run migrations, cross-process races and rollback tests on disposable copies or
databases created from the pinned SQL. Never open personal state as a fixture,
copy a live WAL database with a plain file copy, or infer safe concurrent access
solely from single-process tests. Core operation stays local; developer telemetry
export remains explicitly opt-in and redacted.

## Performance and Windows corrections

Optimize MCP calls and repeated page-load/live-update/sync actions first.
Independent UI sections load with their own indicators; a slow rare operation
does not block unrelated sections. Keep provider startup lazy, light workspace
queries bounded, per-request reads cached and telemetry off the request path.
Record performance baselines in PORT-030 before selecting numerical regression
limits; this task establishes no invented latency target.

The pinned Windows implementation has no-op advisory locks
(`internal/fsutil/lock_other.go`). The source README also lists provider
process-group cleanup and stale graphify cleanup limitations on Windows;
validate these against `internal/mcp/client` and `internal/graphify` rather than
assuming the Unix process lifecycle applies. Git hooks still require a shell supplied
by Git for Windows. The port may correct locking, process ownership/termination,
idle cleanup, argument quoting, permissions and architecture targeting when the
implementing task records the divergence and platform-specific validation.
Preserve unrelated user files, manually started sessions and existing data.
Do not simulate a known race or orphan-process defect merely to match Go.

These Windows corrections are allowed categories for implementation review;
they are not evidence that any correction has already been implemented or tested.
