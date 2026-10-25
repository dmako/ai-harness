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

Embedded packs provide defaults for codebase-memory, graphify-csharp, NuGet,
rg/fd and ripwire. They configure external tools rather than bundling those tools.
Users can override commands, filters and capability mappings in TOML. Protocol
and process-worker tests run with deterministic local test servers; installed
backends have separately enabled smoke tests.
