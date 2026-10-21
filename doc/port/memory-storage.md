# Memory storage and layering

TASK-0008 / PORT-008 implements the memory domain in `Harness.Core/Memory` and
the SQLite repository in `Harness.Infrastructure/Memory`. It uses the existing
schema and Microsoft.Data.Sqlite dependency. Public MCP tools, proposals, human
review and discovery/opening of wider-level database files belong to TASK-0009.

`MemoryStore` wraps a caller-owned `HarnessDatabase`; it never disposes that
database. Each operation owns its connection. Put, reaffirmation, supersession
and retirement use an immediate transaction, including their audit events.
There is no schema migration or package change in this task.

## Domain and applicability

The seven types are goal, constraint, decision, fact, open_question, warning and
convention. Content is trimmed and limited to 4,000 UTF-8 bytes. Source defaults
to agent/unknown, with source_ref falling back to the actor. IDs retain the
`mem_` prefix and eight random bytes encoded as 16 lowercase hexadecimal digits.
`MemoryTimestamp` preserves the original nine fractional digits; validity is
inclusive at valid_from and exclusive at valid_until.

Scopes normalize paths with slash semantics on every OS, without filesystem
access. Paths must remain relative and inside the project; component boundaries
and case are significant. Project knowledge applies everywhere; paths apply to
themselves and descendants; subsystem and branch scopes match by name. A query's
branch additionally includes its branch-scoped memories. Workspace/user memories
are stored at project scope in separate databases, then relabeled when read.

`MemoryLayers` receives the project store and wider stores in precedence order
(workspaces, then user). It returns memories plus any wider-level read errors;
a project read failure propagates. Stores remain caller-owned. Lookup returns
the owning store and its displayed scope. Cancellation propagates promptly
between storage operations. Synchronous native SQLite work retains the configured
busy-timeout limit described in [storage implementation](storage-implementation.md).

## Preserved ordering and lifecycle rules

| Operation | Go-compatible behavior |
| --- | --- |
| Exact duplicate | Same type, scope and SHA256 of lowercase, whitespace-collapsed, trailing-punctuation-trimmed content. Update updated_at and append reaffirmed; retain original content, provenance and validity. Expired/future memories still qualify. This check precedes supersession-target validation. |
| Near duplicate | FTS5 limits candidates to 25 of the same type, with no superseded_by and an unexpired valid_until. Ignore scope and valid_from; future memories can qualify. Significant words use the original stopwords, UTF-8 length and suffix rules. Jaccard similarity >= 0.5; return the best five with stable tie order. Return needs_decision without writing unless forced or explicitly superseding. |
| Supersede | Default type/scope from the predecessor. Validate targets and atomically retire their uniqueness entries before inserting the successor; deferred foreign keys are checked at commit. Keep actor, reason, related ID and source_ref events. |
| History | Walk the containing chain oldest first, detecting cycles. With multiple predecessors, follow the most recently created predecessor, as Go does. |
| Retire | Require a reason and a currently applicable memory; set valid_until to now and append retired. Keep all rows and history. Reaffirming a retired duplicate does not reactivate it. |
| Search | Existing SQLite porter/unicode61 FTS5 and bm25, with any-word matching; no query words means no results. Otherwise newest updated_at first. For scoped queries without text, rank applicable paths deepest first, then subsystem/branch, project, workspaces and user, before limiting. Default limit 20. |
| Wider overrides | Search each level with the same per-level limit, clearing branch for wider stores. Find a same-type near duplicate in narrower stores, even outside the query's scope. Return that narrower memory with labeled overrides, deduplicating by ID. Inactive-inclusive queries bypass overrides. Preserve supplied workspace order. |

The unfiltered override and future-dated similarity rules are compatibility
behavior, rather than new scope policy. Changing them requires an explicit
compatibility decision.

The Go 1.27.1 oracle uses Unicode 17.0.0. Host .NET casing/classification can
use an older Unicode version, and invariant casing differs for dotted I.
`GoUnicode.cs` pins the oracle's 1,488 lowercase mappings as 186 ranges and its
736 letter/digit ranges. Binary searches keep normalization independent of host
ICU/NLS data. The deterministic generator lives beside the Go test bridge;
the original MIT and Go BSD notices are retained and embedded in Core.

## Credential rejection and API boundaries

The original redaction rules cover private keys, URL passwords, authorization
values, known token formats, JWTs and secret-named assignments. Content validation
rejects matches before opening a transaction. Rejection errors do not echo the
input. ASCII word boundaries and regexp whitespace match Go explicitly, including
non-ASCII characters next to tokens. Regex operations have a timeout. The guard
is the original content heuristic; source_ref, actor and audit reasons retain
their original values. It does not claim to identify every possible credential.

Strict UTF-8 validation also rejects malformed .NET surrogate strings and
confidence rejects non-finite numbers. These .NET API guards supplement the
original finite 0..1 and byte-size rules. Domain records are transport-independent;
TASK-0009 must project the Go wire views (RFC3339 seconds, optional fields and
omission of updated_at), rather than serialize these records directly.

## Validation and reproduction

Unit/integration tests cover scope normalization, component boundaries, byte
limits, provenance defaults, validity endpoints, reaffirmation, near duplicates,
same-word supersession, retirement, rollback, FTS queries, branch/specificity
ordering, partial layer failures, cancellation and concurrent writers using
independent database factories.

The test-only Go bridge imports the unmodified memory/storage/redact packages
from commit `9cb401dd2de4f85dd089fea86a0b139d81b178c6`. It seeds synthetic
databases, reports records/searches/similarity/history/events, and reads them
again after C# updates. It also provides layered conflicts, expired and future
knowledge, Unicode tables and credential fixtures. No personal database is read.

```powershell
./test/Harness.Integration/Memory/Go/build-oracle.ps1 -Go C:/path/to/go1.27.1/bin/go.exe
$env:HARNESS_GO_MEMORY_ORACLE="$PWD/artifacts/memory-oracle/memory-oracle.exe"
$env:HARNESS_GO_STORAGE_ORACLE="$PWD/artifacts/storage-oracle/storage-oracle.exe"
dotnet test --solution Harness.slnx --no-restore -bl:artifacts/memory-tests-unique.binlog --report-trx --results-directory artifacts/memory-results
dotnet build Harness.slnx -c Release --no-restore -m -p:ContinuousIntegrationBuild=true -bl:artifacts/memory-release-unique.binlog
```

The bridge builder requires a fresh output directory and accepts
`-OutputDirectory` for repeat builds. Unicode regeneration takes JSON from
`memory-oracle unicode unused`, then runs
`python test/Harness.Integration/Memory/Go/generate-unicode.py <json-path>`.
Oracle integration tests explicitly skip when the corresponding executable
environment variable is absent; acceptance runs require both oracles.

The [validation receipt](inventory/memory-validation.json) records the final
test counts, hashes and commands. Native Linux/macOS execution remains a release
check. MCP wire behavior for memory is not claimed by this storage task.
