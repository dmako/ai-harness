# M.E.AI spike: how it was measured (TASK-0111)

**Recommendation: not in the provider layer.** Use M.E.AI's `IEmbeddingGenerator`/`IChatClient` only if a
model-backed provider is added later. The findings and the reasoning are in [README.md](README.md). This page
describes the spike's program and its runs; the raw output is in [`results/`](results/).

The spike's code (`benchmarks/meai-spike`) was removed from the repository in TASK-0119. To rerun it, check out commit
`a3e435a`, the last one that has it. Since TASK-0118 the projects targeted net11.0 (SDK 11.0.100) and still built and
Native AOT-published on win-x64. The measurements here and in `results/` were taken on .NET 10 (SDK 10.0.403) and were
not repeated.

`MeaiSpike` was a standalone project. It was not in `harness.slnx`, and empty `Directory.*` files kept the
repository's build rules, analyzers and central package versions out. It was published as trimmed Native AOT, like
harness. It referenced:
- ModelContextProtocol.Core 2.3.0, the version harness pins, which brings Microsoft.Extensions.AI.Abstractions
  10.11.0;
- Microsoft.Extensions.AI 10.11.0.

## Commands

| Command | What it does |
|---|---|
| `bench <rounds> <tool> <arguments json> fake <bytes>` | Makes one tool call three ways against the built-in fake MCP server, which answers every call with the same JSON of about `<bytes>` bytes. The three ways: harness's path (`current`), `McpClientTool` as an `AIFunction` (`aifunction`), and that function behind a `DelegatingAIFunction` deadline (`middleware`). The order rotates every round, and the first 10% of rounds are warm-up. Prints median, p90 and mean latency, and allocated bytes per call. |
| `bench <rounds> <tool> <arguments json> -- <command> [args...]` | The same against a real MCP server, started in the current directory. |
| `compose fake <bytes>` | Four ways of combining providers in M.E.AI's terms, each printing PASS or FAIL: a fallback composite (`FirstAnswer`); a union of two providers asked at once; `FunctionInvokingChatClient` with a scripted `IChatClient` calling an MCP tool; and an `IEmbeddingGenerator` (a hashing stand-in) reranking hits. |
| `serve <bytes>` | The fake MCP server. It has the tools `grep`, `grep_b` and `miss`; `miss` answers `isError` "symbol not found". |

`size/` held three minimal programs, all built from the same `size/Probe.cs`. Each makes one MCP tool
call. Publishing them shows what each level of M.E.AI adds to a Native AOT executable:

- `McpOnly`: `McpClient.CallToolAsync`, as harness does today.
- `McpAIFunction`: the same call through `McpClientTool.InvokeAsync` (Abstractions only).
- `McpMeai`: also references Microsoft.Extensions.AI and runs `FunctionInvokingChatClient` and an
  `IEmbeddingGenerator`.

```
# Windows: needs vswhere on PATH (the Visual Studio Installer folder)
dotnet publish -c Release -r win-x64 -o <out>
<out>/MeaiSpike compose fake 4096
<out>/MeaiSpike bench 1000 grep '{"pattern":"x","limit":20}' fake 4096
<out>/MeaiSpike bench 1000 grep '{"pattern":"x","limit":20}' fake 65536
# from the repository root:
<out>/MeaiSpike bench 300 grep '{"pattern":"ProviderResult","limit":20}' -- ripwire --mcp
<out>/MeaiSpike bench 300 for '{"task":"provider registry fan-out","limit":20}' -- ripwire --mcp
<out>/MeaiSpike bench 300 search_graph '{"project":"C-repos-personal-ai-harness-cs","query":"ProviderRegistry","limit":20,"format":"json"}' -- codebase-memory-mcp
dotnet publish size/McpOnly/McpOnly.csproj -c Release -r win-x64 -o <size-out>/McpOnly   # likewise McpAIFunction, McpMeai
<size-out>/McpOnly/McpOnly <out>/MeaiSpike serve 4096
```

**Machine**
- Windows 11 (10.0.26300), 24 logical CPUs.
- .NET SDK 10.0.403, ILCompiler 10.0.14.
- ripwire 1.0 and codebase-memory-mcp 0.12.1, run against this repository.

## Results

| File | What it holds |
|---|---|
| [results/publish-win-x64.txt](results/publish-win-x64.txt) | The Native AOT publish of `MeaiSpike`. It has no IL or AOT warnings. |
| [results/compose-win-x64.txt](results/compose-win-x64.txt) | The four composition steps in the native executable. All pass. |
| [results/bench-fake-win-x64.txt](results/bench-fake-win-x64.txt) | The fake server with 4 KB and 64 KB answers, 1000 rounds each. |
| [results/bench-real-win-x64.txt](results/bench-real-win-x64.txt) | ripwire `grep` and `for`, and codebase-memory `search_graph`, 300 rounds each. |
| [results/size-win-x64.txt](results/size-win-x64.txt) | The three size probes: executable bytes, warning counts, and a run against the fake server. |
| [results/packages.txt](results/packages.txt) | The resolved package graph, including transitive packages. |
