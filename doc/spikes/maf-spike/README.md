# Spike: agent sessions per task through Microsoft Agent Framework? (TASK-0112)

**Recommendation: do not adopt Microsoft Agent Framework or the community bridges** (ClaudeCodeSdk.MAF, CodexSdk.MAF).
Build the four use cases on what harness already has (hooks, transcripts, the task store), plus a small agent-runner
contract of harness's own, with one adapter per CLI.

1. **The MAF layer drops exactly what harness needs.**
   - **Claude's MAF session is not the CLI's session.** Resuming it from a new process starts an empty conversation
     (measured: "remembers: FAIL", under JIT and AOT).
   - **The MAF response has no cost and no CLI session id.** Claude's `ResultMessage` carries both (`TotalCostUsd`,
     `SessionId`), but it never reaches `AgentResponse`.
   - **The Codex bridge's answer text includes event names** (`turn.started`) and repeats the reply.
2. **The Codex bridge does not work under Native AOT**, and harness ships only Native AOT builds.
   - CodexSdk reads the CLI's events with reflection-based System.Text.Json. With reflection off, which is the default
     in an AOT project, it throws at the first event even under JIT.
   - The publish reports 22 IL2026/IL3050 warnings, all in CodexSdk and CodexSdk.MAF. Harness builds treat warnings as
     errors.
   - ClaudeCodeSdk and Microsoft.Agents.AI publish without warnings, and Claude runs natively.
3. **The Codex bridge returns minutes after the turn ends.** A one-word turn finished in 8 s according to Codex's own
   rollout, but `RunAsync` returned after 277 s. A resumed 4-second turn returned after 161 s.
4. **What harness needs is underneath MAF, or already in harness:**
   - the CLIs' own options: Claude `--session-id`/`--resume`, Codex thread ids;
   - harness's PostToolUse hooks, which already record `session_id` for every harness MCP call, `task_claim`
     included;
   - the CLIs' transcripts, which hold per-message usage, model and timestamps.

   Measured: the CLI honours a session id chosen by harness, and `--resume <id>` continues it from a new process.
   Cancel stops a turn and leaves no process behind, for both CLIs.
5. **Pause does not exist in either CLI.** The nearest thing is cancel, then resume from the session or thread id.
   ClaudeCodeSdk's `InterruptAsync` closes the CLI's input, so the process cannot take another turn. This reproduced
   twice.

How it was measured, and the raw results, are in [measurements.md](measurements.md) and [results/](results/). It builds on
[the M.E.AI spike](../meai-spike/README.md) (TASK-0111).

**Machine and versions**
- Windows 11 (10.0.26300); .NET SDK 10.0.403, ILCompiler 10.0.14; win-x64.
- Claude Code 2.1.317 and codex-cli 0.165.0.
- ClaudeCodeSdk(.MAF) 10.4.4, CodexSdk(.MAF) 10.0.3, Microsoft.Agents.AI 1.21.0.

**Sample task**
- A scratch folder holding one README, run read-only: Claude in `plan` mode, Codex in a `read-only` sandbox with
  approval `never`.
- Claude was asked for `haiku`, but its transcripts show `claude-sonnet-5-5` answered. Same in the core SDK's
  probe: $0.076 for a one-word turn.

## 1. Verdicts

