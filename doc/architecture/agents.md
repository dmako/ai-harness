# Agents

Source: [`src/Harness.Agents`](../../src/Harness.Agents), contract in
[`src/Harness.Core/Agents`](../../src/Harness.Core/Agents).

`harness-agents` runs one agent turn for a task and owns the client's process. It is a separate Native AOT executable
next to `harness` (TASK-0115), so a session started from harness ui outlives harness ui, and harness itself carries no
agent code. Why harness speaks the clients' own protocols instead of Microsoft Agent Framework:
[the spike](../spikes/maf-spike/README.md).

## Contract

`IAgentRunner` (Core) starts a session for a task (`AgentStart`: task id, working directory, prompt, model, extra
client arguments, environment) or resumes one by its session id. Both return an `IAgentRun`:

- `SessionId`: chosen by harness for Claude Code; for Codex, empty until `thread.started` names the thread;
- `Events`: `Session`, `Text`, `ToolCall`, `Usage`, `Finished`;
- `Completion`: an `AgentOutcome` (completed, failed or cancelled; answer or error; tokens; the client's own cost
  figure when it has one; duration);
- `CancelAsync`: stops the turn and every process the client started.

Neither client can pause a turn: pause is cancel, then resume.

## Adapters

| | Claude Code | Codex |
|---|---|---|
| Start | `claude -p --output-format stream-json --verbose --session-id <new uuid>` | `codex exec --json -` |
| Resume | `claude -p … --resume <session>` | `codex exec resume --json <thread> -` |
| Session id | the uuid harness chose (`system/init` confirms it) | `thread.started.thread_id` |
| Turn ends | `result` (usage, `total_cost_usd`: the session's total so far, `duration_ms`) | `turn.completed` (usage) or `turn.failed` |

- The prompt goes to standard input, which is then closed.
- `HARNESS_TASK_ID` is set in the client's environment; the harness MCP server the client starts inherits it.
- Processes start through `ProviderProcessFactory`: a job object on Windows, a process group elsewhere.
- A run ends when the client says the turn ended. The process then gets two seconds to exit before its tree is
  stopped, because `codex exec` can linger for minutes after its turn.
- A client that exits without ending the turn fails the run with its exit code and the tail of its standard error.

## Host

```
harness-agents run --client claude-code|codex --task <id> --cwd <dir> (--prompt <text> | --prompt-file <file>)
                   [--resume <session>] [--model <model>] [--db <project database>]
                   [--arg <client argument>]... [--env <name=value>]...
                   [--command <executable> [--command-arg <argument>]...]
```

- It writes the events as JSON lines to standard output, ending with an `outcome` line.
- A `cancel` line on standard input, Ctrl+C or SIGTERM cancels the turn. Standard input closing (a detached host)
  changes nothing.
- Exit codes: 0 completed, 1 failed, 2 usage, 3 cancelled.
- With `--db`, it records the session in `agent_sessions` with origin `harness` (`TaskStore.RecordRun`):
  - `working` while a turn runs;
  - then `idle`, `cancelled` or `failed`.

  How the turn ended never hides what the session itself did to the task (blocked, released, completed).
- `--command` replaces the client's executable; the tests use it to run scripted fake clients
  (`Harness.ProviderWorker` with `HARNESS_FAKE_AGENT`).

## Interactive sessions: `serve` (TASK-0116)

`harness-agents serve --key <32 hex> --state <dir> …` keeps one session open across turns, so harness ui can follow
and steer it.

| | Claude Code | Codex |
|---|---|---|
| Process | `claude -p --input-format stream-json --output-format stream-json --verbose --permission-prompt-tool stdio`, the permission mode's flag, and `--session-id` or `--resume` | `codex app-server` (JSON-RPC): `initialize`, `thread/start` or `thread/resume` with the permission mode's approval policy and sandbox |
| A turn | a `user` message on standard input; ends at `result` | `turn/start`; ends at `turn/completed` |
| Stop | `control_request` `interrupt` (the turn ends `error_during_execution`) | `turn/interrupt` (the turn ends `interrupted`) |
| Approval | `control_request` `can_use_tool`, answered by `control_response` allow (with the input) or deny | `item/commandExecution/requestApproval` and `item/fileChange/requestApproval`, answered `{decision: accept \| decline}` |

**Permission modes** (TASK-0135). `--permission-mode <id>` (serve only) picks how much the agent may do without
asking. `Harness.Core.Agents.AgentPermissionModes` describes each client's modes (id, label, one-line description,
default, unsafe) and the client settings they stand for; harness ui's start dialog is filled from it. Without the
option a session starts in Auto, the default of both clients; a resumed session (`--resume`) keeps the mode its
newest earlier host recorded in its state file. An unknown mode is a usage error and starts nothing.

