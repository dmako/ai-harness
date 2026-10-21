# Workspace adoption, concerns and stack evidence

PORT-020 adds the frozen `library_adoption` and `similar_concerns` MCP schemas
and handlers. There are now 27 implemented public MCP tools. The implementation
uses the BCL and existing SQLite/provider infrastructure; no dependency or
package-lock changes are required.

## Comparison tools

Library adoption lists published packages, import roots, public declarations,
manifest consumers, imported namespaces and possible local implementations.
It reads repository files even when `depth=deep`; it starts no provider. C#
framework namespaces and test projects do not define a library's offered API.
Common names such as `Extensions` are excluded from copy suggestions; other
matching public names are capped at 50 and explicitly described as possibly
unrelated. Imports and manifest references retain file/line attribution.

Similar concerns searches symbols through the existing light/deep workspace
session, deduplicates locations, ranks matched words and puts implementation
before tests at equal match. Each member contributes its own matching
dependencies and up to five applicable decision/convention/constraint/warning
records, including the existing memory provenance fields. Member databases
are read-only. Names and words are comparison evidence, never a behavioural
equivalence claim or a merge/extraction recommendation.

Missing providers are explicitly distinguished from a provider returning zero
matches. Missing repositories and unreadable files retain partial diagnostics;
an unreadable member is not labelled a non-consumer. These are intentional
clarifications over the frozen source's silent no-provider/individual-read
paths. The ordinary fixture results compare directly with Go.

## Shared readers and UI-ready summaries

`AnalysisRepository` owns the file list and successful/failed text reads for
one request. `EcosystemReader` shares dependency results and static project
evaluation across adoption, dependency graphs, build and stack summaries.
No cache survives its request, so uncommitted manifest edits remain visible.
The dependency graph reads each member once and each manifest once rather
than re-reading it for each member pair. Neither SDKs nor provider processes
are needed for these file-based readers.

| Reader | Preserved evidence |
| --- | --- |
| .NET ecosystem/evaluation | Nearest build/CPM props, direct and resolvable GetPathOfFileAbove imports, targets, case-insensitive properties, framework-specific conditions, package include/update/remove/version overrides, project references, unresolved versions and conditional items |
| .NET build | Solution/slnx membership and missing projects; project kinds, published identity/version, project/package dependencies and source locations |
| npm ecosystem | dependencies, devDependencies, peerDependencies and optionalDependencies; dependency lines, declared numeric versions, package identity and import roots |
| TypeScript/JavaScript build | Workspace packages or source-folder units, pnpm workspaces, relative imports, tsconfig paths/baseUrl, simple Vite aliases, ESM .js-to-.ts paths and imported npm packages |
| Go ecosystem/build | Module identity, require versions, package imports, longest required-module prefix, nested-module exclusion and test/helper filtering |
| Stack | C# host-preferred runtime/framework versions; Node engines/version files/Docker ARG defaults and framework badges; Go directive/frameworks; existing Dart/Flutter pubspec badges |
| Public declarations | C# public types/records and single-line base evidence, Go exported types/functions, TS/JS exports and extends/implements evidence; tests/vendor/declaration-only files excluded |

`WorkspaceEvidenceReader` exposes dependency graphs, project stack/package-use
summaries and workspace runtime/framework/core version rows. Published versions
remain separate from consumed versions; a mismatch means the declarations
differ, not that a consumer was proven incompatible. The HTTP endpoints and
UI wiring remain PORT-027/028 work. Architecture rules, deep/internal-import
findings and system communication analysis are implemented by
[PORT-021](architecture-readers.md); the manifest readers do not create native
symbol indexes.

The original PORT-019 `PackageIdentityReader` remains the conservative impact
helper. The new static evaluator follows the source's supported build-reading
subset, preserves unresolved/conditional flags and never restores, compiles,
loads SDK imports or executes property functions. It is not compiler-exact
MSBuild evaluation.

## File boundaries and validation

Reads use strict UTF-8 and a 4 MiB per-file bound. Normalized paths and symlink
targets must remain within the repository, including imported XML. XML external
entities/DTDs are disabled. Conditions are bounded to 8192 characters and 512
tokens; unsupported expressions remain unknown/conditional. Compared with Go,
outside-repository imports are intentionally withheld rather than reading
arbitrary machine files; these summaries do not claim complete build evaluation.

The frozen oracle driver calls the original ecosystem, build, stack, adoption,
dependency and concern implementations. It contains no replacement algorithms.
Fixtures cover framework conditions, imported CPM versions, monorepos, aliases,
cross-language adoption, source attribution, missing providers, version rows,
read-only memory and public SDK schema/text/structured agreement.

```powershell
python test/Harness.Compatibility/Ecosystem/build_oracle.py --source artifacts/compatibility/oracle/source --go artifacts/compatibility/toolchain/go/bin/go.exe --modules artifacts/compatibility/oracle/modules --output artifacts/ecosystem-oracle/ecosystem-oracle.exe
$env:HARNESS_GO_ECOSYSTEM_ORACLE="$PWD/artifacts/ecosystem-oracle/ecosystem-oracle.exe"
dotnet test --project test/Harness.Unit/Harness.Unit.csproj --filter-class 'Harness.Unit.Architecture.*' -bl:artifacts/evidence-unit.binlog
dotnet test --project test/Harness.Integration/Harness.Integration.csproj --filter-class '*Evidence*' -bl:artifacts/evidence-integration.binlog
```

Windows Go repository resolution needs a normal process token rather than the
restricted test sandbox's temporary-directory token. Linux validation runs the
same portable C# assemblies and a cross-built frozen oracle. Commands, results,
platform limits and hashes are in
[workspace-evidence-validation.json](inventory/workspace-evidence-validation.json).
