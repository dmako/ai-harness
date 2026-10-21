# Finding snapshots and author review

TASK-0023 completes the architecture findings lifecycle and adds
`architecture_findings` and `architecture_propose`, bringing the MCP surface to
all 30 captured tools. The schemas come unchanged from the pinned Go capture.
The implementation uses the existing SQLite migrations and packages; no CPM or
lock-file changes are needed.

## Snapshots and drift

`FindingStore` reads and writes the existing `findings`, `finding_snapshots`,
`finding_marks` and `finding_proposals` tables. A first sync records even an empty
snapshot. Later snapshots are created only when findings arrive, disappear or
change. Unchanged syncs refresh evidence locations without resetting statuses:
“new” continues to mean new since the last meaningful change. Reappearing
findings become new again; task marks remain available when their finding is
gone. Duplicate keys merge evidence and retain the largest measure.

The persisted SHA-256 prefix matches Go's UTF-8 JSON encoding, including HTML,
control-character and supplementary Unicode escaping. Hashes ignore evidence
line numbers and ordering, and exclude suggested fixes, ranks and source
metadata, as in Go. Cross-runtime tests use one database in both directions.

`KnowledgeDrift` checks current project constraints, decisions and conventions:
missing scoped paths, missing source paths, and absent quoted code names.
Missing paths are facts; absent names are qualified heuristics. Every result
retains the memory ID, scope, type and specific stale reference. Boundary
findings continue to reference their recorded rules. A `memory_propose` with
`supersedes` offers a correction without changing active knowledge immediately.

`FindingSets` supplies project/workspace reads and synchronization. Quick project
reads check current build files and boundary rules, then reuse sync-only clone,
provider, published-package-use and stale-knowledge evidence. They never rerun
provider calls or the drift corpus scan. Sync refreshes those sources and each
workspace's system evidence. Failed/unavailable providers retain their last
findings; a healthy empty answer removes them. Workspace snapshots and marks
live in the workspace knowledge database. Workspace read paths open existing
knowledge read-only and do not create a database.

## MCP and author control

`architecture_findings` preserves brief/full shapes, severity summaries,
key/kind/path/member filters, `hide_marked` and opt-in gone findings. Hiding marks
hides only `by_design`; `review` and `task` stay visible. Optional snippets use
contained, bounded text reads, at most 30 lines per place and 8,000 code bytes
including the original per-place overhead. `get_context` attaches only touching
stored findings and their marks, while preserving its serialized UTF-8 budget.

`architecture_propose` only writes pending proposals. Finding-backed proposals
use current evidence and basis; observations require the agent's title, reason
and evidence and retain basis `observation`. Matching pending finding proposals
are deduplicated. Nothing creates a task, mark or decision at proposal time.
The response instructs the agent to tell the user that review is pending.

`FindingReview` and `FindingSets.ApproveAsync` provide author-side operations for
the later UI adapter. Approval rechecks the finding and workspace membership;
gone findings cannot be approved. By-design approval records a decision memory
and the author's mark, preserving proposer attribution. Restating a reason
supersedes the prior decision; removing a mark or making a task retires it.
Task approval imports an `ARCH-` task with evidence and acceptance criteria.
Workspace approval keeps marks/decisions in workspace knowledge and puts the task
in the approving project's database. Rejection only closes the proposal with
its note and audit fields. Decided proposals cannot be decided again.

By-design findings return for review when severity rises, or their measure grows
by at least two and at least 50 percent. Reasons, original level/measure and
proposer/author timestamps remain available for review.

HTTP inbox/triage routes and frontend wiring remain PORT-027/028. No author-side
approval or triage tool is exposed over MCP. As in the source, cross-database
workspace approval is a sequence of durable operations rather than a distributed
transaction; this task does not claim crash-atomic commits across those stores.

## Validation and limits

The receipt in [findings-validation.json](inventory/findings-validation.json)
records commands, results, source/artifact hashes and platform coverage. Focused
tests cover persistence/reopening, growth thresholds, proposal validation,
approval/rejection/audit, supersession, task creation, workspace membership,
provider failure versus healthy-empty answers, filtering, snippet budgets and
context budgets. A thin Go driver exercises the original storage and drift
algorithms. Public MCP tests compare seven read shapes with the original server,
including marked and gone cases. The original Go findings package also passes.

Windows runs the whole regression suite; Linux/WSL runs all new tests with native
Go oracle/server binaries. macOS is not exercised. The diagnostic excerpt for
long knowledge text clips at a valid UTF-8 scalar boundary; Go can cut through a
multibyte scalar at byte 160. Source reads retain the existing containment,
strict UTF-8 and size limits instead of allowing drift scans to follow arbitrary
links. These bounded-reader differences affect malformed/untrusted evidence,
not normal finding contracts. There is no new compiler analysis or task creation
without author review.
