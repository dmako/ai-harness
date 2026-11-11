# Spikes

A spike measures one question before harness commits to an answer: a library, an engine or a framework, tried
against harness's real workload. Each folder here keeps a spike's write-up, how it was measured, and the raw output of
its runs. The spikes' code was removed in TASK-0119; it is in the Git history up to commit `a3e435a` under
`benchmarks/<spike>/`.

| Spike | What it measured | Decision |
| --- | --- | --- |
| [libgit2](libgit2-spike/README.md) (TASK-0097, TASK-0098) | harness's hot git commands as git processes against LibGit2Sharp and the managed [GitReader](libgit2-spike/GITREADER.md), on ai-harness-cs and a large repository, Windows and Linux, plus repository formats and Native AOT behaviour | Keep the external git; cut the number of git starts instead |
| [LiteDB](litedb-spike/README.md) (TASK-0110) | LiteDB 5 against SQLite on a real `harness.db`: hot-operation speed, several processes writing at once, Native AOT cleanliness and executable size | Stay on SQLite |
| [Microsoft.Extensions.AI](meai-spike/README.md) (TASK-0111) | MCP tool calls through `AIFunction` and M.E.AI middleware against harness's provider path: latency, allocations, AOT size and composition | Not in the provider layer; use `IChatClient`/`IEmbeddingGenerator` only for a future model-backed provider |
| [Microsoft Agent Framework sessions](maf-spike/README.md) (TASK-0112) | agent sessions per task through ClaudeCodeSdk.MAF and CodexSdk.MAF: session identity and resume, cancel and pause, usage and cost, Native AOT | Do not adopt MAF; use hooks, transcripts and harness's own runner over the CLIs' stdio protocols |

Each spike folder holds:

- `README.md`: the question, the recommendation and the findings behind it;
- `measurements.md` (libgit2: inside its `README.md`): the spike's program, the commands it ran and the machines;
- `results/`: the raw output of every run.