| Client | Mode (id) | Client settings |
|---|---|---|
| Claude Code | Auto (`auto`, default) | `--permission-mode auto`: what Claude Code judges safe runs, the rest asks through the stdio permission prompt |
| | Manual (`manual`) | no `--permission-mode`: every tool that needs permission asks |
| | Accept edits (`acceptEdits`) | `--permission-mode acceptEdits` |
| | Plan (`plan`) | `--permission-mode plan`: reads and plans, changes nothing |
| | Bypass permissions (`bypassPermissions`, unsafe) | `--permission-mode bypassPermissions`: nothing asks |
| Codex | Auto (`auto`, default) | approval policy `on-request`, sandbox `workspace-write` |
| | Manual (`manual`) | `untrusted`, `workspace-write`: asks before any command not known to be safe |
| | Read only (`read-only`) | `on-request`, `read-only` |
| | Full access (`full-access`, unsafe) | `never`, `danger-full-access`: nothing asks, nothing is contained |

The ids follow Claude Code 2.1.318's `--permission-mode` choices (it also has `dontAsk`, which denies what is not
pre-approved and is not offered) and codex app-server 0.167.1's `AskForApproval` and `SandboxMode`
(`codex app-server generate-json-schema`). Under `on-request` Codex decides itself when to ask, and on Windows it ran a
command outside the working tree without asking: Codex's Auto relies on the `workspace-write` sandbox, and Manual
(`untrusted`) is the mode to choose when every command should be seen first. Approvals still come as `approval`
events in every mode that asks.

**Model catalog** (TASK-0142). harness keeps, per client, the models it can offer in the start dialog: a
machine-wide `agent-models.json` beside the global configuration (`Harness.Infrastructure.Agents.AgentModelStore`,
written through the machine file's lock and atomic replace, like `ui.json`), so every project and browser shares it.
`{version, clients: {<client>: {refreshed_at, source, cli_version, error, failed_at, models: [{id, label,
description, default, hidden, resolved, upgrade, source, last_seen}]}}}`. The data and the merging live in
`Harness.Core.Agents` (`AgentModel`, `AgentModelCatalog`, `AgentModelCatalogs`, `AgentModelProtocol`).

Sources, checked against the installed CLIs (recorded answers in `AgentModelCatalogTests`):

| Client | Source | What it gives |
|---|---|---|
| Codex | app-server `model/list` (codex-cli 0.167.1, `codex app-server generate-json-schema`: `ModelListParams` {cursor, limit, includeHidden}, `ModelListResponse` {data, nextCursor}) | per model `id`, `model` (the id a thread takes), `displayName`, `description`, `isDefault`, `hidden`, `upgrade`/`upgradeInfo`; harness reads up to 10 pages, hidden models are kept but not offered |
| Claude Code | the stream-json control request `initialize` (Claude Code 2.1.318; what the Agent SDK's `supportedModels()` reads) | `models: [{value, resolvedModel, displayName, description, …}]` and `claude_code_version`; `value` is what `--model` takes, `default` names the model the CLI defaults to (that alias becomes the catalog's default; the full id is added when no alias names it) |
| Claude Code, older | an initialize answer without `models` | the documented aliases `sonnet`, `opus`, `haiku` (source `alias`); nothing is guessed beyond them |
| both | the transcripts (`agent_usage`, read by `TranscriptReader`) | models a session used that the list does not name, as `seen` entries (not `<synthetic>`) |

Refresh:
- **A session's handshake.** `harness-agents serve --models <file>` (harness ui passes the file beside the global
  configuration) refreshes its client's catalog with no extra process: Claude's session sends `initialize` before its
  first turn and reads the `control_response` with everything else; Codex's sends `model/list` right after
  `initialized`, beside `thread/start`. Neither waits for the answer; source `session`.
- **A probe.** When the start dialog opens, `GET /api/projects/{id}/agent-models` (with `X-Harness-UI`) starts a
  background probe for each client whose catalog is empty or older than 24 h (`AgentModelCatalog.MaxAge`), at most
  one per client at a time and not again within 5 minutes of a failure: `claude -p --input-format stream-json
  --output-format stream-json --verbose --no-session-persistence` gets `initialize`, `codex app-server` gets
  `initialize`, `initialized` and `model/list`; the process runs in the global configuration's folder through
  `IProviderProcessFactory` and its whole tree is killed once it answers or after 30 s (`AgentModelProbe`). The
  endpoint answers at once with what the file holds and `refreshing: true`; the dialog asks again every 3 s while it
  is open. `HARNESS_AGENT_MODELS_OFFLINE=1` (set by the tests) turns probes off. Source `probe`.
