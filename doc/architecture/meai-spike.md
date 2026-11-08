# Spike: Microsoft.Extensions.AI in the provider layer? (TASK-0111)

**Recommendation: do not adopt it in the provider layer.** Keep calling MCP providers the way harness does today.
Adopt it **partially, later**: if harness ever gets a provider backed by a language model or an embedding model,
that provider should sit behind M.E.AI's `IChatClient` or `IEmbeddingGenerator` inside an ordinary `IProvider`
adapter.

1. **It would not simplify the code.** M.E.AI has no counterpart for the parts of the provider layer that carry
   weight:
   - process ownership, supervision and restarts;
   - the declarative TOML mapping from tool answers to `ProviderItem`s;
   - capability routing;
   - the registry's parallel fan-out with deadlines.

   The only thing it would replace is a single call, `McpClient.CallToolAsync`, with `McpClientTool.InvokeAsync`.
   That swap removes no lines and loses the typed `CallToolResult` (see §2).
2. **It would not make adding or combining MCP providers easier.** Today a new MCP server is a TOML pack with no code.
   A fallback or chain is `chain` plus `combine = "first"` in that TOML. With M.E.AI, each of these becomes C# code: an
   `AIFunction` subclass (§4).
3. **It costs nothing at run time, and almost nothing to ship, if harness uses only the abstractions.**
   - The call paths are within noise of each other: about 121–125 µs on the fake server, and the same 23–55 ms on
     ripwire and codebase-memory.
   - Wrapping a tool as an `AIFunction` adds 0.06 MB to a Native AOT executable.
   - The full `Microsoft.Extensions.AI` package adds 1.8 MB. That package is what `FunctionInvokingChatClient` and the
     middleware builders need.
   - There are no trim or AOT warnings in any variant.

   One measured cost: for providers that answer with structured content (codebase-memory), `AIFunction` re-serializes
   the whole `CallToolResult`. That is +20.7 KB allocated per call (+34%).
4. **Where it pays off is model-backed providers.** These are not in harness today. Examples: semantic reranking for
   `get_context`, embedding search over memories, a model that decides which provider tools to call.
   - `IChatClient` and `IEmbeddingGenerator` are the standard .NET seams for that. Vendors and local runtimes ship
     clients that implement them.
   - `Microsoft.Extensions.AI.Abstractions` is **already in harness's dependency graph**: it comes in transitively
     through ModelContextProtocol.Core 2.3.0. Using it adds no new package.
   - The spike runs a scripted model and a stand-in embedding generator over real MCP tools under Native AOT (§4).

The code and raw results are in [benchmarks/meai-spike](../../benchmarks/meai-spike/README.md).

**Machine**
- Windows 11 (10.0.26300), 24 logical CPUs.
- .NET SDK 10.0.403, ILCompiler 10.0.14.
- Release build, Native AOT, win-x64.
- Providers: ripwire 1.0 and codebase-memory-mcp 0.12.1, run against this repository.

Linux and macOS were not measured. Nothing here is platform-specific beyond the size numbers.

## 1. The provider layer today, and what M.E.AI offers for each part

All code lives in `src/Harness.Providers` (about 4,300 lines) on top of the contracts in
`src/Harness.Core/Providers` (about 460 lines).

