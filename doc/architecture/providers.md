# Providers

Source: [`src/Harness.Providers`](../../src/Harness.Providers).
Providers depends only on Core. Infrastructure builds configured adapters;
transport processes and discovery remain behind Core contracts.

## Capability routing

The registry maps capability names to providers, isolates failures and merges
partial results with provenance. Provider selection respects enabled state,
`only`, project markers and context participation. Skipping a provider's overview
controls inclusion rather than changing its execution semantics. Configuration
is lazy: constructing a registry does not start every backend.

Deadlines and caller cancellation are propagated independently. Supervisors bound
frames, diagnostic buffers and pending requests, handle disconnects, and own their
child processes. A slow or broken provider does not block unrelated healthy
results. Health and coverage output distinguishes unavailable, unsupported and
empty answers.

## External protocols

- Generic MCP adapters use the MCP SDK client and a supervised stdio transport.
  Mapping templates render arguments and normalize configured result fields,
  including modern object rows and legacy table shapes.
- Protocol adapters speak `harness-provider/1` and use advertised capabilities,
  explicit JSON requests and bounded responses.
- The Graphify adapter exposes compiler-oriented symbol, caller, reference and
  impact capabilities while session discovery/lifetime lives in Infrastructure.
- A generic MCP provider with a `resolve` mapping finds the repository's project
  in the provider's index. `harness sync` gives it a `NotIndexedMemo` (one file
  per provider and repository under `<global config dir>/providers/not-indexed`):
  a "not known" answer is remembered, and the next syncs' refresh and questions
  fail fast with the same note instead of starting the provider (TASK-0103). The
  answer counts while its fingerprint holds (the provider's command, arguments,
  resolve and refresh mappings, and the names of the files in the directory that
  `resolve.index` names, SQLite sidecars and temporary files left out: indexing a
  repository adds a file there) and for a day at most, for indexes made through
  channels harness cannot see. A successful resolve forgets it. MCP sessions and
  doctor do not use the memo.

## Built-in search

Provider type `builtin` (`Harness.Providers.Search.RgFdProvider`, registered as
`search`) serves `code.text_search` through `rg --json` and `code.file_search`
through `fd`, or `rg --files` filtered by the file name when only rg is
installed. It needs no external MCP server: each call starts the tool in the
project root through `IProviderProcessFactory`, reads its output line by line up
to the limit (plus one, which marks the answer truncated) and at most 2 MiB, then
stops the whole process tree; caller cancellation and the provider's timeout do
the same. rg runs with `--sort path` so the first matches are always the same;
fd's answer is collected and sorted. The query is escaped as the packs'
`{query_regex}` (`McpMapping.QuoteMeta`), a `path` becomes the absolute search
path (`{abs_path}`), and items carry the rg-fd pack's fields (`McpMapping.Relative`
paths, `start_line`, `snippet`), so routing and ranking treat both alike.

Detection lives in Core (`SearchTools`), so Infrastructure (doctor's `search`
check), Mcp (`provider_status`'s `unavailable` list) and Cli (`harness report`
finding) can report it without referencing Providers: rg, and fd or `fdfind`, on
`PATH` (Windows tries `.exe`, `.cmd`, `.bat`, `.com`), or the `[providers.search]`
`rg`/`fd` overrides, cached per process. `SearchTools.Status` decides the state:
off (`enabled = false`), superseded (an enabled `rg-fd` pack entry, unless
`[providers.search] enabled = true`), missing (no tool; the registry records an
`UnavailableProvider` with the install hint per OS) or active.
`ConfiguredProviders.Build(builtInSearch: true)` (the MCP server, its workspace
registries and harness ui's probe) adds it; other callers get it only from an
explicit `[providers.search]` entry. The `[providers.search]` table without a
`type` loads as type `builtin`; validation allows only `enabled`, `rg`, `fd`,
`timeout_ms`, `only`, `languages` and `context` on it.

Embedded packs provide defaults for codebase-memory, graphify-csharp, NuGet,
rg/fd (optional; superseded by the built-in search) and ripwire. They configure external tools rather than bundling those tools.
Users can override commands, filters and capability mappings in TOML. Protocol
and process-worker tests run with deterministic local test servers; installed
backends have separately enabled smoke tests.
