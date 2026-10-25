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

**Upgrade:** replace the binaries. Before a project database is migrated,
harness copies it to `.harness/backups/` (the last 5 are kept). Rerun
`harness hooks install` after an upgrade if hooks were installed, so their
commands match the new binary.

**Uninstall:** `harness uninstall --all --project <repository>` removes
everything harness added (MCP entries in every scope, Git and client hooks,
the agent instructions, shell completion) and nothing else; `--dry-run`
shows it first. The project's data in `.harness/` stays unless you also pass
`--delete-data`. Then delete the binaries. In a terminal, `harness
uninstall` alone lets you pick what to remove.

## Commands

| Command | What it does |
|---|---|
| `harness init` | Creates `.harness/` for the repository (safe to rerun). |
| `harness install` / `uninstall` | In a terminal: the setup wizard. With flags: adds or removes harness in Claude Code / Codex (`--client`, `--scope`, `--dry-run`). |
| `harness doctor` | Checks the installation, database, clients, hooks and providers, with a fix for each problem (`--all`: every project on the machine). |
| `harness status` | The project, its Git state, database and clients. |
| `harness sync` | Refreshes the recorded state (branch, HEAD, files, findings). The Git hooks run it for you. |
| `harness instructions` | Adds the harness guidance for agents to AGENTS.md / CLAUDE.md (`--remove`). |
| `harness hooks` | Optional hooks: Git sync, Claude Code and Codex tool events (`install`, `uninstall`, `status --kind git,claude-code,codex`). |
| `harness tasks` | The task queue: list, show, next, claim, complete, block, defer, ready, release, import. |
| `harness projects` | Every repository on this machine that uses harness (`forget <id>` drops one from the list). |
| `harness workspace` | Groups related repositories for cross-project search and knowledge. |
| `harness search <query>` | Code search here, or across the workspace (`--workspace`). |
| `harness providers sessions` | graphify-csharp's warm C# sessions: list, start, restart, stop. |
| `harness metrics` / `report` | How the tools and providers fared; findings with a suggested action. |
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
args = ["--graphify", "/path/to/graphify-csharp"]

[providers.nuget]                  # package_api for C# packages
command = "/path/to/NugetMcpServer"
```

Install each tool as its own documentation says (graphify-csharp and
NugetMcpServer are .NET tools: `dotnet tool install -g graphify-csharp`,
`dotnet tool install -g NugetMcpServer`). Every provider also takes
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

[architecture.complexity]         # cyclomatic complexity of C# members
note = 11                         # lowest value reported as a note
warn = 21                         # ... as a warning
problem = 51                      # ... as a problem

[providers.<name>]                # see Providers
```

Providers belong in the global file (executable paths are machine
specific). `harness doctor` prints which files were read.

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
| `harness tasks add --title … [--goal --accept … --depends … --milestone --type --priority --scope --prefix --id]` | Adds a task by hand with the next free id of the prefix (default: the prefix of the backlog's newest task). harness ui: Work › Tasks › New task. |
| `harness tasks export [file]` | Writes the tasks the backlog file lacks (added by hand, approved proposals, tasks made from findings) at the end of its `tasks:` list; the rest of the file is kept as it is. A missing file is written whole. |
| `harness tasks proposals [--status]` | The tasks agents proposed. `harness tasks approve <id>` makes the task; `harness tasks reject <id> --note …` closes the proposal. harness ui: Work › Inbox. |

Each task's audit trail (`harness tasks show <id> --events`) says how it
was made: imported, added with `harness tasks add`, added in harness ui, or
proposed by an agent and approved by you.

## harness ui

The default listener is `127.0.0.1:17477`. Use `--addr` to override it and `--no-open` to leave the browser closed.

`harness ui` opens a local page (loopback only, nothing leaves the machine)
over every project on this machine: overview, knowledge (with the Inbox of
proposals to approve), work, the agents' tool use and `get_context` quality,
architecture findings, and the Docs. Changes made there (approving
proposals, marking findings, workspace membership, graphify sessions) are
recorded as yours.

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
