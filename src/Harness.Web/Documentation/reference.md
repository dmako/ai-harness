# Reference

What every command and setting does. `harness <command> --help` shows a
command's flags; the "Using harness" guide (`harness docs guide`) explains
how to work with it.

## Install, upgrade, uninstall

Harness needs Git and nothing else: no Node, no Python, no network access.

1. Put `harness` (and `harness-graphify`, only needed for C# projects) on
   your `PATH`, e.g. `~/.local/bin`. On macOS and Linux make them
   executable (`chmod +x`); on macOS the first run may need
   `xattr -d com.apple.quarantine harness`.
2. In a repository you work on:

   ```sh
   harness init        # creates .harness/ (kept out of Git)
   harness install     # in a terminal: choose MCP server, agent instructions,
                       # hooks and shell completion, review, apply
   harness doctor      # checks everything, with fixes
   ```

   Without a terminal (scripts): `harness install --client all`, then
   `harness instructions` and `harness hooks install --kind git`.
3. Restart Claude Code / Codex so they pick up the harness MCP server.

**Upgrade:** replace the binaries, then run `harness migrate`. It migrates
the global config, the knowledge databases and every registered project
(config, database, the guidance, hooks and MCP entries already installed),
then syncs each project's worktrees. Before a database is migrated, harness
copies it to `.harness/backups/` (the last 5 are kept). An MCP entry or hook
whose executable no longer exists is pointed at the binary running `migrate`;
one whose executable still exists keeps it.

**Uninstall:** `harness uninstall --all --project <repository>` removes
everything harness added (MCP entries in every scope, Git and client hooks,
the agent instructions, shell completion) and nothing else; `--dry-run`
shows it first. The project's data in `.harness/` stays unless you also pass
`--delete-data`. Then delete the binaries. In a terminal, `harness
uninstall` alone lets you pick what to remove.

## Commands

| Command | What it does |
|---|---|
| `harness init` | Creates `.harness/` for the repository (safe to rerun), and the project and global `config.toml` with every option commented out; run again after an upgrade, it brings both up to date (see Configuration). |
| `harness migrate` | After an upgrade: brings the machine (global config, knowledge databases, user-scope MCP entries and hooks) and then every registered project (what `init` does, plus the guidance, hooks and MCP entries it already has) up to date, and syncs every worktree that exists. Projects run in parallel (`--jobs N`, default the processor count up to 4). `--full` makes the syncs full ones, `--dry-run` changes nothing, and `--json` gives a per-project result. Exits 1 when a project failed or was skipped (its folder is missing; it stays registered). |
| `harness install` / `uninstall` | In a terminal: the setup wizard. With flags: adds or removes harness in Claude Code / Codex (`--client`, `--scope`, `--dry-run`). |
| `harness doctor` | Checks the installation, database, clients, hooks and providers, with a fix for each problem (`--all`: every project on the machine). |
| `harness status` | The project, its Git state, database and clients. |
| `harness sync` | Refreshes the recorded state (branch, HEAD, files, findings). The Git hooks run it for you. |
| `harness instructions` | Adds the harness guidance for agents to AGENTS.md / CLAUDE.md (`--remove`). |
| `harness hooks` | Optional hooks: Git sync, Claude Code and Codex tool events (`install`, `uninstall`, `status --kind git,claude-code,codex`). |
| `harness tasks` | The task queue: list, show, next, claim, complete, block, defer, ready, release, import. |
| `harness memory` | One memory: `show`, `retire --reason`, `promote --to`, `share` and `withdraw` (see Shared knowledge). |
| `harness proposals` | Knowledge agents proposed: list, show, approve, reject. |
| `harness projects` | Every repository on this machine that uses harness (`forget <id>` drops one from the list). |
| `harness workspace` | Groups related repositories for cross-project search and knowledge. |
| `harness search <query>` | Code search here, or across the workspace (`--workspace`). |
| `harness providers sessions` | graphify-csharp's warm C# sessions: list, start, restart, stop. |
| `harness report` | What you can act on so harness serves your agents better (providers, hooks, instructions, knowledge), each with a suggested action. How harness itself gathers and serves is for its developer: it reaches them only in the opt-in telemetry. |
| `harness ui` | Opens the local GUI over every project on this machine. |
| `harness docs [section]` | This documentation. |
| `harness completion` | Shell completion for zsh, bash, fish and PowerShell. |
| `harness telemetry` | Opt-in export of aggregated usage reports (off by default). |
| `harness mcp` | The MCP server the clients start; you do not run it yourself. |

## Connecting Claude Code and Codex

`harness install` run bare in a terminal is a setup wizard, one screen per
section, each a checklist checked where already installed: MCP server
(Claude Code per scope, Codex), agent instructions, hooks and shell
completion. Space toggles, enter moves on, `b` goes back, `q` cancels;
nothing is written until you confirm the list of changes.

With flags it asks nothing:

```sh
harness install --client all --dry-run   # show the exact lines that would change
harness install --client all             # Claude Code user scope + Codex
harness install --scope project --client claude-code   # .mcp.json in this repository
harness uninstall --scope all            # remove harness's entry only
```

| Client | Scope | File |
|---|---|---|
| Claude Code | `user` (default) | `~/.claude.json` |
| Claude Code | `project` | `<repository>/.mcp.json` (shareable) |
| Claude Code | `local` | `~/.claude.json`, for this repository only |
| Codex | always user | `~/.codex/config.toml` |

Harness changes only its own `harness` entry and keeps everything else in
the file byte for byte; it writes a backup (`<file>.bak-harness-<time>`)
before each change. Claude Code rewrites `~/.claude.json` while it runs:
install while it is closed, or use `--scope project`.

## Agent instructions

`harness instructions` writes a short marked section telling agents to use
harness; it holds no project knowledge. Codex reads `AGENTS.md`, Claude Code
`CLAUDE.md`: the section goes into `AGENTS.md` and `CLAUDE.md` imports it
(`@AGENTS.md`), created if missing. Text outside the markers is never
touched; `--remove` takes only the section out. `harness doctor` warns when
Claude Code would not see the section.

## Hooks

All optional (`harness hooks install --kind …`):

- **git**: Git hooks that run `harness sync` when you commit, check out,
  merge or rebase, so harness always knows the current branch and files.
  On Windows they need Git for Windows.
- **claude-code**: a hook after each tool call that records what the agent
  read, edited and called (names and repository paths only, never
  contents), to measure how well `get_context` answered; and a
  session-start hook telling the agent, in a repository using harness, to
  start with `get_context` and to tell its subagents the same.
- **codex**: the tool-call hook for Codex (Codex asks once to trust it,
  under `/hooks`).

harness ui shows per project which hooks are installed and how many events
they recorded (Agents › Tools & providers › Hooks). If installed hooks
record nothing, start an agent session with `HARNESS_HOOK_DEBUG=1` in its
environment: each event then prints "recorded" or "dropped: <why>" in the
hook's output (Claude Code: verbose mode); no output means the hook command
does not run. `harness doctor` warns about hook commands that cannot run;
`harness hooks install` again rewrites them.

## Providers

Harness answers agents' code questions (`get_context`, `search_project`,
`package_api`) from providers: other tools it starts and asks, merging their
answers. All are optional; without any, harness still keeps knowledge and
tasks and reads project structure itself. Declare them in the global
configuration (see Configuration); a table named after a provider uses
harness's built-in mapping for it, so you only give the executable:

```toml
[providers.codebase-memory]        # symbol and text search, overview (any language)
command = "/path/to/codebase-memory-mcp"

[providers.ripwire]                # ranked symbol and text search, name-based callers
command = "/path/to/ripwire"

[providers.rg-fd]                  # text and file search
command = "/path/to/rg-fd-mcp"

[providers.graphify-csharp]        # compiler-exact C# callers, references, impact
command = "/path/to/harness-graphify"
args = ["--graphify", "~/.dotnet/tools/graphify-csharp.exe"]

[providers.nuget]                  # package_api for C# packages
command = "dnx"
args = ["DimonSmart.NugetMcpServer@1.1.10", "--yes"]
```

Install each tool as its own documentation says (graphify-csharp and
NugetMcpServer are .NET tools: `dotnet tool install -g graphify-csharp`,
`dotnet tool install -g DimonSmart.NugetMcpServer`). Every provider also takes
`enabled = false` and `timeout_ms`. `harness doctor` checks each one and
says what is missing.

- **codebase-memory** answers from an index: if the repository is not
  indexed, doctor says so with the command that indexes it.
- **graphify-csharp** keeps a C# solution compiled in a warm session.
  Loading a large solution takes a minute or more; harness keeps the
  session warm for the next agent session and stops it after 2 hours
  without queries. `harness providers sessions` and harness ui (Machine, and
  the project's Tools & providers) show the sessions and restart them. When
  several solutions sit below the repository root, choose one by adding
  `"--", "--input", "App.slnx"` to `args`. If doctor's "graphify session"
  line says *discovery incomplete*, graphify could not read part of the
  solution and every query runs slowly; the line names the cause (seen:
  the `Microsoft.AspNetCore.OpenApi` package) and the fix.
- **nuget** answers `package_api` (a package's public API at the version
  you use). Packages from a private feed need the feed and its credentials
  in your NuGet configuration (`dotnet nuget update source <name> -u <user>
  -p <token> --store-password-in-clear-text`); harness says plainly when the
  feed cannot be reached.

Agents should talk to harness, not to the providers directly: harness merges
their answers, records how they fared and adds the project's knowledge.

## Configuration

TOML, in increasing precedence: built-in defaults, the global file, the
project file. Missing files are fine; a malformed file, an unknown key or an
invalid value is rejected with an error naming the file.

| Level | Path |
|---|---|
| global | `$HARNESS_CONFIG_DIR/config.toml` if set, else macOS `~/Library/Application Support/harness/config.toml`, Linux `~/.config/harness/config.toml` (`$XDG_CONFIG_HOME`), Windows `%AppData%\harness\config.toml` |
| project | `<repository>/.harness/config.toml` |

```toml
[storage]
db_path = ".harness/harness.db"   # relative to the repository root
busy_timeout_ms = 5000

[log]
level = "info"                    # debug | info | warn | error

[sync]
ignore = ["vendor/", "*.min.js"]  # on top of .gitignore

[architecture.complexity]         # cyclomatic complexity of functions
languages = ["csharp", "go"]      # add "typescript" for TypeScript/JavaScript
note = 11                         # lowest value reported as a note
warn = 21                         # ... as a warning
problem = 51                      # ... as a problem

[architecture.complexity.typescript]  # optional: one language's own thresholds
warn = 31

[architecture.complexity.metrics]     # exact .NET values (Windows, .NET SDK)
enabled = false                       # true: sync starts a background run per HEAD
timeout_ms = 600000                   # a longer run fails and keeps the counted values

[providers.<name>]                # see Providers
```

Providers belong in the global file (executable paths are machine
specific). `harness doctor` prints which files were read.

`harness init` writes both files with every option commented out, as an
example to uncomment. Run again after upgrading harness, it brings them up
to date line by line: an option the new version adds appears, commented
out, under its table (a missing table at the end); the example of an option
it no longer has is removed, and so is your setting of an option it dropped.
Everything else, your settings and comments included, stays as you wrote
it. A setting harness never had (a typo) still stops `harness init` with an
error naming the file, before anything is written. A file that is not valid
TOML is left alone, and `--dry-run` shows what would change.

## Knowledge types

Knowledge is one statement with a type and a scope (the project, a path,
a subsystem, a branch; wider: a workspace or the user). Agents record it
(`memory_put`, or `memory_propose` for you to approve); you add, correct,
retire or promote it in harness ui (a project's Knowledge, Your knowledge,
a workspace's Knowledge). The type says how it binds an agent:

| Type | What it is | Example | Reaches agents |
|---|---|---|---|
| goal | What the work is for: an outcome to optimize toward. | Optimize code intelligence for C#/.NET first. | `goals_current`; the top goals in every `get_context` |
| constraint | A hard limit set from outside: what must or must not happen, not ours to change. | Keycloak must not be changed for now. | `get_context`, `decisions_for`, `constraints_current` |
| decision | A choice made between alternatives, with its reason. | Live updates use Server-Sent Events, not WebSocket. | `get_context`, `decisions_for` |
| convention | How code and work are done here, in every new change. | CancellationToken is always the last parameter. | `get_context`, `decisions_for`; offered to other projects |
| warning | A trap: what breaks or misleads, and how to avoid it. | graphify-csharp ignores SIGTERM: stop sessions by id. | `get_context`, `decisions_for` |
| fact | Something true that the code does not show. | Tenant GUIDs originate in a Confluence table. | `get_context` where it applies or matches the task; `memory_search` |
| open_question | Not decided yet: what is open and what it waits for. | Do roles exist on the organization server? | `get_context` where it applies or matches the task (agents ask rather than decide it); `memory_search` |

History (what was added when) belongs in Git, not in knowledge.

## Shared knowledge

`.harness/knowledge.toml` holds the memories the team shares through Git.
Harness writes it; you commit, review and merge it like code.

| Command | What it does |
|---|---|
| `harness memory share <id> [--json]` | Writes a current project memory (scope project, path, subsystem or branch) to the file and marks it PROJECT. A superseded, retired, workspace or user memory is refused, as is a file with problems (fix it first). Warns when Git ignores the file, with the fix. harness ui: Share with project on the memory's card. |
| `harness memory withdraw <id> [--json]` | Removes the memory's entry; the memory stays current and local. harness ui: Withdraw from project. |

The file:

```toml
version = 1

[[memory]]
id = "mem_3f2a9c0d1e4b5a67"
type = "convention"
scope = "path:src/Harness.Core"
content = "CancellationToken is always the last parameter"
source_ref = "review of TASK-0042"
shared_by = "human:dana"
shared_at = "2026-10-31T21:00:00Z"
```

- **Fields**: `id` (`mem_` followed by letters, digits, `_` or `-`),
  `type` (one of the knowledge types), `scope` (`project` if left out, or
  `path:`, `subsystem:` or `branch:`), `content` (the statement, at most
  4000 bytes), and optional `source_ref`, `shared_by` and `shared_at`.
- **Order**: harness writes the entries sorted by id, with the keys in the
  order above, empty ones left out, LF line endings and no BOM. The same
  memories always give the same bytes, so concurrent changes merge cleanly.
- **Ids**: an entry keeps the id its memory had when first shared. A
  correction rewrites the entry's statement under that id, and on every
  clone the entry stands for the current memory along that memory's
  history.
- **Validation**: a bad entry is reported with its line and skipped while
  the others load: no or a malformed id, an unknown type, empty or too long
  content, an invalid scope or a workspace or user one, a value that is not
  text, or an id listed twice. Unknown keys and tables are reported too.
  A file that is not valid TOML, or one written by a newer harness
  (`version` above 1), loads nothing. While the file has problems, nothing
  is marked removed.
- **Reading**: `harness sync` (which the Git hooks run) and agents'
  knowledge reads merge the file when its SHA-256 changed. An unchanged
  file costs one hash comparison. `harness sync` prints what changed
  (`Knowledge` line) and the file's problems.
- **Merging**: a new entry becomes a memory with the entry's id, its
  source `import` with the entry's `source_ref` and `shared_by`. A local
  memory with the same type, scope and statement is superseded by it
  (linked). A changed entry supersedes the current memory, and a memory
  retired here stays retired while its entry is unchanged. A memory whose
  entry is gone stays current, marked removed. It waits in Knowledge ›
  Inbox, where Keep as local or Retire answers it.
- **Local edits**: correcting a PROJECT memory (`memory_supersede`,
  harness ui Correct, approving a proposal that supersedes it) rewrites
  its entry; retiring or promoting it removes the entry.
- **Git**: `harness doctor` and Health have a `knowledge` check once the
  file exists. It warns when a `.gitignore` rule hides the file (typically
  `.harness/` in the root `.gitignore`), with the fix: ignore `.harness/*`
  and add `!.harness/knowledge.toml`, or drop the rule.
- **Worktrees** share one database, each with its own file: moving between
  worktrees whose files differ marks entries removed and back.

## Workspaces

A workspace groups repositories that belong together (services and the
libraries they share): `harness workspace create <name> --discover <folder>`
finds them, `join`/`leave` change membership. Knowledge recorded at
workspace level applies to every member; `harness search --workspace` and
the agents' `scope: "workspace"` search all members; harness ui shows the
workspace's architecture, shared code and version differences.

## Tasks

Tasks come from a backlog file (`docs/backlog.yaml` by default):
`harness tasks import --help` describes the format with an example, and
`harness tasks import` loads it (re-importing updates definitions, never
progress). Agents take tasks with `task_next` and complete them with
evidence for every acceptance criterion; you follow them with `harness tasks
list` or in harness ui (Work).

| Command | What it does |
|---|---|
| `harness tasks add --title … [--goal --accept … --depends … --blueprint --type --priority --scope --prefix --id]` | Adds a task by hand with the next free id of the prefix (default: the prefix of the backlog's newest task). `--depends` takes this project's task ids, or `<project>:<task id>` for another registered project's task (its short name, folder name, parent/folder or id, any case; a name two projects share is refused): the task stays in the backlog until that one is done, and goes back if it reopens (its status is read again after 30 s). Backlog files and `task_propose` take the same form. harness ui: Work › Tasks › New task, whose dependency search offers the workspace's other projects' open tasks. |
| `harness tasks blueprints` | The blueprints (formerly milestones): what each group of tasks is for, its exit criteria and progress. `harness tasks list --blueprint M5` and the agents' `task_next` take one blueprint's tasks. The former names (`tasks milestones`, `--milestone`, the backlog keys `milestones:` and `milestone:`) are still accepted. harness ui: Work › Blueprints, where blueprints are also created, edited and removed (with their tasks moved or deleted). Listed in the order the author gave them in harness ui (drag a blueprint, or use its up and down arrows: its header in Work › Tasks while all blueprints are shown, or its card in Work › Blueprints), then the ones not yet placed in the backlog's order; each placed one has `order` in `--json` and in `task_blueprints`, which agents list them with. |
| `harness tasks blueprints add [--id --title --goal --exit … --notes --color]` | Adds a blueprint: the next free id (M17 after M16, or BP-001) and the name "New blueprint" unless given, and the first of the 16 colours no other blueprint uses unless `--color` names one (teal, orange, blue, rose, green, violet, amber, cyan, pink, lime, indigo, red, fuchsia, yellow, slate, purple). |
| `harness tasks blueprints edit <id> [--title --goal --exit … --notes --color]` | Restates a blueprint; what is not given stays, `--exit ""` clears its exit criteria. Its id stays. `--color` sets its colour on harness ui's Board and in Work › Tasks; an unknown colour is refused. |
| `harness tasks blueprints remove <id> [--tasks move/delete --to <blueprint>]` | Removes a blueprint. One with tasks needs `--tasks move` (to `--to`, or to no blueprint without it; `--to` alone moves too) or `--tasks delete`, which is refused while an agent holds one of them; tasks elsewhere lose their dependencies on deleted ones. |
| `harness tasks export [file]` | Writes the tasks the backlog file lacks (added by hand, approved proposals, tasks made from findings) at the end of its `tasks:` list; the rest of the file is kept as it is. A missing file is written whole. |
| `harness tasks proposals [--status]` | The tasks and blueprints agents proposed (`task_propose`, `task_blueprint_propose`). `harness tasks approve <id>` makes the task, or makes or restates the blueprint (a new one gets the next free id); `harness tasks reject <id> --note …` closes the proposal. harness ui: Work › Inbox, where a proposal can be restated before it is approved. A proposed task's blueprint is matched by id or title when the task is approved, so tasks proposed with a new blueprint (named by its title) join it whichever is approved first; with no match, the blueprint is created with the next free id. A new blueprint whose title a blueprint has already asks whether to merge into that one (it is restated, its id and tasks stay) or create a separate one: `--same-name merge` or `--same-name new`, without which it is refused. |

Each task's audit trail (`harness tasks show <id> --events`) says how it
was made: imported, added with `harness tasks add`, added in harness ui, or
proposed by an agent and approved by you. A ready or backlog task can be
edited in harness ui (Work › Tasks, Edit…); the edit is recorded as a
`definition_updated` event with you as its actor. Removing a blueprint in
harness ui records a `moved` event on each task it moves, and a
`dependency removed` event on each task that lost a dependency on a task
deleted with it; the deleted tasks' own trails go with them.

## harness ui

The default listener is `127.0.0.1:17477`, or `HARNESS_UI_ADDR` when that is set (a loopback address, as for `--addr`). Use `--addr` to override it and `--no-open` to leave the browser closed.

`harness ui` opens a local page (loopback only, nothing leaves the machine)
over every project on this machine: overview, knowledge (with the Inbox of
proposals to approve), work, how harness serves the agents and what to
change so it serves them better, architecture findings, health, and the
Docs. How harness itself performs (relevance per provider, latency) is the
harness developer's, from opt-in telemetry, and not part of harness ui.
Changes made there (approving proposals, adding knowledge, sharing it with
the team or withdrawing it, creating,
editing, moving or deleting tasks and blueprints, marking findings,
workspace membership, graphify sessions, Fix it) are recorded as yours.

## Troubleshooting

- **Start with `harness doctor`** in the repository: every problem comes
  with a fix. `harness doctor --all` checks every project on the machine.
- **Agents do not use harness:** check that `CLAUDE.md` holds or imports the
  harness section (`harness instructions`), that the MCP server is installed
  for the client (`harness install`), and restart the client. The
  session-start hook (`harness hooks install --kind claude-code`) reminds
  agents at each session.
- **A duplicate project in the list:** `harness projects` shows both;
  `harness doctor` in the repository shows the right ID; `harness projects
  forget <the other id>` removes the extra one (the real one registers
  itself again at the next sync anyway).
- **Slow or empty C# answers:** see graphify-csharp under Providers.
- **Windows:** use Windows Terminal, PowerShell or cmd for the wizard; Git
  hooks need Git for Windows; shell completion is PowerShell's
  (`harness completion install`).