| Use case | Claude, started by harness | Claude, started elsewhere | Codex, started by harness | Codex, started elsewhere |
|---|---|---|---|---|
| **1. Which session works on a task** | **Possible.** Harness chooses the id (`--session-id`, honoured) and passes `HARNESS_TASK_ID` in the environment. | **Possible with limits.** Needs the opt-in hooks: the `task_claim`/`task_update` hook event carries `session_id` (§2). | **Possible.** The thread id arrives at start (`OnThreadStartedAsync` / `thread.started`). | **Possible with limits.** The same hooks: Codex's PostToolUse carries `session_id`. |
| **2. Duration and usage/price** | **Possible.** The core SDK's `ResultMessage` gives tokens, `TotalCostUsd`, `DurationMs` and turns. The MAF layer gives tokens only. | **Possible.** Read `~/.claude/projects/<cwd>/<session>.jsonl`: per-message `usage` (input, output, cache read, cache write by TTL), `model`, timestamps. Price from a table. | **Possible, tokens only.** `turn.completed` usage (input, cached, output, reasoning). Price from a table. | **Possible, tokens only.** Read `~/.codex/sessions/YYYY/MM/DD/rollout-*-<thread>.jsonl`: `token_count` (cumulative), `turn_context.model`, `plan_type`. |
| **3. Start / pause / resume / cancel** | Start: **yes**. Cancel: **yes** (7 ms, process gone). Resume: **yes** with `--resume <id>`, **no** through the MAF session. Pause: **no** (cancel + resume). | **Not possible.** Harness does not own the process. Resume is possible only after the user's session has ended (`claude --resume <id>`); otherwise it forks the conversation. | Start: **yes**. Cancel: **yes** (276 ms, process tree gone). Resume: **yes** (thread id). Pause: **no** (cancel + resume). Only with reflection JSON, i.e. not under AOT. | **Not possible**, same as Claude. |
| **4. Connect** | **Possible.** The process that owns the CLI's stdio streams output, sends follow-up turns and answers tool approvals (`CanUseTool`). | **Read-only.** Tail the transcript file. | **Possible with limits.** `codex exec` is one turn per process with no approvals. A live, approvable session needs `codex app-server` (JSON-RPC; not tested here). | **Read-only.** Tail the rollout file. |

## 2. Session identity

What each source gives:

| Source | What it gives | Link to a task |
|---|---|---|
| `tasks.claimant`, `task_events.actor` | The MCP client's name and version (`claude-code 2.1.318`). No session id: the MCP protocol does not carry one. | The claim itself, but not *which* session of that client. |
| `claim_token` | Harness's own token, returned to the agent. | Exact, but harness never sees it again outside MCP calls. |
| Hooks → `tool_events` (`HookIngestion`, migration 0014) | `client`, `session_id`, `mcp_server`/`mcp_tool`, `at`, for every tool call, harness's `task_*` tools included. PostToolUse for both clients, plus SessionStart for Claude. | **Already joinable by time.** TASK-0112's `claimed` event (00:14:19.674Z) has a `task_claim` tool event 123 ms later with this session's id. Exact once `HookPayload.Parse` also keeps the `id` argument of the `task_*` tools, as it already keeps `task` for `get_context`. |
| MAF session (`ClaudeCodeAgentSession.SessionId`) | A Guid the bridge makes up. It is neither the CLI's id nor kept across processes. | Useless for linking. |
| `CodexAgentSession.ThreadId` | The real Codex thread id. | Exact, for sessions harness starts. |
| Claude `--session-id` (ClaudeCodeSdk `SessionId`) | An id harness chooses before starting. The CLI uses it (the transcript is named by it). | Exact, for sessions harness starts. |
| `HARNESS_TASK_ID` in the agent's environment | Inherited by the stdio MCP servers the CLI starts, including `harness mcp`. | Lets the MCP server tag that session's calls. Not verified here: the sample ran in a folder without harness. |

**Proposal.** Add a table `agent_sessions` (project database):
- `task_id`, `client`, `session_id`, `origin` (`harness` | `hook`), `started_at`, `ended_at`, `status`, `model`,
  `transcript_path`;
- token totals, and `cost_usd` (nullable, with its price-table version).

How it is filled:
- **Hook ingestion:** a `task_claim`/`task_next`/`task_update`/`task_complete` event, with the task id read from
  `tool_input.id`, links its `session_id` to that task.
- **The agent runner:** for sessions it starts.

Several sessions can work on one task (claim, release, another claim), so the link is many-to-one, not a column on
`tasks`.

## 3. Duration, usage and price

**Usage fields per path:**

