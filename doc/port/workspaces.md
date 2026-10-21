# Machine workspaces and cross-project context

PORT-019 adds machine registry management and workspace scopes to the existing
`search_project` and `get_context` tools. No additional MCP tools or package
dependencies are introduced.

## Membership and machine state

`harness projects [list] [--json]`, `projects rename` and `projects forget`
manage the existing machine registry. The read-only overview reports Git and
schema state, sync freshness, actual pending proposal counts and the average
context hit rate per scored answer over 30 days. It starts no providers and
does not migrate databases. Text output includes usage, clients, workspaces and
worktrees when available.

`harness workspace` (alias `workspaces`) defaults to `list`. Its subcommands are
`create`, `show`, `join`, `leave`, `delete` and `rename`. Create accepts
`--discover <directory>` to add already registered repositories beneath that
directory. The separate `workspace discover <directory>` listing and JSON
listing/show options are additive conveniences. Discovery never initializes
repositories. Project short names retain case; workspace names normalize to
lowercase. Titles collapse whitespace and allow at most 80 Unicode scalars.

Both JSON machine files use an exclusive cross-process lock, compare-before-
replace protection and a flushed temporary file in the destination directory.
Unix writes are owner-only. Invalid existing data fails without replacement.
Membership changes, project forgetting and renaming never modify member
databases. Workspace renaming retains its ID, so its existing knowledge
database remains applicable. Unknown machine-file properties survive updates.

## Retrieval and lifetime

Workspace scope resolves only workspaces containing the current project.
Missing registry entries are skipped; missing or inaccessible repositories
produce partial evidence alongside healthy peers. Every result retains its
project name, and `<project>:<path>#<symbol>` selects a recognized member.
Duplicate short names receive a parent-directory prefix. Results put the
current project first, then other projects in UTF-8 lexical order.

Member queries run concurrently. Light mode has a 10-second cap; explicit
deep mode has a 60-second cap and retries warming providers within that cap.
Uncooperative members get at most one additional second before completed peers
are returned. The session owns and reuses one registry per member root and
mode; it borrows the current registry. A membership change retires old
registries after their active requests finish. Server shutdown closes owned
providers.

Light mode filters excluded providers before metadata discovery, including
the borrowed current-project registry. This intentionally corrects a frozen-Go
path that could discover heavy current-project adapters during a light
workspace query. Deep mode performs full discovery. Invalid peer configuration
is reported as partial evidence instead of silently starting default providers.

Impact combines current-project impact, other projects' references, package
manifest dependencies, namespace users and name matches, in that strength
order. Duplicate project/path/line locations appear once. Compiler evidence
and manifest dependencies are exact; namespace and name matches are approximate.
C# package identity reads nearest build/CPM props, direct imports, the project
and build targets without executing MSBuild. It expands simple properties and
quoted equality conditions. Unresolved properties or unsupported conditions,
imports or functions withhold package evidence while retaining namespace and
compiler evidence. Full framework-aware MSBuild/ecosystem evaluation belongs
to PORT-020; these searches do not claim compiler-exact build evaluation.

## Context and knowledge boundaries

Other projects receive at most one quarter of the serialized context budget.
Their section contains project-tagged code locations and relevant decisions,
conventions, warnings and constraints read from their existing databases.
Those reads are read-only and never create or migrate state. Cross-project
path memories exclude project-wide rules. Task-text memories require at least
two distinct matching content words. Memory IDs deduplicate within each member.
Dropped matches or memories are reported in the workspace budget note.

Current-project memory still uses the existing project/workspace/user layers,
their scope applicability, overrides and deduplication. Another project's
workspace-wide knowledge is not imported through its code results. Member
memory assembly uses deterministic member order, replacing Go map iteration.
The final UTF-8 budget includes the workspace section, envelope and diagnostics;
`used_bytes` remains the exact serialized response size.

## Validation

`test/Harness.Compatibility/Workspaces/main.go` is a driver around the frozen
Go implementations, with synthetic registries/providers and isolated global
configuration. It compares management, member/reference resolution, ordinary
and deep member search, and impact with literal and imported package identity.
Only random IDs/timestamps are avoided by comparing stable identity relations
and semantic state; item fields and result order compare directly.

```powershell
python test/Harness.Compatibility/Workspaces/build_oracle.py --source artifacts/compatibility/oracle/source --go artifacts/compatibility/toolchain/go/bin/go.exe --modules artifacts/compatibility/oracle/modules --output artifacts/workspace-oracle/workspace-oracle.exe
$env:HARNESS_GO_WORKSPACE_ORACLE="$PWD/artifacts/workspace-oracle/workspace-oracle.exe"
dotnet test --project test/Harness.Integration/Harness.Integration.csproj --filter-class 'Harness.Integration.Workspaces.*' -bl:artifacts/workspaces-focused.binlog
```

Build with `--goos linux` for portable test DLL execution under Linux. The
frozen upstream workspace/search suites pass under Linux; their Unix file-mode
assertion is unsuitable for Windows. C# integration tests exercise CLI and
real stdio MCP, provider startup, serialized budgets, membership invalidation,
partial deadlines, shared-memory isolation, read-only member database hashes
and live overview aggregation. Commands, results and hashes are recorded in
[workspaces-validation.json](inventory/workspaces-validation.json).