- **Transcripts.** The endpoint first adds the project's `agent_usage` models as `seen`. This is how sessions harness
  did not start count: an MCP client's `initialize` (`clientInfo`) names the client and its version, never its models.
- A refresh replaces the client's own entries in the order it gave them and keeps the `seen` ones it does not name;
  a failed (or empty) refresh records `error` and `failed_at` and keeps the models and `refreshed_at`.

The endpoint answers `{clients: {<client>: {models (hidden left out), default: {id, label} | null, refreshed_at,
age_seconds, stale, refreshing, error, failed_at, source, cli_version}}, max_age_hours}`. `POST …/agents` still
takes any model id. The host keeps the id asked for as `requested_model` in its state; `GET …/tasks/{tid}/sessions`
marks a host whose id the client's (non-empty) catalog does not name with `model_unlisted`, and the session view says
so.

**The pipe.** `harness-agent-<key>` on Windows; on Linux and macOS `ha-<first 16 digits of the key>`, because .NET makes it a Unix socket at `$TMPDIR/CoreFxPipe_<name>` and macOS limits socket paths to 104 bytes. `PipeOptions.CurrentUserOnly`, 64 KiB buffers.
- Without a buffer, a write on Windows waits until the reader takes it, and a reader that stopped reading held the
  host open.
- A new connection first gets every event so far, then each new one.
- It sends JSON-line commands: `send` (text), `stop`, `approve` (id, allow), `close`. What cannot be done comes back as
  an `error` event; the host goes on.
- On close, a connection that does not take its last events within two seconds is cut off. A cancelled pipe read
  does not always return on Windows, so the pipe is closed rather than its read cancelled.

**Files.** Beside the project database, in `agents/` (which holds its own `.gitignore` of `*`):
- `<key>.json`: the state (client, task, session id, model, the model asked for, permission mode, pid, status
  `starting|idle|running|closed|failed`);
- `<key>.jsonl`: every event, numbered (`seq`);
- `<key>.err`: the host's standard error.

**Events.** Each event has a `seq` and an `at`:
- `session`, `status`, `user`, `text`, `tool`;
- `approval` and `answered`;
- `usage`;
- `turn` (`completed|stopped|failed`, `cost_usd`, `duration_ms`);
- `error`.

**The session row.** The task's `agent_sessions` row (origin `harness`) is:
- `working` from the moment a turn starts;
- then `idle`, `cancelled` or `failed`.

**Idle close.** A host closes its session after `--idle-minutes` (default 60) without a turn or a command.

**harness ui** (`Harness.Cli/Ui/UiAgents`):
- It starts hosts through `DetachedProcess`, so they outlive harness ui. `HARNESS_AGENTS_PATH`,
  `HARNESS_CLAUDE_COMMAND` and `HARNESS_CODEX_COMMAND` override the executables.
- It connects to every live host of a task it shows, and finds hosts again after a restart by their state files.
- Events reach the page as `event: agent` on `/api/events` (`{project, key, events}`, drained at each live tick).
  Without the stream, the open panel polls `GET /api/projects/{id}/agents/{key}/log?after=<seq>`.
- Writes:
  - `POST …/tasks/{tid}/agents` (client, prompt, model, resume, mode: a permission mode id; empty for the default,
    or for a resumed session the mode it started with; an unknown one is refused with 400);
  - `POST …/agents/{key}/send|stop|approve|close`.
- `GET …/tasks/{tid}/sessions` carries `permission_modes` ({client: [{id, label, description, default, unsafe}]})
  for the start dialog's Permission mode box, which follows the chosen agent, preselects its default and marks the
  unsafe modes; each host carries its `permission_mode`, shown in its session and live panel.
- `GET …/agent-models` fills the start dialog's Model box (see Model catalog): Default first (named by the
  catalog's default when known), the catalog's models by label with the id in the tooltip (`seen` ones marked), then
  Other…, which shows a field for any id. Switching the agent reloads the list and resets it to Default; a refresh in
  progress or a failed one is a quiet note under the box.
- A session started elsewhere is read from the end of its transcript:
  `GET …/tasks/{tid}/sessions/{client}/{sid}/tail`.
