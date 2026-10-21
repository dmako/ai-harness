# Repeated code and change coupling

TASK-0022 implements function-body comparisons, repeated statements, interface
candidates, workspace copies and the public `change_coupling` MCP tool. There
are now 28 implemented public tools. Everything uses the BCL, existing provider
contracts, SQLite knowledge readers and external Git; no package or lock-file
changes are required. The shipped executable requires neither Go nor Python.

## Clone evidence

`CloneExtractor` reads Go, C#, TypeScript and JavaScript function declarations.
Its bounded token reader follows the source implementation's declaration/body
shapes and tracks comments, quoted/verbatim/raw/template strings and JS regexes.
Go receivers, parameters/results, local bindings and package type names supply
the canonical forms. C#/TS declarations retain signatures, override flags and
names captured by closures. Expression-only members and declarations outside
the supported shapes are omitted; this is not compilation or a symbol index.

| Evidence | Rules retained from Go |
| --- | --- |
| Canonical bodies | Own parameters/locals renamed by first use, numeric tokens normalized, member/type/string names retained; Go package types qualified by directory |
| Identical copies | At least 12 canonical tokens; canonical equality is a fact of this normalization, not literal source equality |
| Near copies | At least 30 tokens, similarity at least 0.85; token-run candidate selection, LCS alignment and exclusion of different logic/types/members/templates |
| Templates | Shapes at least 0.85 alike, normally three places or two bodies of at least 100 tokens; separate heuristic findings, never confirmed reusable copies |
| Small helpers | Bodies up to 20 tokens rank lower unless they contain a decision; standard-library equivalents and wrappers get the original inline/use-library suggestions |
| Repeated sequences | At least two working statements and 14 tokens when holding data, otherwise 21; normally three places, or two with five statements and 50 tokens; common seeds capped at 400 places |
| Interface candidates | Name families, variant/verb filtering and Go dispatch evidence; explicit name-based heuristic confidence and original ranking |

Nested functions and overrides of the same member are not duplicate pairs.
Statements exclude cleanup/error-check idioms, repeated calls to one function,
already-reported copy/template groups and overlapping runs. Numeric differences
in repeated sequences remain visible and lower their basis to heuristic. Stable
keys, measures, ranking, source attribution and evidence paths/lines match the
frozen outputs.

`RepeatedCodeReader.ProviderDuplicatesAsync` retains whether any provider
answered, including a healthy empty response. A later failed/unavailable
provider therefore need not erase a stored finding (storage integration is
TASK-0023). Providers' supported-language pairs are checked against local bodies:
missing bodies, trivial methods, overrides and templates are withheld. Unsupported
languages keep the provider's own attributed heuristic. Pairs already in a local
copy group are counted once. Test/test pairs and generated content are excluded.

`WorkspaceDuplicateReader` compares public same-named declarations and normalized
whole-file content across members, retaining extends-only/same-name-only results
separately from copied code. Identical file groups require eight normalized lines;
at most 300 groups are returned. Shared-code findings suggest an existing holder
library, a holder used by the others, an already-used library, another workspace
library or a new library, in that order. Up to three applicable workspace
decisions/conventions accompany the home. Similarity and the suggested home are
evidence to inspect, not automatic refactoring or task creation.

## Co-change MCP behavior

`change_coupling` preserves the captured Go schema and dispatch precedence:

- `file` returns provider partners; only its leading `./` is removed.
- `check` uses supplied files, otherwise Git's staged/unstaged/untracked paths,
  and reports usual partners absent from the change.
- Otherwise it returns source-file hotspots over 180 days, with provider partners.

The default limit is 10. Partners merge by path, retaining the largest shared
commit count, then sort by count, share and path. A usual partner needs three
shared commits, share at least 0.5 and inferred history of at least 4.5 commits
(the original tolerance for the five-commit threshold). Check queries at most
30 changed code files, up to 20 partners each. Missing partners must still exist.

Hotspots count non-merge commits since a UTC timestamp at second precision,
exclude tests/generated content and removed paths, and require more than one
commit. Renames retain Git's reported path counts rather than transferring old
history to the new path. Partner requests retain provider/tool attribution.
Missing providers produce the original unknown-partners note. These reminders
do not prove a file needs changing.

## Caches and boundaries

Each request owns its repository lists, successful/failed text reads, manifest
evaluations, function forms and normalized workspace bodies. `ArchitectureReader`
shares this evidence across project, repeated-code and system passes. Nothing
persists these derived caches across calls: a new request sees uncommitted edits,
new files and changed references. Git continues to decide ignored/tracked paths.

Clone inputs are limited to 1 MiB per file, and existing strict UTF-8 and
repository/symlink containment apply. Snippets now locate the containing clone
function by default and preserve bounded fallback context and span focus.
Unlike the original unrestricted `os.Stat` existence check, co-change existence
checks withhold outside-repository paths. No provider claim becomes compiler
certainty from a matching name. LCS comparison remains potentially expensive for
large near-identical bodies; it is sync/explicit analysis work rather than a
page-load or every-query background scan.

## Validation

`Clones/capture_fixtures.py` copies the original synthetic clone tests into an
isolated temporary package and instruments only their input-writing helper.
The frozen oracle tree is unchanged. It captures 22 input sets and reruns the
original tests. The source's `TestSequences` assumes an order for a Go map; the
capture retries that known order-sensitive assertion, never changes analysis.

Differential tests compare extraction metadata, private canonical/shape/signature
forms, copies/templates/sequences/contracts and generated finding text/rank for
all 22 sets. Additional C# raw-string/generated-content and TS closure/regex
fixtures bring this to 72 comparisons. Multi-repository fixtures compare copies,
inheritance, shared homes and unchanged databases. Git fixtures exercise dates,
renames, hotspots, provider merging and missing-partner checks. Public SDK tests
check schemas, dispatch precedence, dirty paths and text/structured equality.

Commands, platform coverage and hashes are in
[clones-validation.json](inventory/clones-validation.json). Finding snapshots,
drift, marks, proposal lifecycle and architecture MCP reads remain TASK-0023.