| Path | Fields |
|---|---|
| Claude, MAF `AgentResponse.Usage` | `InputTokenCount` (the sum of uncached input, cache read and cache write), `OutputTokenCount`, `TotalTokenCount`, `CachedInputTokenCount`, `AdditionalCounts` = {`cacheReadInputTokens`, `cacheCreationInputTokens`}. No cost, no duration. |
| Claude, core `ResultMessage` | `Usage` {InputTokens (uncached only, e.g. 2), OutputTokens, CacheReadInputTokens, CacheCreationInputTokens, ServiceTier}, `TotalCostUsd` (API-equivalent, computed by the CLI), `DurationMs`, `DurationApiMs`, `NumTurns`, `SessionId`. |
| Claude transcript | Each assistant message: `model` and `usage` {input, output, cache read, cache creation split into 5 m and 1 h TTL, thinking tokens, server tool use, service tier}, plus `timestamp`. No cost. |
| Codex, MAF `AgentResponse.Usage` | `InputTokenCount`, `OutputTokenCount`, `TotalTokenCount`, `CachedInputTokenCount`, `ReasoningTokenCount`. |
| Codex rollout | `token_count.info.total_token_usage` {input, cached input, cache write, output, reasoning output, total}, cumulative per thread; `turn_context.model` and effort; `rate_limits.plan_type` (here `business`). No cost. |

**Duration.**
- A task's wall time is already in `task_events`: `claimed` to `completed`.
- A session's active time is the sum of its turns: Claude `DurationMs`, or task_started to task_complete in a Codex
  rollout. Idle time between turns can be shown separately.

**Price.** Only Claude Code computes a price, and only for sessions run through its SDK stream.
- For everything else harness needs a price table: per model, the $/MTok for input, cached input, cache write (5 m /
  1 h) and output (reasoning counts as output).
- Proposed: ship it as a TOML file next to the provider packs, versioned with harness releases, with
  `[pricing.<model>]` overrides in config.
- Harness is local-first, so it should not fetch prices at run time. An unknown model shows tokens without a price.
- Label the figure "API-equivalent". Subscription users (Claude Max/Team, ChatGPT plans; Codex rollouts show
  `plan_type`) do not pay per token.

**External sessions.**
- Harness reads the transcript or rollout of each linked session, by the path known from the session id: Claude's
  `<cwd slug>/<session>.jsonl`, Codex's rollout found by the thread id in its name.
- It reads incrementally, from the last offset, on sync or when the task page asks.
- Hooks alone cannot give usage: no hook payload carries token counts.

## 4. Control

What `MafSpike` measured (the raw results are in `results/`):

| Action | Claude | Codex |
|---|---|---|
| Start, one turn | 4.8–6.6 s wall | turn 8 s by rollout, **277 s** until `RunAsync` returned |
| Cancel mid-stream | `OperationCanceledException` 0–7 ms after `Cancel()`; no CLI process left after 3 s | 276 ms; no `codex`/`node` process left |
| Resume in a new process, MAF session | **FAIL**: a new CLI session, context lost | PASS: thread resumed, but 161 s for a 4 s turn |
| Resume in a new process, CLI id | PASS: `Resume = <id chosen at start>` → `--resume` | (same as the row above; the MAF session holds the real thread id) |
| Pause | No such CLI operation. `ClaudeSdkClient.InterruptAsync` closes the CLI's stdin, so the next `QueryAsync` fails with "The pipe is being closed" (twice). Use cancel + `--resume`. | No such operation in `codex exec`. Use cancel + resume. `codex app-server` has `turn/interrupt` (CodexSdk.AppServer's `TurnInterruptAsync`), not tested. |

Cancel plus resume keeps the conversation. It loses only the cancelled turn's unfinished work, so harness can offer
"Stop" and "Resume", and present them as pause if wanted.

## 5. Connecting to a session, and harness ui

**What "connect" means:**
- **Claude:** the stream-json protocol over the CLI's stdin and stdout. Only the process that started the CLI holds
  those pipes. That process can:
  - stream the session's output (`AgentResponseUpdate`s or raw messages);
  - send follow-up turns;
  - answer tool-permission requests (`CanUseTool`, `--permission-prompt-tool stdio`).
- **Codex:** `codex exec --json` takes one turn per process and cannot ask for approval. Interactive control (turns,
  interrupts, approvals) needs `codex app-server`.
- **Sessions started elsewhere:** harness cannot attach to their pipes. It can follow their transcript read-only.
  Taking a session over means the user ends it there and harness resumes it here. Resuming while the other process
  still runs forks the conversation.

So connecting is possible only for sessions harness starts. They must run inside a long-lived harness process,
because a session dies with its owner:
- either harness ui's server;
- or a separate `harness agent` host process that harness ui talks to. The host is better: it outlives harness ui,
  and it keeps the agent packages out of harness.exe (§6).

