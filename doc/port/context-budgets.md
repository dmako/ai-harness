# Serialized context budgets and get_context

TASK-0018 / PORT-018 exposes `get_context` using the retrieval pipeline from
PORT-017. Core owns budget packing; Mcp owns the wire representation and exact
serialized-size accounting. Infrastructure supplies current project knowledge,
recent Git commits and request-scoped file readers. No packages, CPM versions
or SQL migrations change.

## Inputs and packed content

The default budget is 8000 bytes. Positive `tokens` takes precedence over
`budget`, at four bytes per estimated token. A positive budget below 1000 is
raised to 1000 with a note. Nonpositive budgets use the default. Integer range
and token multiplication overflow are rejected. Task text is trimmed and must
be nonempty; paths must be relative and cannot contain `..`. Scope and depth
accept the frozen schema's project/workspace and light/deep values.

The envelope reserves space for task, symbols, errors, freshness, collection
fields, area counts, notes and numeric counters. Each candidate costs the JSON
it adds, including escaped bodies and new file/area overhead. Packing preserves
the source's ranking order, best-first file grouping and omission count.

Bodies use at most 75 percent of the remaining packing budget. The default
maximum individual body is one quarter of that budget. Oversized bodies become
drill-down references; named definitions in tier zero can retain a whole-line
head with the original clipping marker. A body whose escaped JSON would exceed
the remaining budget also becomes a reference. Missing/unreadable bodies retain
their known snippet or a reference. Long memory statements already visible in
an included body use `see` instead of repeating the statement.

## What the byte budget measures

`used_bytes` equals the UTF-8 length of the final JSON text in the MCP text
content. The structured content describes the same JSON value. The budget
covers that answer payload, including debug when requested; it does not count
the surrounding JSON-RPC frame or the protocol's duplicate representations.

The context serializer follows Go JSON escaping: quotes, backslashes, control
characters, HTML characters and U+2028/U+2029 are escaped, while ordinary
non-ASCII text and supplementary characters remain literal UTF-8. Object keys
use UTF-8 lexical ordering. Measuring and sending share that serializer, so
UTF-16 `string.Length` and a serializer's extra surrogate escapes cannot change
the budget calculation. The self-referential `used_bytes` digit count converges
before the answer is returned.

The pinned Go handler measures before adding debug. This port intentionally
counts debug in the final answer. If full diagnostics do not fit, it retains a
deterministic prefix and says they were truncated. Oversized optional notes or
errors are omitted with a diagnostic note; lower-ranked packed units can be
removed as a final safeguard, increasing `omitted`. Task metadata that cannot
fit produces an explicit error asking for a larger budget or shorter task,
rather than returning an oversized answer or silently changing the task.

Ordinary answers preserve the Go envelope/cost/packing decisions. A debug answer
that fits preserves all Go debug entries, with corrected `used_bytes`. Debug
candidate provenance, fates, fold reasons and score explanations remain
available within the declared budget.

## Project composition

`get_context` reads existing project state and wider knowledge without
initializing an uninitialized project. It includes pending memory proposal
notes, applicable memories and recent commits. Every request gets a fresh file
reader/cache. Source bodies stay within the containment and byte limits
documented in [context retrieval](context-retrieval.md).

Freshness is checked after gathering, including when a branch changes while a
provider is answering. Indexed candidates from another branch are withheld;
live candidates remain available. Directory hits from file search are omitted.
Providers excluded from context are filtered before discovery or handshakes.
Caller cancellation propagates through gathering and body reads.

Workspace context remains PORT-019 work and currently returns the existing
workspace-not-configured error. Architecture findings and their proposal notes
remain the architecture tasks; context hit-rate persistence remains the later
telemetry work. The frozen schema is advertised unchanged, including these
optional future sections.

## Validation and reproduction

The context oracle temporarily adds an export-only bridge to the frozen server
package, then removes it even on build failure. The bridge calls the original
handler; it does not replace retrieval, packing, wire costs or envelope logic.
Six packing scenarios and five handler scenarios compare the port to that
source, including Unicode, HTML, quotes, backslashes, controls and line
separators. Debug comparisons exclude only measured latency and the known Go
`used_bytes` defect. Additional hard-budget tests verify bounded truncated debug
and oversized diagnostics. PORT-017's merge/rank comparisons run alongside them.

```powershell
python test/Harness.Compatibility/Context/build_oracle.py --source artifacts/compatibility/oracle/source --go artifacts/compatibility/toolchain/go/bin/go.exe --modules artifacts/compatibility/oracle/modules --output artifacts/context-oracle/context-oracle.exe
$env:HARNESS_GO_CONTEXT_ORACLE="$PWD/artifacts/context-oracle/context-oracle.exe"
$env:HARNESS_GO_RETRIEVAL_ORACLE="$PWD/artifacts/retrieval-oracle/retrieval-oracle.exe"
dotnet test --solution Harness.slnx --filter-class '*Retrieval*' -bl:artifacts/context-focused.binlog
```

Build with `--goos linux` and a separate output to use the oracle with portable
test DLLs on Linux. Real stdio tests verify schema parity, text/structured
agreement, validation failures, lazy state and initialized memory/body/Git
composition. The published smoke test creates its own Git repository and
isolated global configuration, so it cannot inherit the checkout's project
state. It also configures an excluded heavy provider with a nonexistent
executable to verify that it stays unstarted.

Commands, results, platform coverage and hashes are recorded in
[context-budget-validation.json](inventory/context-budget-validation.json).
