# Deterministic context retrieval

TASK-0017 / PORT-017 ports the gathering, normalization, merging and ranking
pipeline from `internal/retrieval` at
`9cb401dd2de4f85dd089fea86a0b139d81b178c6`. Core owns the algorithms and accepts
provider, memory-search and recent-change contracts. Infrastructure supplies
Git commit queries and request-scoped physical file bodies. No package, CPM
version, database migration, model service or search index is added.

Public `get_context`, answer budgeting and rendering are implemented separately
by [PORT-018](context-budgets.md). The retrieval contracts are usable by that
composition without starting a host.

## Gathering

Task parsing prioritizes up to five code identifiers, then capitalized names
in prose. Issue IDs, acronyms, abbreviations and sentence-start prose are
excluded. Up to three adjacent content-word pairs suggest code/file names;
snake_case names also try camelCase and PascalCase spellings.

Providers first receive the task sentence for symbol search and context.
Named symbols then receive symbol searches; only spellings with a matching
definition expand into callers and impact. Qualifiers distinguish namesakes.
Phrase searches try symbols and files, with a head-noun file fallback. More
than three phrase hits in one call lose their phrase boost. Test files are
discounted unless the task concerns tests or names their definition exactly.

The best reported provider positions select up to ten unique paths for
applicable decisions, conventions, warnings and constraints. Those searches
carry the current branch. Task-content memory searches and project goals are
queried separately. Duplicate memory IDs retain their first provenance and
strongest scope score. The best three paths also select up to five recent
non-merge Git commits, with reciprocal recency weights.

## Identity, merging and ranking

Definitions and literal matches identify code by file/line, relations by
file/name, and locationless symbols by qualified name/name. Memory and Git
identities include their original IDs. Sizes count UTF-8 bytes; unavailable
bodies use the source estimate of 45 bytes per known line.

Merging preserves first-seen order, all distinct provenance reports, strongest
evidence, best score components, known text/spans and the more direct role.
Single-line reports inside a definition span fold into that definition; reports
with their own multi-line span remain independent. Input candidates are not
modified by merging.

Named definitions occupy tier zero. Tier one combines role weights, reciprocal
provider positions, relation depth, phrase/name signals, documentation/test
discounts, memory scope, Git recency, distinct-provider corroboration and inferred
evidence. Each score has a human-readable explanation. Equal scores use the
source's UTF-8 lexical identity/provenance ordering; exact equal keys preserve
input order. Provider registration and returned-item order remain observable.

## Responsiveness and file reads

Code gathering shares a ten-second default deadline. A failing provider is
skipped for the remaining answer; unindexed providers produce notes. Completed
peer results survive a slow provider's deadline, and memory/Git gathering can
continue. Caller cancellation propagates. Discovery and capability metadata are
also bounded, with late task faults observed.

Context-aware registry discovery filters exclusions **before** capability reads
or handshakes. Skipped providers are filtered before discovery on ordinary
registry calls as well. This corrects the source's ability to start a provider
while checking whether it should be excluded. Explicit full discovery and
capability inspection retain their existing behavior.

One `FileReadCache` instance belongs to one answer. Concurrent reads of a path
share one load, including its failure; a new request gets fresh contents. Bodies
preserve source line separators and clamp the requested end line. Invalid
start lines fail. Physical reads require valid UTF-8, relative paths within the
repository, and resolved file/directory links that remain inside it. Limits are
4 MiB per physical file and 32 MiB total loaded bytes per cache. These bounds
prevent provider-selected bodies from consuming unbounded memory. Link checks
precede opening and do not provide atomic protection against concurrent hostile
filesystem replacement.

## Validation and reproduction

The frozen-Go driver imports the original retrieval package without copying or
rewriting its algorithms. Eight differential scenarios compare normalized
candidates, complete gathering results, merge/fold reports, tiers, scores,
explanations and ordering. Only measured call latency is omitted; nil source
collections are represented as empty collections by the driver. Additional C#
tests cover deadlines, late faults, exclusions, memory/Git query selection,
cache concurrency/errors and physical file containment. Integration fixtures
use synthetic repositories and databases.

Build the oracle from the source captured by PORT-002:

```powershell
python test/Harness.Compatibility/Retrieval/build_oracle.py --source artifacts/compatibility/oracle/source --go artifacts/compatibility/toolchain/go/bin/go.exe --modules artifacts/compatibility/oracle/modules --output artifacts/retrieval-oracle/retrieval-oracle.exe
$env:HARNESS_GO_RETRIEVAL_ORACLE="$PWD/artifacts/retrieval-oracle/retrieval-oracle.exe"
dotnet test --project test/Harness.Unit/Harness.Unit.csproj --filter-class 'Harness.Unit.Retrieval.*' -bl:artifacts/retrieval-unit.binlog
dotnet test --project test/Harness.Integration/Harness.Integration.csproj --filter-class 'Harness.Integration.Retrieval.*' -bl:artifacts/retrieval-integration.binlog
```

Add `--goos linux` and a separate output path to build the Linux oracle. Run the
portable test DLLs with the Linux runtime for the Unix symlink case. Windows
skips that case. Numerical performance budgets remain PORT-030 work.

Exact validation counts, commands, hashes, platform coverage and unchanged
package locks are recorded in
[context-retrieval-validation.json](inventory/context-retrieval-validation.json).