**harness ui sketch.** In Work › Tasks, the task row's detail gets an *Agent sessions* section. Live updates use the
existing `/api/events` SSE stream (`UiLiveHub`): a new `agent` event kind carries session status, appended output
and approval requests.

```
TASK-0112  Spike: integrate Microsoft Agent Framework …        in_progress · claude-code
┌ Agent sessions ──────────────────────────────────────────────────────────────────────┐
│ ● claude  31cdbc85…  hook     running  1h 12m   312k tok (41k out)   ≈ $4.80 API-eq.  │
│ ○ codex   01a1b887…  harness  ended    2m 05s    54k tok              tokens only      │
│                                                   [Start agent ▾ Claude | Codex]       │
├ claude 31cdbc85… (started elsewhere: read-only) ──────────────────────────────────────┤
│ 00:14:19  task_claim TASK-0112                                                         │
│ 00:16:44  Bash  dotnet build …                                                         │
│ 00:17:02  assistant: Build succeeded. Now …                                ▼ follow   │
├ codex 01a1b887… (started by harness) ─────────────────────────────────────────────────┤
│ … streamed output …                                                                    │
│ ⚠ wants to run: dotnet test            [Allow] [Deny]                                  │
│ [ message to the agent…                                 ] [Send]  [Stop]  [Resume]     │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Sessions harness started:** Send, Stop (cancel), Resume (`--resume` / thread resume) and approvals.
- **Sessions started elsewhere:** the log, usage and an "Open in terminal" hint (`claude --resume <id>`) only.

## 6. Fit: packages, Native AOT, size, ownership

**Packages.** These are what the spike resolved; `results/packages.txt` has the full list.
- ClaudeCodeSdk.MAF 10.4.4 → ClaudeCodeSdk 10.4.4.
- CodexSdk.MAF 10.0.3 → CodexSdk 10.0.3.
- Both → Microsoft.Agents.AI 1.21.0 (the latest is 1.28.0) and Microsoft.Agents.AI.Abstractions 1.21.0.
- Microsoft.Agents.AI → Microsoft.Extensions.AI and .Abstractions 10.10.0, .AI.Evaluation 10.10.0,
  Compliance.Abstractions 10.10.0, VectorData.Abstractions 10.10.0, Microsoft.ML.Tokenizers 2.0.0 (→
  Google.Protobuf 3.30.2), System.Numerics.Tensors, FileSystemGlobbing, ObjectPool, Caching.Abstractions,
  DependencyInjection/Logging.Abstractions and Primitives (10.0.12).
- The core SDKs alone depend only on Logging.Abstractions.

**Conflicts with harness.**
- None that stop a restore. The MCP SDK 2.3.0 lifts M.E.AI.Abstractions to 10.11.0, and NuGet takes the higher
  version. The Microsoft.Extensions 10.0.12 packages are lifted by `NetSdkVersion` 10.0.14 where harness pins them.
- About 13 new packages would enter `packages.lock.json` and the four-RID `packages.publish.lock.json`, including
  ML.Tokenizers and Protobuf, which harness has no use for.

**Native AOT (win-x64).**

| Executable | Size | Trim/AOT warnings | Runs natively |
|---|---:|---:|---|
| `Hello` (empty program) | 932,864 B | 0 | — |
| `ClaudeCore`: ClaudeCodeSdk's own client, no MAF | 3,685,888 B | 0 | Yes. READY, session id, cost $0.0761. |
| `MafSpike`: both bridges + MAF + M.E.AI | 7,353,856 B | **22** (11 IL2026 + 11 IL3050, all in CodexSdk/CodexSdk.MAF) | Claude: yes. **Codex: no** (reflection JSON disabled). |

harness.exe is 45.9 MB and already carries System.Text.Json and the logging abstractions, so these deltas are upper
bounds:
- the Claude core SDK: about 2.8 MB;
- the whole MAF stack: about 6.4 MB.

**Ownership.** Under the dependency rules (Directory.Build.targets: Core has no dependencies; Infrastructure,
Providers, Mcp and Web depend only on Core; Cli composes them):
- The runner contract (`IAgentRunner`: start, resume, cancel, stream events, usage) belongs in Core.
- The session store and the transcript readers belong in Infrastructure, next to `HookIngestion` and
  `TelemetryStore`.
- The CLI adapters do not fit in harness.exe if they bring MAF, or anything not AOT-clean. They belong in a separate
  executable, like `harness-graphify`, that harness ui starts and talks to. It would be a new project
  (`Harness.Agents`, referencing Core) added to the graph rule. Being separate also lets a session outlive harness ui.

## 7. Risk of the community packages

| | ClaudeCodeSdk(.MAF) | CodexSdk(.MAF) |
|---|---|---|
| Maintainer | One person (zxyao) | The same person |
| Repository | github.com/zxyao145/claude-code-sdk-csharp: 7 stars, 142 commits, created 2025-08, last push 2026-10-18, 0 open issues | github.com/zxyao145/codex-sdk-csharp: 1 star, 59 commits, created 2026-03, last push 2026-10-28, 0 open issues; README "generated by Codex" |
| Licence | MIT (repository); the NuGet entry has a deprecated licence URL | Apache-2.0 by GitHub's detection, while its README says MIT |
| Cadence | Frequent previews (10.4.x: about 20 versions); targets net10.0 only | Previews; protocol models pinned to Codex 0.163.0 (0.165.0 installed) |
| Coverage gaps found | The MAF session is not the CLI session; `Resume` ignored by the adapter; cost not surfaced; `InterruptAsync` ends the process | Reflection JSON (not AOT); returns minutes after the turn; event names leak into the answer text |

**Alternatives.**
- **Claude:** Anthropic publishes the Claude Agent SDK for TypeScript and Python, not .NET. Its transport is the
  CLI's documented stream-json (`claude -p --output-format stream-json --input-format stream-json`, with
  `--session-id`/`--resume`/`--permission-prompt-tool stdio`). ClaudeCodeSdk's core (AOT-clean, 2.8 MB) speaks it.
  So does a small adapter of harness's own with source-generated JSON, which is what harness does for MCP already.
- **Codex:** OpenAI publishes `@openai/codex-sdk` (TypeScript), a thin wrapper over `codex exec --json` and
  `codex app-server` (JSON-RPC). Both are plain stdio protocols a harness adapter can speak with source-generated
  JSON.
- **MAF's own abstractions** (`AIAgent`, `AgentSession`) add multi-agent orchestration and workflows that harness
  does not do. The session abstraction is the part harness would use, and it is the part that loses the CLI ids.
- The goal "keep abstractions for other integrations" is better served by a harness-owned `IAgentRunner` with
  explicit `SessionId`, `ResumeAsync`, `CancelAsync` and usage, instead of `AIAgent`. A MAF adapter can still sit
  behind it later if a MAF-native agent ever matters.

## 8. Recommendation and follow-up

**Recommendation:**
- Do not adopt Microsoft Agent Framework or ClaudeCodeSdk.MAF / CodexSdk.MAF.
- Build session tracking from hooks and transcripts first: it covers sessions the user starts, which today is all of
  them.
- Then add harness-started sessions through harness's own runner contract:
  - Claude: stream-json, with `--session-id` chosen by harness and `--resume`;
  - Codex: `codex exec --json`, then `codex app-server` for interactive control;
  - running in a separate agent-host executable, with source-generated JSON.

Proposed follow-up tasks (in the Inbox for approval):

| Use case | Proposed task |
|---|---|
| 1. Identity | Link agent sessions to tasks from hook events: keep the task id of harness `task_*` tool calls in `HookPayload`, add `agent_sessions`, and show a task's sessions. |
| 2. Duration, usage, price | Read Claude transcripts and Codex rollouts of linked sessions incrementally. Sum tokens and active time. Shipped price table with config overrides, shown as API-equivalent. |
| 3. Control | Agent runner contract and agent host: start, cancel and resume Claude (stream-json) and Codex (`exec --json`) sessions for a task, AOT-clean. |
| 4. Connect and UI | Agent sessions on the task in harness ui: live log over `/api/events`, Send/Stop/Resume and tool approvals for sessions harness started, read-only log for others. Codex approvals through `codex app-server`. |