| Part | What it does | Lines | M.E.AI counterpart |
|---|---|---:|---|
| `IProvider`, `IProviderRefresher`, `IProviderDiscovery`, `CapabilityInfo`, `ProviderRequest`/`ProviderResult` (Core) | A provider answers *capabilities* (`code.symbol_search`, `arch.change_coupling`, …), each with evidence, freshness and cost hints. It also reports its health and refreshes its index. | 460 | **None.** `AITool`/`AIFunction` is a named function with a JSON schema. It has no capability vocabulary, health, refresh, freshness or evidence. |
| `McpProcessClient` | Starts the provider through `IProviderProcessFactory` (an OS containment group, so `KillTree` reaches descendants). Caps frames at 16 MiB (`BoundedFrameStream`), keeps a redacted stderr tail, applies start and per-call deadlines, and maps failures to `IOException`/`TimeoutException`. | 326 + 59 | **None.** M.E.AI does not deal with processes or transports. The MCP SDK's own `StdioClientTransport` could start the process, but it has no containment group or frame bound, which harness relies on. |
| `McpSupervisor` | Lazily starts one client per provider, restarts it with backoff within a window, and reports health. | 285 | **None.** |
| `McpMapping` + `GenericMcpProvider` | Fills argument templates (`{query}`, `{limit}`). Turns tool text or JSON into `ProviderItem`s through the TOML pack (`items`, `fields`, `pattern`, `where`, `miss`, `truncated`). Runs `chain`/`combine` plans. Resolves the provider's project name. Remembers "not indexed" answers. | 775 + 465 + 151 | **Only the single call.** `McpClientTool.InvokeAsync(AIFunctionArguments)` can replace `McpClient.CallToolAsync`. Everything around it is harness-specific. |
| `ProtocolProvider`, `ProviderProtocol`, `ProtocolServer` | harness-provider/1: a handshake that lists capabilities, then typed calls over MCP tools. | 378 + 78 + 246 | **None.** It is a harness protocol, carried by MCP. |
| `ProviderRegistry`, `ProviderRouting`, `ConfiguredProviders`, `NestedWorktreeFilter` | Registration and enablement. Routing by language and `only`. Parallel fan-out with a deadline per provider. Results observed for telemetry. Paths filtered. | 306 + 172 + 55 + 98 | **None for functions.** M.E.AI middleware (`UseLogging`, `UseOpenTelemetry`, `UseDistributedCache`, `UseFunctionInvocation`) wraps `IChatClient`/`IEmbeddingGenerator`, not tools. For tools the only building block is `DelegatingAIFunction`, which wraps one function in another. |
| `GraphifyProvider` | An in-process provider over a warm graphify-csharp session. | 844 | **None.** |

The connection to M.E.AI is already there:
- ModelContextProtocol.Core 2.3.0 depends on Microsoft.Extensions.AI.Abstractions 10.11.0.
- `McpClientTool` derives from `AIFunction`.
- Harness's own code uses no M.E.AI type. The MCP server (`HarnessMcpServer`) uses the SDK's low-level
  `CallToolHandler`.

## 2. What would be removed, replaced or added

**Removed: nothing.** Every line listed in §1 does harness-specific work that M.E.AI does not offer.

**Replaced: one call site.** `McpProcessClient.CallToolAsync` (about 10 lines) could call
`McpClientTool.InvokeAsync`. This makes things worse, not better:
- **The answer becomes `object?`, with two shapes.** A single text block comes back as M.E.AI `TextContent`. A
  structured answer, several blocks, or an *error* comes back as a `JsonElement` holding the serialized
  `CallToolResult`. `McpMapping.Text`, `Body` and `ToolError` work on the typed `CallToolResult` (`IsError`,
  `StructuredContent`, content blocks). They would have to decode that `JsonElement` again.
- **Errors stop being typed.** A failing tool no longer sets `IsError`. Harness would have to look for `"isError":
  true` inside JSON (see `FirstAnswer` in [Compose.cs](../../benchmarks/meai-spike/Compose.cs)).
- **It needs a tool list first.** `McpClientTool` comes from `tools/list`, or from
  `new McpClientTool(client, new Tool { Name = … })`. Today `GenericMcpProvider` never lists tools. Listing costs
  0.8–2 ms on ripwire and 27 ms on codebase-memory, once per process start.

**Added if adopted anyway:** an `AIFunction`-based wrapper per provider and per chain. That is roughly 100–150 lines,
the same logic as today's `Answers`/`Plan` moved into `AIFunction` subclasses. There is no gain.

**Stays harness-specific in every case:** the capability model, packs and mapping, process containment and
supervision, the registry, harness-provider/1 and graphify. In other words, the whole layer.

## 3. Performance

`bench` connects one SDK session to one provider process. It then makes the same tool call three ways, rotating
which goes first in each round:

- **`current`:** harness's path. A per-call deadline, `CallToolAsync`, text blocks joined, JSON parsed as
  `McpMapping.Body` does.
- **`aifunction`:** `McpClientTool.InvokeAsync`, then the answer turned back into text and parsed.
- **`middleware`:** the same `AIFunction` behind a `DelegatingAIFunction` that applies the deadline, the M.E.AI way
  of composing a pipeline.

Allocation is measured with `GC.GetTotalAllocatedBytes(precise: true)` around each call. That includes the SDK's
transport threads.

