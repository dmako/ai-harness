# Provider v1 and public facade (PORT-014)

TASK-0014 connects configured providers to the CLI MCP server and adds the four
public facade tools. Its authority is Go revision
`9cb401dd2de4f85dd089fea86a0b139d81b178c6`. The
[validation receipt](inventory/provider-facade-validation.json) records source
hashes, test results and release artifacts.

## Protocol and composition

`Harness.Providers.Protocol.ProtocolProvider` consumes `harness_provider_info`,
accepts `harness-provider/1` and its dotted minor revisions, and rejects other
major versions. Capability tool names replace dots with underscores. Shared
envelopes carry request query/path/limit/target/budget/package/version/type and
response items/truncation/summary/text. Refresh forwards typed full/changed
arguments to `harness_refresh`, or returns a skipped result when no index exists.

Construction and cached health are passive. First capability discovery or use
performs a single-flight handshake. Successful info is cached; failed handshakes
are retried no more often than every 30 seconds. Hints normalize to low/medium/high,
package capabilities use external scope, and indexed/evidence declarations are
preserved. Items retain their fields, receive harness's provider provenance and
a default capability tool name, and respect a positive requested limit. Both
structured content and JSON text are accepted. Tool errors, invalid payloads,
incompatible handshakes, unsupported capabilities and adapter timeouts remain
distinct. The [downstream process client](downstream-mcp.md) supplies bounded I/O,
cancellation, restart and descendant cleanup.

The Core discovery port lets the registry and MCP facade await remote capability
discovery before using cached synchronous declarations. Concurrent discovery
shares the same handshake and honors caller cancellation; routing wrappers keep
language-inactive providers passive. The synchronous capability API remains
available for existing callers.

`ProtocolServer.RunAsync` exposes an `IProvider` over the same contract, including
discovery, capability calls and optional refresh. It validates argument names
and types, returns structured and matching text results, represents empty item
collections as arrays and closes the owned provider on shutdown. No native
analyzer is introduced.

`ConfiguredProviders` accepts expanded configuration and a Core process factory.
It selects generic MCP or v1 protocol adapters, then applies the existing registry
ordering, enabled/context/language/only routing and nested-worktree filter.
`McpCommand` owns this registry for the CLI server lifetime. Packs remain embedded
in Infrastructure's existing configuration loader, avoiding duplicate TOML
parsers or forbidden project references. No additional NuGet package is needed;
the existing MCP SDK, System.Text.Json, Channels and SQLite cover this task.

## Frozen packs

The five packs present at the pin are `codebase-memory`, `ripwire`, `rg-fd`,
`nuget` and `graphify-csharp`. There is no separate trace pack in that revision;
the existing caller/reference/impact mappings stay in their original packs.
No integration is invented to fill that absent name.

The loader retains implicit pack selection by provider name, explicit `pack`,
top-level overrides, per-capability replacement, global/project layering and
strict validation. Tests compare embedded TOML with the committed source
inventory plus the reviewed upstream fixture below, and exercise overridden
commands, disabled providers, capability restriction and separate code/package
routing without launching executables.
Graphify execution remains PORT-015; its context exclusion is preserved.

### Upstream MCP compatibility fix (01f8b47)

The 2026-10-22 update ports Go commit
`01f8b4775910ad8af1fec024c4c3029dd584372a`. It supplements the original frozen
baseline rather than rewriting its historical inventories and evidence.
[Source hashes and provenance](../../test/fixtures/compatibility/upstream-mcp-01f8b47.json)
identify the three upstream files; the embedded-pack comparison uses an exact
[upstream TOML fixture](../../test/fixtures/compatibility/codebase-memory-01f8b47.toml)
for codebase-memory while the other four packs retain their original baseline.

The codebase-memory pack requests JSON explicitly for project resolution, symbol
and text search, architecture overview, and duplicate queries. Modern positional
`rows` are mapped first; older `results` responses retain fallback mappings. The
generic mapper reads `"start-end"` line ranges and derives a missing non-file name
from the qualified name's final segment. Explicit names and nonzero end lines
retain precedence; file-name fallback stays based on the file path. Text searches
continue to use the first matching line, not the enclosing symbol's start.

`CodebaseMemoryPackTests` covers both response generations, emitted JSON arguments,
line-range parsing, malformed ranges, explicit-field precedence, and file names.
The original default capability restriction remains unchanged: codebase-memory
text search is available only when explicitly routed back in.

