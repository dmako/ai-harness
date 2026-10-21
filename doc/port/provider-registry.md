# Provider capability registry

TASK-0012 ports the provider-neutral contracts and routing layer from pinned Go
commit `9cb401dd2de4f85dd089fea86a0b139d81b178c6`. Core owns the contracts under
`Harness.Core.Providers`; `Harness.Providers.Registry` owns registration, routing,
fanout and worktree filtering. No package was added. The integration project now
references Providers; its lock file records that project reference.

## Contracts and composition

The 18 capability identifiers retain their code, external-package and architecture
distinctions. Capability metadata retains cost, latency, live/indexed freshness,
project/external scope and declared versus default exact/inferred evidence.
Requests retain query, path, per-provider limit, context byte budget, flow target
and package/version/type fields. Items retain locations, provider/tool provenance,
scores, flow/impact depth and architecture relations, cycles, counts and confidence.
Structured overview summaries and document answers remain separate from items.
JSON field names and optional zero/empty omission follow the Go declarations.

`IProvider` is asynchronous and independent of transport. `IProviderRefresher`
remains conditional: routing a live provider does not make it refreshable.
`IVersionedProvider` passes through a wrapped adapter's known server version.
`ProviderNotIndexedException` and `UnsupportedCapabilityException` preserve
distinct failure categories. Partial items can accompany an error.

`ProviderRouting.Build` accepts adapter-neutral `ProviderRegistration` entries.
The Cli composition root will translate loaded configuration into these entries
when the process adapters arrive in TASK-0013/0014. This avoids a Providers to
Infrastructure reference. Factories must construct passive adapters: Build never
calls capabilities, probes health or warms them. Generic adapters can declare
capabilities without starting; protocol adapters may handshake on first capability
inspection. `WarmAsync` is explicit and awaitable.

Build registers supported `mcp`/`harness` types in ordinal provider-name order.
Disabled/unknown types never invoke their factories. A light registry also omits
`context = false` factories. A full registry retains those providers for direct
searches and records their context exclusion; assembled-context callers use
`InContext` with the call's skip predicate. Language-inactive providers advertise
nothing and report idle with the pinned explanation even when asked to probe.
An `only` list hides and rejects other capabilities, while refresh still passes
through for active indexed providers.

Language detection inspects the root and one directory level below, with the
pinned C#, TypeScript, Go, Python and Java markers. Matching remains case-sensitive
on Windows, as with Go's glob matching. No recursive source scan occurs.

## Execution and filtering

Each selected provider gets an independent linked deadline, with a default of
30 seconds. Build preserves the Go outer timeout calculation: the larger of
30 seconds and every configured timeout plus one second, including disabled
entries. An adapter's configured shorter RPC deadline belongs to TASK-0013.
The earlier captured Go bug where a configured 800 ms call inherits the outer
30-second deadline is therefore still an explicit adapter follow-up.

Execution starts concurrently, including adapters doing synchronous work before
their first await. Exceptions, errors returned with partial answers and timeout
results remain local to each provider. Results and observations use registration
order regardless of completion order. The registry assigns result provider,
capability, elapsed time and timeout status; it preserves item provenance and raw
response byte counts. No health probe precedes a request.

Adapters enforce request path/limit/budget semantics. The registry forwards these
unchanged; it does not apply a global limit, rerank, deduplicate or truncate
documents. The additional nested-worktree filter removes matching item paths
before observers run, retaining pathless items and the original response bytes.
It examines ancestor `.git` files for `/worktrees/`, caches checks for 30 seconds,
resolves root directory links, and keeps submodules and ordinary nested repos.
Provider-supplied outside/traversal paths do not cause ancestor reads outside the
root. Each section can consume independent results without a heavy discovery scan.

Overview preserves the pinned skip behavior: it calls providers before excluding
their summaries and emits only the first line of each error. Disposal attempts
every registered provider and propagates the first disposal error.

## Intentional corrections and limits

Unlike Go's cooperative-only `Do` wait, C# bounds the registry wait even if an
adapter ignores cancellation. The registry observes late faults and never alters
a returned result afterward. Caller cancellation is reported as cancellation,
with `TimedOut = false`; expiration of the registry deadline reports a timeout.
Go can label caller cancellation as a timeout. Both corrections are exercised
with conformance fakes, including one that ignores cancellation and faults later.
The registry cannot terminate arbitrary in-process code; adapters must release
their own resources and the process adapters will own subprocess termination.

This task introduces no provider MCP tools, subprocess client, shipped-pack
mapping, telemetry persistence, context ranking or compiler analyzer. Those have
separate approved tasks. Platform validation here is Windows x64; Linux/macOS
execution remains a release-matrix check.

## Validation

The integration tests port the pinned registry/routing scenarios using fakes:
parallel start barriers, registration order, direct/context/only/language filters,
empty and unavailable providers, partial and failed results, linked cancellation,
stalled adapters, observers, overview, refresh/version passthrough, explicit warm,
all-provider cleanup, shallow markers, nested-worktree/submodule/outside paths
and cache expiry. Model tests cover evidence and segment-boundary path semantics.

`test/fixtures/compatibility/providers-registry.json` is captured by executing
the pinned Go registry with a separate fixture driver. Eight .NET theory cases
compare its metadata, sorted capabilities, path/limit answers, path coverage,
architecture item, empty item, health and context membership semantically.
Regenerate it without touching the neighboring source checkout:

```powershell
python test/Harness.Compatibility/Providers/capture.py --source artifacts/compatibility/oracle/source --go artifacts/compatibility/toolchain/go/bin/go.exe --output test/fixtures/compatibility/providers-registry.json
```

The [validation receipt](inventory/provider-registry-validation.json) records
source/fixture hashes, actual test totals, build and formatting evidence. Historical
receipts remain unchanged.