| Provider and call | Rounds | `current` median / p90 | `aifunction` | `middleware` | Allocated per call (`current` / `aifunction` / `middleware`) |
|---|---:|---|---|---|---|
| fake server, 4 KB JSON | 1000 | 121 / 245 µs | 122 / 234 µs | 125 / 238 µs | 43.4 / 43.3 / 43.7 KB |
| fake server, 64 KB JSON | 1000 | 960 / 1397 µs | 950 / 1367 µs | 948 / 1387 µs | 580 / 580 / 580 KB |
| ripwire `grep` (JSON, 5.5 KB) | 300 | 54.7 / 58.7 ms | 55.0 / 58.4 ms | 54.4 / 58.3 ms | 50.9 / 50.8 / 51.1 KB |
| ripwire `for` (text, 4.2 KB) | 300 | 23.1 / 25.5 ms | 23.1 / 25.5 ms | 23.2 / 25.2 ms | 31.5 / 31.5 / 32.0 KB |
| codebase-memory `search_graph` (structured, 4.3 KB) | 300 | 33.3 / 34.7 ms | 33.5 / 35.0 ms | 33.3 / 34.9 ms | **60.5 / 81.2 / 81.5 KB** |

- **Latency:** the differences are a few microseconds, inside the noise. A real provider's own work is 100–400
  times the client's share.