Validation: the C# provider area passed 148 tests (one optional real-Graphify test
skipped), including the installed codebase-memory server's isolated smoke test.
All ten new regression cases also passed on Linux. The upstream Go generic MCP
package passed all 22 tests on Linux; Windows passed 21, with its existing
`TestSymbolSearchMapsArgsAndFields` fixture failing because it substitutes raw
Windows backslashes into JSON. The solution built with zero warnings/errors and
the changed C# files passed the pinned formatter check. The broad unit run passed
246 cases and skipped 208 (unavailable unrelated Go oracles and a Unix-only
fixture); those skips are not counted as a full parity replay for this update.

## Public MCP behavior

Four captured Go tool descriptors are embedded unchanged, bringing the current
server to 24 tools. Existing schema validation applies before dispatch, including
argument hints and unknown-property rejection. Success results keep matching
structured and canonical text content.

* `search_project` supports all, symbol, text, file, callers, references and impact;
  trims queries, validates paths, forwards limits (default 10, maximum 50), and
  preserves ordered provider results, partial errors, truncation and provenance.
  Debug alone includes qualified names, downstream tools, scores and call metrics.
  Missing capabilities and empty graph results have explicit guidance. Unindexed
  providers contribute a degradation note while live providers can still answer.
* `provider_status` probes independent providers concurrently and preserves
  registration order. It reports health, fixes, capabilities/freshness, five
  architecture coverage entries, and existing 24-hour call/30-day context statistics.
* `package_api` validates search/info/types/definition inputs and routes exclusively
  to package capabilities. It preserves API text, partial errors and explicit
  unavailable-capability/private-feed guidance.
* `project_overview` returns project identity/root, Git branch/commit, capability
  providers and reported structure, together with partial errors and staleness.

The Core `IProviderRegistry` and `IProviderProjectSession` ports keep Mcp dependent
only on Core. Infrastructure implements project metadata and historical reads.
Search consults project state only when an indexed provider exists. These reads
and observations never initialize an absent database. Existing sync-state rows
produce never-synced, moved-HEAD and branch-switch warnings. A branch switch
withholds indexed search results and overview structure while retaining live
results; a moved HEAD warns without withholding. Sync mutations remain PORT-016.

The overview snapshot has an optional stack field for PORT-020's stack reader.
Workspace search and richer stack/workspace enrichment are allocated to later
tasks in the [scope map](scope-map.md); this task does not claim those analyses.

Provider call observations use a bounded background queue rather than database
writes on the request path. The recorder uses read/write-existing SQLite mode,
redacts/truncates queries and errors, retains approximately 10,000 calls, and
allows two seconds to drain on shutdown. Under sustained overload it drops old
queued observations. Optional metrics cannot fail a provider call. Native SQLite
busy waits are capped at two seconds per operation; an in-flight native wait can
outlast the drain budget before remaining queued observations are discarded.

## Validation and reproduction

Tests cover v1 minor/major compatibility, capability hints, field forwarding,
structured/text response precedence, item limits and provenance, cached failed
handshake/retry, explicit unsupported/refresh/error behavior, and short RPC
timeouts. A real SDK session exercises `ProtocolServer` discovery, validation,
calls, refresh and ownership cleanup.

Eleven differential cases run the pinned Go protocol adapter and facade against
the same independent worker. They compare normal/structured calls, unsupported
capabilities, incompatible majors, tool errors, concise/debug search, overview,
status and package answers/unavailable capabilities. Only process IDs and elapsed
milliseconds are normalized. The CLI test compares all four advertised input and
output schemas with the frozen descriptors and invokes all four tools through
an actual stdio client. Git tests exercise staleness from existing sync rows.

Build the optional additional oracle with the existing isolated source/toolchain:

```powershell
python test/Harness.Compatibility/Providers/build_oracle.py --source artifacts/compatibility/oracle/source --go artifacts/compatibility/toolchain/go/bin/go.exe --modules artifacts/compatibility/oracle/modules --bridge test/Harness.Compatibility/Providers/protocol_oracle.go --output artifacts/provider-oracle/protocol-oracle.exe
$env:HARNESS_GO_PROTOCOL_ORACLE="$PWD/artifacts/provider-oracle/protocol-oracle.exe"
dotnet test --solution Harness.slnx --no-restore -bl:artifacts/provider-facade-tests.binlog --report-trx --results-directory artifacts/provider-facade-results
```

The optional storage, memory, generic-provider and real-provider environment
variables from earlier task documentation enable those regression suites too.
The generic-provider non-timing fixture allows ten seconds under concurrent
process load; dedicated short-deadline tests continue to assert bounded timeouts.
Process-heavy fixture classes share a test collection to avoid saturating the
host; their explicit parallel-call and single-flight scenarios still run.
Python and Go remain development-time compatibility tools, not product runtime
dependencies. Captured packs/schemas retain the original MIT notices.
