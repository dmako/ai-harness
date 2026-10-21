# Architecture readers and finding generation

TASK-0021 ports file-based architecture analysis and recorded boundary rules.
It reuses TASK-0020's request-scoped file/manifest cache and adds no packages,
build execution, symbol index or provider process to file-based analysis.

## Readers

| Reader | Supported evidence and findings |
| --- | --- |
| .NET | csproj references/packages, sln/slnx membership; dependency layers, strongly connected cycles, production references to tests, missing references, unused projects/packages, projects outside solutions and differing solution membership |
| Go | Root-module package imports, nested-module/test/vendor filtering; common dependency findings, name-based package roles and imports across a nested internal-package boundary |
| TypeScript/JavaScript | Workspace packages or source/feature folders, resolved imports and aliases; common dependency findings, name-based roles and imports into another feature's files instead of its index (three evidence samples per pair) |
| Workspace system | C# ServiceContract interfaces, proto declarations, multiline/primary-constructor implementations and uses; appsettings addresses and declared database names, front-end environment addresses, messaging dependencies/topics, EF context identities and tenant/connection-key detail |

System findings include synchronous call cycles, chatty gRPC links (eight
contracts or twenty uses), unserved contracts, contracts without workspace
clients and shared configured database names. Service layers collapse cycles
as the project analyzer does. Test/generated/mock/sample/example code and test
environment configuration are excluded. Database nodes come from context code;
connection-string database names only supply shared-database evidence. Findings
never include connection strings, credentials or configured URL values.

Dependency graphs are facts of the supported static file readers, not proof
that a compiler resolved every conditional/import. Name-based layering is a
heuristic with confidence 0.6. Chatty links, unused contracts and shared database
names retain the frozen source's heuristic basis and confidence. Every finding
retains its source, severity, evidence, stable key and measure; provider cycles
retain their provider/tool and supplied confidence. Published-package usage is
unknown outside a workspace or when a peer is unreadable. No missing-provider
response is treated as an empty successful analysis.

## Recorded rules and provider coverage

`BoundaryRules` parses `group`, `layers`, `forbid` and `only` statements from
constraints, decisions and conventions. Other prose is ignored. A malformed
rule produces `rule-unclear`, rather than an inferred rule. Patterns match names
or paths case-insensitively; bare names also match the last name/path segment.
The project's groups take precedence over workspace groups, then user groups;
all rules apply and retain memory ID, scope, type and original rule line.
Constraint violations are problems; decision/convention violations are warnings.
An applicable explicit layers rule replaces the name-based layering heuristic.
Wider knowledge is opened read-only and is never created by an analysis read.

`ArchitectureProviders` reports configured or running coverage without requiring
discovery and adapts successful `arch.cycles` responses. Healthy empty responses
remain distinct from unavailable or failed providers. As in Go, there is no
compiler-level dead-code reader: only unused project/package evidence. Duplicate
and co-change generation belongs to TASK-0022. Finding storage, sync integration,
drift, marks/proposals and public architecture MCP tools belong to TASK-0023.

## Evidence and request cost

`EvidenceSnippets` parses file/member sites, bounds snippets to 120 lines and
2 MiB text files, lists folders with directories first, preserves span focus and
refuses absolute/outside/symlink-escape paths. A containing-function resolver
can supply the clone reader's function range; without one the reader shows five
lines around the evidence, or twenty opening lines. TASK-0022 supplies function
extraction rather than duplicating that parser here. Non-file evidence such as
memory IDs and communication arrows does not become a file path.

`ArchitectureReader` shares repository/build reads across project and system
analysis. Quick project reads omit workspace package-use scans. Full reads
check package references, never public declarations as substitutes. Unknown or
unreadable members produce partial notes, not invented negative usage findings.
File reads and text failures are cached only for the request. SCC/dependency
analysis does not start external programs; repository discovery uses Git.

## Validation

The Architecture compatibility driver calls the unmodified pinned Go analyzer,
rule checker, system reader, provider coverage and snippet reader. Differential
fixtures cover .NET solution/unused/reference findings, cycles, layering rule
overrides, clean/group/only/forbid rules, invalid rules, Go internal imports,
TypeScript feature imports, workspace contracts/configuration, chatty thresholds,
provider coverage and bounded snippets. Arrays originating in Go maps are sorted
for comparison; evidence fields, text, confidence and null/empty shape are kept.
Integration fixtures cover real SQLite knowledge levels and provenance, narrow
group precedence, workspace package consumers, missing peers, read-only state
and healthy-empty/failed provider cycles.

Commands and hashes are recorded in
[architecture-validation.json](inventory/architecture-validation.json).
