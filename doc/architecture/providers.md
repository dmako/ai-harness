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

Embedded packs provide defaults for codebase-memory, graphify-csharp, NuGet,
rg/fd and ripwire. They configure external tools rather than bundling those tools.
Users can override commands, filters and capability mappings in TOML. Protocol
and process-worker tests run with deterministic local test servers; installed
backends have separately enabled smoke tests.