- **Allocation:** the paths are the same, except that structured answers cost +20.7 KB per call through
  `AIFunction`, because it serializes the `CallToolResult` to a `JsonElement`. The `DelegatingAIFunction` adds about
  0.3–0.5 KB, mostly a linked `CancellationTokenSource` (today's path makes one as well).
- **Startup:** connecting is the same either way (50 ms to the fake server, 56–72 ms to ripwire, 433 ms to
  codebase-memory). `AIFunction` adds the one-time `tools/list` described in §2.

Under the constraint "what agents call through MCP must be fast", M.E.AI neither helps nor hurts the hot path. Its
only measurable effect is the extra allocation on structured answers.

**Native AOT (win-x64, trimmed).** `MeaiSpike` publishes with **no** trim or AOT warnings, and so does each size
probe. All four composition steps pass in the native executable:
[results/compose-win-x64.txt](../../benchmarks/meai-spike/results/compose-win-x64.txt).

| Size probe (one MCP tool call) | Executable | vs. MCP only |
|---|---:|---:|
| `McpOnly`: `CallToolAsync`, harness today | 9,325,056 B | — |
| `McpAIFunction`: `McpClientTool` as `AIFunction` (Abstractions only) | 9,383,936 B | +58,880 B (+0.6%) |
| `McpMeai`: plus `Microsoft.Extensions.AI` (`FunctionInvokingChatClient`, an `IEmbeddingGenerator`) | 11,130,880 B | +1,805,824 B (+19%) |

The installed harness.exe is 45.9 MB, so 1.8 MB would be about 4% of it. That is an upper bound: harness already
carries System.Text.Json and the MCP SDK.

## 4. Adding and combining providers

| Case | Today | With M.E.AI |
|---|---|---|
| A new MCP server (e.g. another code index) | A TOML pack in `Configuration/Packs` or `[providers.x]` in config. **No code.** | The same pack is still needed to map answers to capabilities. M.E.AI adds nothing. |
| Fallback: ask A, then B on a miss | `chain` + `combine = "first"` + `miss` in the pack. **No code.** | An `AIFunction` subclass (`FirstAnswer` in [Compose.cs](../../benchmarks/meai-spike/Compose.cs), about 25 lines). It also has to parse `isError` out of JSON. |
| Union: ask several providers at once | `ProviderRegistry.CallAsync` already does this for every capability, with a deadline per provider. | `Task.WhenAll` over `AIFunction`s, the same as today without the deadlines and telemetry. |
| Middleware (deadline, logging, telemetry, cache) around a provider | Inside `McpProcessClient.CallAsync` and `ProviderRegistry.CallOneAsync`. | `DelegatingAIFunction` per concern (`DeadlineFunction` in [Bench.cs](../../benchmarks/meai-spike/Bench.cs)). M.E.AI ships no ready-made middleware for functions, only for chat and embedding clients. |
| **A model-backed provider** (rerank, semantic memory search, a model choosing tools) | A new `IProvider`, plus a hand-written HTTP client for each vendor. | **A new `IProvider` over `IEmbeddingGenerator`/`IChatClient`.** Any vendor or local client that implements them plugs in, with M.E.AI's logging, OpenTelemetry and caching middleware. Provider tools reach a model through `FunctionInvokingChatClient` unchanged, because `McpClientTool` already is an `AIFunction`. |

This is a sketch of the last row, the only case where M.E.AI saves work: a rerank provider for `get_context`. The
spike's `compose` command runs the same idea with a stand-in generator.

```csharp
// Harness.Providers/Models/EmbeddingRerankProvider.cs: a capability like "code.rerank" answered by any M.E.AI
// embedding client (local or hosted), chosen and configured like any other provider.
public sealed class EmbeddingRerankProvider(string name, IEmbeddingGenerator<string, Embedding<float>> model) : IProvider
{
    public string Name => name;

    public IReadOnlyList<CapabilityInfo> Capabilities() =>
        [new("code.rerank", name, "medium", "low", "live", "project", "inferred")];

    public async Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        var candidates = request.Items; // what the other providers found
        var vectors = await model.GenerateAsync([request.Query, .. candidates.Select(c => c.Name + " " + c.Path)],
            cancellationToken: cancellationToken);
        var query = vectors[0].Vector;
        return new()
        {
            Items = candidates
                .Select((item, i) => item with { Score = TensorPrimitives.CosineSimilarity(query.Span, vectors[i + 1].Vector.Span) })
                .OrderByDescending(item => item.Score)
                .ToArray(),
        };
    }

    public ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default) => /* … */;
    public ValueTask DisposeAsync() { model.Dispose(); return ValueTask.CompletedTask; }
}
```

The sketch shows the shape only. `ProviderRequest.Items` and a rerank capability do not exist yet. Whether a vendor
client (OpenAI, Azure, Ollama, Anthropic) is clean under Native AOT has to be checked when one is chosen. The spike
checked only M.E.AI itself.

## 5. Packages

| Package | Version | Brings | In harness today? |
|---|---|---|---|
| Microsoft.Extensions.AI.Abstractions | 10.11.0 (latest, 2026-10-14) | — | **Yes**, transitively through ModelContextProtocol.Core 2.3.0. Listed in the lock files of Providers, Mcp, Cli and Graphify. |
| Microsoft.Extensions.AI | 10.11.0 (latest) | Abstractions 10.11.0, Caching.Abstractions 10.0.13, DependencyInjection.Abstractions 10.0.13, Logging.Abstractions 10.0.13, Primitives 10.0.13, System.Numerics.Tensors 10.0.13, System.Diagnostics.DiagnosticSource, System.Text.Json and System.Threading.Channels (the last three are in the .NET 10 framework) | No |

**Conflicts: none.**
- ModelContextProtocol.Core 2.3.0 requires exactly the Abstractions version M.E.AI 10.11.0 ships.
- Every other dependency asks for 10.0.13 or later, and harness pins `NetSdkVersion` 10.0.14.

**Adding `Microsoft.Extensions.AI` would:**
- add a `PackageVersion` to `Directory.Packages.props`;
- add new transitive entries (Caching.Abstractions, System.Numerics.Tensors) to each consuming project's
  `packages.lock.json`;
- add those entries to `src/Harness.Cli/packages.publish.lock.json`, which must be restored with
  `-p:RuntimeIdentifier=<rid>` for all four RIDs, as the existing harness warning says.

**Using only the Abstractions changes no lock file.**

Under the dependency rules, a model-backed provider belongs in Harness.Providers (Core stays dependency-free). The
Abstractions would become a direct reference there, at the version the MCP SDK already brings.

## 6. Recommendation

- **Provider layer (MCP and harness-provider/1): do not adopt.** It removes no code, adds no capability, and makes
  results less typed. It costs extra allocation on structured answers. Packs and `chain`/`combine` already make adding
  and combining MCP providers a matter of configuration, which M.E.AI would turn back into code.
- **Model-backed providers: adopt the abstractions when such a provider is planned.** Use `IEmbeddingGenerator` /
  `IChatClient` from Microsoft.Extensions.AI.Abstractions as the seam inside an `IProvider` adapter in
  Harness.Providers. Take `Microsoft.Extensions.AI` (+1.8 MB) only if its middleware or `FunctionInvokingChatClient`
  is actually used. Check each vendor client for Native AOT before adopting it.
- **No follow-up tasks now.** No model-backed feature is planned. TASK-0112 (Microsoft Agent Framework) builds on
  M.E.AI. If it leads to adopting MAF, these abstractions arrive with it, and this recommendation applies there.
