# Agents

Source: [`src/Harness.Agents`](../../src/Harness.Agents), contract in
[`src/Harness.Core/Agents`](../../src/Harness.Core/Agents).

`harness-agents` runs one agent turn for a task and owns the client's process. It is a separate Native AOT executable
next to `harness` (TASK-0115), so a session started from harness ui outlives harness ui, and harness itself carries no
agent code. Why harness speaks the clients' own protocols instead of Microsoft Agent Framework:
[the spike](maf-sessions-spike.md).

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
| Process | `claude -p --input-format stream-json --output-format stream-json --verbose --permission-prompt-tool stdio` with `--session-id` or `--resume` | `codex app-server` (JSON-RPC): `initialize`, `thread/start` or `thread/resume` (approval policy `untrusted`, sandbox `workspace-write`) |
| A turn | a `user` message on standard input; ends at `result` | `turn/start`; ends at `turn/completed` |
| Stop | `control_request` `interrupt` (the turn ends `error_during_execution`) | `turn/interrupt` (the turn ends `interrupted`) |
| Approval | `control_request` `can_use_tool`, answered by `control_response` allow (with the input) or deny | `item/commandExecution/requestApproval` and `item/fileChange/requestApproval`, answered `{decision: accept \| decline}` |

`on-request`, Codex's default, let Codex run a command outside the working tree without asking on Windows. A session
a person supervises asks first instead (`untrusted`).

**The pipe.** `harness-agent-<key>` on Windows; on Linux and macOS `ha-<first 16 digits of the key>`, because .NET makes it a Unix socket at `$TMPDIR/CoreFxPipe_<name>` and macOS limits socket paths to 104 bytes. `PipeOptions.CurrentUserOnly`, 64 KiB buffers.
- Without a buffer, a write on Windows waits until the reader takes it, and a reader that stopped reading held the
  host open.
- A new connection first gets every event so far, then each new one.
- It sends JSON-line commands: `send` (text), `stop`, `approve` (id, allow), `close`. What cannot be done comes back as
  an `error` event; the host goes on.
- On close, a connection that does not take its last events within two seconds is cut off. A cancelled pipe read
  does not always return on Windows, so the pipe is closed rather than its read cancelled.

**Files.** Beside the project database, in `agents/` (which holds its own `.gitignore` of `*`):
- `<key>.json`: the state (client, task, session id, pid, status `starting|idle|running|closed|failed`);
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
  - `POST …/tasks/{tid}/agents` (client, prompt, model, resume);
  - `POST …/agents/{key}/send|stop|approve|close`.
- A session started elsewhere is read from the end of its transcript:
  `GET …/tasks/{tid}/sessions/{client}/{sid}/tail`.
