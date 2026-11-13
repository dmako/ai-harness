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
| `harness doctor` | Checks the installation, database, clients, hooks, providers, NuGet package sources and SCM connections, with a fix for each problem (`--all`: every project on the machine; `--fix nuget-config`, with `--dry-run` to only show it: writes the nuget provider's project override). |
| `harness status` | The project, its Git state, database and clients. |
| `harness sync` | Refreshes the recorded state (branch, HEAD, files, findings). The Git hooks run it for you. |
| `harness instructions` | Adds the harness guidance for agents to AGENTS.md / CLAUDE.md (`--remove`). |
| `harness hooks` | Optional hooks: Git sync, Claude Code and Codex tool events (`install`, `uninstall`, `status --kind git,claude-code,codex`). |
| `harness tasks` | The task queue: list, show, next, claim, complete, block, defer, ready, release, import. |
| `harness memory` | One memory: `show`, `retire --reason`, `promote --to`, `share` and `withdraw` (see Shared knowledge). |
| `harness proposals` | Knowledge agents proposed: list, show, approve, reject. |
| `harness projects` | Every repository on this machine that uses harness (`forget <id>` drops one from the list). |
| `harness workspace` | Groups related repositories for cross-project search and knowledge. |
| `harness scm` | This machine's SCM connections (GitLab, Forgejo, GitHub): `list`, `add`, `set`, `remove`, `test`, `alias`, `use`, `which` (see SCM connections). |
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
  contents), to measure how well `get_context` answered, and which
  session claimed, updated or completed which task (the task's id only;
  Work › Tasks and `task_get` list a task's agent sessions); and a
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
tasks and reads project structure itself. Text and file search are built
in (see Built-in search below). Declare the others in the global
configuration (see Configuration); a table named after a provider uses
harness's built-in mapping for it, so you only give the executable:

```toml
[providers.codebase-memory]        # symbol and text search, overview (any language)
command = "/path/to/codebase-memory-mcp"

[providers.ripwire]                # ranked symbol and text search, name-based callers
command = "/path/to/ripwire"

[providers.graphify-csharp]        # compiler-exact C# callers, references, impact
command = "/path/to/harness-graphify"
args = ["--graphify", "~/.dotnet/tools/graphify-csharp.exe"]

[providers.nuget]                  # package_api for C# packages
command = "dnx"
args = ["DimonSmart.NugetMcpServer@1.1.10", "--yes", "--source", "https://api.nuget.org/v3/index.json"]
```

Install each tool as its own documentation says (graphify-csharp and
NugetMcpServer are .NET tools: `dotnet tool install -g graphify-csharp`,
`dotnet tool install -g DimonSmart.NugetMcpServer`). Every provider also takes
`enabled = false` and `timeout_ms`. `harness doctor` checks each one and
says what is missing.

- **codebase-memory** answers from an index: if the repository is not
  indexed, doctor says so with the command that indexes it. A sync that
  hears it does not know the repository remembers that and does not start
  it again for this repository (the sync still says why it is skipped)
  until a repository is indexed in its index directory (`CBM_CACHE_DIR`, or
  `~/.cache/codebase-memory-mcp`), its configuration changes, or a day has
  passed.
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
  feed cannot be reached. A repository with its own `nuget.config` may need
  a project override: see below.

### Projects with their own nuget.config

A `nuget.config` in the repository (or a folder above it) changes where
packages come from: `<clear/>` drops nuget.org, `packageSourceMapping`
sends package ids to named feeds only, and private feeds need credentials.
It affects the nuget provider twice, as measured with the .NET SDK
11.0.100 and NugetMcpServer 1.1.10:

- **dnx fetching the server.** dnx reads the NuGet configuration of the
  folder it starts in (that folder and every folder above it, then your
  user-level `NuGet.Config`). `--source <url>` replaces all of it, package
  source mapping included, so the documented command line keeps working
  with `<clear/>` and mapping. Without `--source`, dnx fails when nuget.org
  is cleared or mapping gives `DimonSmart.NugetMcpServer` no nuget.org
  source, and when any configured feed does not answer (unless you add
  `--ignore-failed-sources`). `--add-source <url>` adds a feed, but dnx
  refuses it while any of those files has package source mapping;
  `--configfile <file>` reads that one file instead of the folders and the
  user-level file. Once the exact version is in the global packages folder
  (`~/.nuget/packages`), dnx starts it without asking any feed, so a broken
  configuration shows only on a machine (or CI agent) that has not fetched
  it yet.
- **The server finding your packages.** NugetMcpServer reads the NuGet
  configuration of the folder *it* starts in (the machine, user and folder
  files, as `dotnet restore` does), searches every enabled source in it,
  ignores package source mapping and falls back to nuget.org when there is
  none. Without `dir = "project"`, harness starts it in harness's own
  working folder, which is the repository only when an agent started
  harness there. dnx keeps the options before `--` for itself; the
  arguments after `--` go to the server: `--source <url or folder>`
  (repeatable; these replace the configured sources) and `--nuget-config
  <file>`. The server also reads `NUGET_SOURCES` and `NUGET_CONFIG` from
  its environment.

`harness doctor` (and Health, and the nuget card in Agents › Tools &
providers) checks the provider against the repository's NuGet
configuration as `nuget-config`. When one of the above would stop it, the
check names the file and prints the project override that fixes it.
`harness doctor --fix nuget-config` writes that override into the
project's `.harness/config.toml` (`--dry-run` only shows it); in harness ui,
Fix shows the change and writes it when you confirm. The rest of the file,
comments included, stays as it was, a second run changes nothing, and
removing the table undoes it.

A project's `[providers.nuget]` table takes precedence over the global
file's and replaces it whole, so give `command` and `args` again (and any
other key you set globally, such as `timeout_ms`). Examples:

```toml
# .harness/config.toml: a private feed beside nuget.org, or <clear/> with
# package source mapping: dnx takes the server from nuget.org, and the
# server, started in the project, reads the project's nuget.config
[providers.nuget]
command = "dnx"
args = ["DimonSmart.NugetMcpServer@1.1.10", "--yes", "--source", "https://api.nuget.org/v3/index.json"]
dir = "project"
```

```toml
# without package source mapping you may add nuget.org to the project's
# feeds instead of replacing them (dnx then needs every feed to answer)
[providers.nuget]
command = "dnx"
args = ["DimonSmart.NugetMcpServer@1.1.10", "--yes", "--add-source", "https://api.nuget.org/v3/index.json"]
dir = "project"
```

```toml
# the server reads one NuGet configuration file you name, wherever it starts
[providers.nuget]
command = "dnx"
args = ["DimonSmart.NugetMcpServer@1.1.10", "--yes", "--source", "https://api.nuget.org/v3/index.json",
        "--", "--nuget-config", "C:/src/app/nuget.config"]
```

Credentials belong in the NuGet configuration, never in `config.toml`
(which is committed): store them in your user-level `NuGet.Config` with
`dotnet nuget update source <name> -u <user> -p <token>`, or let the
repository's `nuget.config` read them from environment variables in its
`packageSourceCredentials` (`<add key="ClearTextPassword"
value="%FEED_TOKEN%" />`). Not verified: no authenticated feed was at hand,
so that the server uses stored credentials is inferred from the NuGet
libraries it loads its sources with; its 1.1.10 binary sets up no NuGet
credential service, so a credential provider plugin (such as Azure
Artifacts') may serve `dotnet restore` but not the server. Prefer stored
credentials or a token in an environment variable for it.

### NuGet package upgrades and vulnerabilities

Architecture's package marks and the `vulnerable-package` and
`outdated-package` findings come from the NuGet V3 protocol of the sources
the repository's NuGet configuration resolves to (enabled http(s) sources,
package source mapping applied; local folder sources are not read):

- **Versions:** `PackageBaseAddress/3.0.0` (the flat container,
  `{base}/{id-lower}/index.json`). The newest version without a pre-release
  label above the one in use is offered, compared by NuGet's SemVer 2.0
  rules (four numbers, labels case-insensitive, build metadata ignored).
- **Vulnerabilities:** `VulnerabilityInfo/6.7.0` where a source offers it
  (nuget.org does): its index lists pages (base, update) mapping lower-case
  ids to advisories with a severity (0 low, 1 moderate, 2 high, 3 critical),
  a link and the affected range in interval notation. The fixed version is
  the lowest listed version above the one in use outside the range.
- **Credentials:** `packageSourceCredentials` (Username with
  ClearTextPassword, `%VARIABLES%` expanded, or an encrypted Password,
  decrypted with DPAPI on Windows), sent as Basic authentication. A source
  answering 401/403 is reported as unauthorized, one that does not answer as
  unreachable, never guessed.
- **Cache:** beside the global configuration in `cache/nuget/<source
  hash>/` (`service.json`, `versions/<id>.json`, `vulnerabilities/`). A
  version list and a service index are kept 24 h; the vulnerability index
  is read again after 24 h and only the pages whose update time changed are
  downloaded. harness ui asks for the marks after it has drawn the view
  (`GET /api/projects/{id}/packages?packages=name@version,…`, at most 30 s);
  a sync builds the findings from the cache alone (up to a week old) and
  never waits for a source. `HARNESS_NUGET_OFFLINE=1` keeps every lookup to
  the cache.
- **Doctor:** `package-sources` reads each enabled source's service index
  and names the fix for a source it cannot read (credentials, the URL, or
  `dotnet nuget disable source`).

Only packages a project references directly are looked up; transitive ones
are out of scope. The nuget provider's `package.info` (NugetMcpServer's
`get_package_info`) answers description, dependencies and recent versions,
pre-releases mixed in, as text for an agent; it has no vulnerability data
and needs the provider running, so the marks do not use it.

### Built-in search

The MCP server and harness ui add a provider named `search` that answers
`code.text_search` with `rg --json` and `code.file_search` with `fd`. Each
call starts the tool in the project root, stops it (with its whole process
tree) once it has the limit, at the provider's timeout or when the call is
cancelled, and returns items sorted by path: the path relative to the root,
and for text the line number and the line. The query is plain text (its
regular-expression characters are escaped), `path` limits the search to a
folder, and `.gitignore` and `.ignore` apply as rg and fd read them. It
starts no process until asked and keeps no index, so it serves light and
workspace registries too, and its calls appear in the provider statistics
like any other provider's.

Harness looks for `rg` (`rg.exe`) and `fd` (`fd.exe`, or `fdfind` on Debian
and Ubuntu) on `PATH` once per process. With rg but no fd, file search
lists `rg --files` and keeps the files whose name matches the query
(ignoring case unless the query has a capital letter, as fd does). With
neither, the provider is left out, and `harness doctor`, `harness report`
and `provider_status` name the install command for the system.

| Platform | Install |
|---|---|
| Windows | `winget install BurntSushi.ripgrep.MSVC` and `winget install sharkdp.fd`; or `scoop install ripgrep fd`; or `choco install ripgrep fd` |
| Debian, Ubuntu | `sudo apt install ripgrep fd-find` (the command is `fdfind`) |
| Fedora, RHEL (EPEL) | `sudo dnf install ripgrep fd-find` |
| Arch | `sudo pacman -S ripgrep fd` |
| Alpine | `sudo apk add ripgrep fd` |
| macOS | `brew install ripgrep fd` |
| any, with Rust | `cargo install ripgrep fd-find` |

A tool installed while harness runs is seen only by processes started
afterwards (detection is cached per process): restart harness ui and the
agent sessions. On Windows, winget's links folder
(`%LOCALAPPDATA%\Microsoft\WinGet\Links`) reaches only new terminals.
`harness doctor`'s `search` line confirms what was found. Optional
settings:

```toml
[providers.search]
enabled = false                    # turn the built-in search off
rg = "/opt/ripgrep/rg"             # instead of rg on PATH
fd = "/usr/bin/fdfind"             # instead of fd or fdfind on PATH
timeout_ms = 10000                 # per call (default 30 s)
only = ["code.text_search"]        # serve just this one
```

The `rg-fd` pack (`[providers.rg-fd]`, running the separate rg-fd-mcp
server) remains available but is optional and superseded. While an
enabled `[providers.rg-fd]` entry is configured the built-in search stays
off, so existing setups keep answering as before; `harness doctor` notes
that the entry can be removed once rg and fd are installed. Setting
`enabled = true` under `[providers.search]` runs both.

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

[architecture.packages]           # NuGet upgrades and vulnerabilities (see Providers)
enabled = true                    # false: no package source is read
outdated = false                  # true: an outdated package is an info finding

[providers.<name>]                # see Providers

[scm]                             # see SCM connections: this project's SCM
name = "work"                     # when its git remotes do not match one
project = "group/sub/repo"
remote = "origin"

[pricing."gpt-6.1-terra"]         # a model's API price, US$ per million tokens
input = 1.25                      # (harness ships Claude prices; this adds or
cached_input = 0.125              # overrides one; a price left out keeps the
cache_write = 1.25                # shipped one, or the input price)
output = 10.0
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

## SCM connections

harness can talk to GitLab (REST API v4), Forgejo (REST API v1) and GitHub
(github.com or Enterprise Server, REST API) servers,
authenticated with an access token only. Connections are set up once per
machine and serve every project on it, with `harness scm` or in harness ui
under Machine › SCM connections, where Test connection checks the server's
version and the token's user and names the failure (unreachable, TLS,
token rejected, not permitted, wrong server type) with its fix. Which token
scopes and permissions each operation needs, per SCM, is in the guide's
[Token permissions](#guide-token-permissions) (`harness docs
guide-token-permissions`).

| Command | What it does |
|---|---|
| `harness scm list [--json]` | Name, type, URL, aliases and token source of each SCM; tokens only masked (`****abcd`). |
| `harness scm add <name> --type gitlab\|forgejo\|github --url <url> --token-stdin` | Adds an SCM; the token is read from the first line of standard input. `--token-env <VAR>` reads it from that environment variable on every call instead of storing it; `--token <t>` works but can leave the token in your shell history. |
| `harness scm set <name> [--type] [--url] [--token-stdin\|--token\|--token-env] [--rename <new>]` | Changes an SCM; a new token or variable replaces the old one. |
| `harness scm remove <name>` | Removes an SCM, with its aliases. |
| `harness scm alias list <name> [--json]` | Lists an SCM's aliases. |
| `harness scm alias add <name> <url>` | Adds an alias: a host name (`git-alias.example.com`, or an SSH host alias from `~/.ssh/config`) or a base URL (`https://git-alias.example.com[/prefix]`, `ssh://git@ssh-alias[:port]`). |
| `harness scm alias set <name> <old> <new>` | Replaces an alias in place. |
| `harness scm alias remove <name> <url>` | Removes an alias. |
| `harness scm test [<name>] [--json]` | Tests one SCM, or all of them side by side: the server's version and the token's user, or what failed and how to fix it. Exits 1 when a test fails. |
| `harness scm use <name>\|--auto [--project <dir>] [--json]` | Chooses the SCM this repository uses on this machine (one that serves its remotes), or with `--auto` forgets the choice and detects it from the remotes again. |
| `harness scm which [--project <dir>] [--json]` | Shows the repository's SCM, project and remote, how it was chosen, and the connections that serve it equally. Exits 1 when no SCM serves it. |

Names are 1-64 letters, digits, `-`, `_` or `.` and unique ignoring case;
the URL is the server's base address (`https://gitlab.example.com`, or
`https://host/gitlab` for an instance under a path; `https://github.com` for
GitHub, whose API harness calls at `api.github.com`, or an Enterprise Server's
address, under its `/api/v3`). The type is always given explicitly.

Aliases are other names under which the same server is reached, for
repositories cloned from an alias host name, a proxy path or an SSH host alias
while the URL stays the canonical API address. They are used only to match a
repository's remotes to the SCM and work out its project path (an alias's path
prefix stands for the URL's: with `url = "https://gitlab.example.com/gitlab"`
and the alias `https://proxy.example.com/scm`, the remote
`https://proxy.example.com/scm/group/repo` is project `group/repo`); API calls
always go to the URL. A host name alone matches that host over HTTPS and SSH on
any port; an `ssh://` alias matches only SSH remotes. Aliases are normalised
(lower-case host, no default port, no user, no trailing slash), may not repeat
or equal the SCM's own URL, and a web alias may not hold a password. An alias
that two SCMs share leaves the project ambiguous until you choose one
(`harness scm use`). harness ui edits them in the SCM's dialog.

They are stored in `scm.toml` beside the global `config.toml`, one table per
SCM:

```toml
[scm.work]
type = "gitlab"
url = "https://gitlab.example.com"
aliases = ["https://git-alias.example.com", "ssh-alias"] # optional: only match remotes
token_protected = "CfDJ8..." # the token, encrypted; or: token_env = "WORK_GITLAB_TOKEN"
```

Stored tokens are encrypted (ASP.NET Core Data Protection) with keys kept in
`scm-keys/` beside `scm.toml`. harness creates that folder readable only by
you (an owner-only ACL on Windows, where the keys are also protected with your
Windows account; mode 700 elsewhere) and writes `scm.toml` readable only by you
through a locked, atomic replace; a file or folder you widened yourself is
tightened again on the next change. Tokens never appear in harness's output,
logs or telemetry.

- **Upgrading**: a `token = "..."` saved by an older harness keeps working and
  is shown as unencrypted (`harness scm list`, `harness doctor`); the next
  change with `harness scm` or in harness ui saves every stored token
  encrypted.
- **Backups and other machines**: `scm.toml` and `scm-keys/` belong together.
  Back them up, move them and, in a container, mount them on the same volume
  together. Without its keys a token cannot be decrypted (on Windows, not by
  another account either): `harness doctor` says so, and you store it again
  with `harness scm set <name> --token-stdin`.
- **What it protects**: a copy of `scm.toml` on its own (a backup, a synced
  dotfiles folder, a file attached to a support request) no longer reveals your
  tokens. It does not protect against programs running as you, coding agents
  included, which can decrypt them just as harness does, nor, on Linux and
  macOS, against anyone who can read your whole configuration folder. To keep a
  token out of harness's files altogether, use `token_env` with your own secret
  manager.

Each project finds its SCM from its git remotes: `origin` first, then the
other remotes, the first whose URL (after `insteadOf` rewrites) is on a
configured SCM's host and below its base URL. The rest of the URL's path is
the project (`group/sub/repo` on GitLab, `owner/repo` on Forgejo and GitHub). Worktrees
use their repository's remotes. `harness status` shows the result (an `SCM`
line; `scm` in `--json`), as does the agents' `project_overview`. When the
remotes do not say, set it in the project's `.harness/config.toml`:

```toml
[scm]
name = "work"              # an SCM from 'harness scm list'
project = "group/sub/repo" # with name: its path there, no matching
remote = "upstream"        # or: match only this remote
```

When several connections serve the same server (one URL, different tokens),
a remote matches them equally and the project's SCM is ambiguous until one is
chosen. Choose it per repository on this machine with `harness scm use <name>`,
or on the project's Agents › Tools & providers page (the SCM card in Used
here). The choice is stored in `scm.toml`, keyed by the repository's common Git
directory so its worktrees share it:

```toml
[project."/home/me/src/service/.git"]
scm = "bot"
```

Renaming the connection keeps the choice; removing it forgets the choice. The
SCM is chosen in this order:

1. `[scm] name` in the project's `.harness/config.toml` (shared with the team);
2. the connection chosen for the repository on this machine;
3. the remotes, as above.

A choice still applies `[scm] project` and `remote`. A choice that names a
removed connection, or one that no longer serves the remotes, is reported by
`harness status`, `harness doctor` and harness ui with the fix (`harness scm use
<name>` or `--auto`), never silently replaced. `harness status` says how the SCM
was chosen (`detected from the remotes`, `chosen on this machine` or `set in
.harness/config.toml`; `source` in `--json`).

`harness doctor` checks them too: that `scm.toml` reads and is readable only by
you, that each SCM has its token and answers its connection test (all tested
at once, 15 s at most each), and which SCM and project this repository is on
and whether the token reaches that project. No SCM configured is only noted.

Agents work with the project's merge requests through these MCP tools. Each
acts on the current project's SCM and project; `merge_request_id` is the number
the SCM shows (GitLab `!7` is 7, Forgejo or GitHub pull request `#7` is 7).

| Tool | Arguments (default) | Does |
|---|---|---|
| `scm_get_projects` | `search`, `visibility` (`private`; or `internal`, `public`), `scm` (the project's) | projects the token's user can access |
| `scm_list_merge_requests` | `state` (`opened`; or `merged`, `closed`) | the project's merge requests, latest first |
| `scm_get_merge_request_details` | `merge_request_id` | description, branches, SHAs, approvals, reviewers, labels |
| `scm_get_merge_request_comments` | `merge_request_id` | general and diff-line comments, oldest first |
| `scm_add_merge_request_comment` | `merge_request_id`, `comment` | a general comment |
| `scm_add_merge_request_diff_comment` | `merge_request_id`, `comment`, `file_path`, `line_number`, `line_type` (`new`; or `old` for a removed line) | a comment on one diff line |
| `scm_get_merge_request_diff` | `merge_request_id` | the changes per file, at most 60 KB, cut files marked truncated |
| `scm_set_merge_request_title` | `merge_request_id`, `title` | renames the merge request |
| `scm_set_merge_request_description` | `merge_request_id`, `description` | replaces its description |
| `scm_approve_merge_request` | `merge_request_id` | approves it as the token's user |
| `scm_create_merge_request` | `from`, `to`, `title`, `description` (empty), `assignees`, `reviewers` (usernames; none) | opens a merge request from branch `from` into `to`, assigns it and requests the reviews; answers like `scm_get_merge_request_details` plus `warnings` |

A tool that cannot reach the SCM says why and what to do: no SCM configured
(`harness scm add`), no remote matching one (`[scm]` above, `harness doctor`),
or the server's answer (`unauthorized`, `forbidden`, `not_found`, `invalid`
for a line outside the diff, `rate_limited`) with its fix.
`scm_create_merge_request` is `invalid`, and creates nothing, for a missing
branch, the same branch twice, an unknown username, or an open merge request
that already merges the same branches (named with its number and URL); an
assignee or reviewer the SCM refuses once the merge request exists is a
warning in the answer, not an error. Agents open merge requests only when the
user asks.

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
| `harness tasks proposals [--status]` | The tasks and blueprints agents proposed (`task_propose`, `task_blueprint_propose`). `harness tasks approve <id>` makes the task, or makes or restates the blueprint (a new one gets the next free id); `harness tasks reject <id> --note …` closes the proposal. harness ui: Work › Inbox, where a proposal can be restated before it is approved. A proposed task's blueprint is matched by id or title when the task is approved, so tasks proposed with a new blueprint (named by its title) join it whichever is approved first; with no match, the blueprint is created with the next free id. A new blueprint whose title a blueprint has already asks whether to merge into that one (it is restated, its id and tasks stay) or create a separate one: `--same-name merge` or `--same-name new`, without which it is refused. An agent whose work was already approved outside harness passes `approved_by` (the approver) to `task_propose` or `task_blueprint_propose` (with `same_name` for a blueprint titled like an existing one): the proposal is approved at once, recorded as decided by `external: <approver>`, and shown as "approved externally by …"; anything an Inbox approval would refuse is refused and nothing is created. |

Each task's audit trail (`harness tasks show <id> --events`) says how it
was made: imported, added with `harness tasks add`, added in harness ui, or
proposed by an agent and approved by you. A ready or backlog task can be
edited in harness ui (Work › Tasks, Edit…); the edit is recorded as a
`definition_updated` event with you as its actor. Removing a blueprint in
harness ui records a `moved` event on each task it moves, and a
`dependency removed` event on each task that lost a dependency on a task
deleted with it; the deleted tasks' own trails go with them.

With the Claude Code or Codex hooks installed, a task lists the agent
sessions that claimed, updated or completed it (Work › Tasks, `task_get`),
with what each used: tokens by kind (input, cache read, cache write,
output, reasoning), the models, the active time (the turns, not the idle
time between them) and an API-equivalent price, with the task's totals and
the wall time from its first claim to its completion. harness reads them
from the clients' own transcripts on your machine (Claude Code's
`~/.claude/projects`, Codex's `~/.codex/sessions`; `CLAUDE_CONFIG_DIR` and
`CODEX_HOME` are honoured), counts and times only, never content, and
reads on from where it stopped on every `harness sync` and whenever a
task's page opens. A session that worked on several tasks is shared out by
time: a task gets what the session used after its previous task's last
event, up to its own last event (or up to now while it still works on it).
The price is what the same tokens would cost through the API at harness's
price table (Claude models; others under `[pricing."<model>"]`, see
Configuration), not what a subscription pays; a model without a price shows
its tokens only.

**Agents started from harness ui.** A task's Agent sessions › Start an agent
runs Claude Code or Codex on the task in an agent host, `harness-agents serve`,
started detached beside `harness`:

- Claude Code runs with stream-json input and output and asks harness for
  tool permissions (`--permission-prompt-tool stdio`); Codex runs as
  `codex app-server`. Whatever needs your approval waits in the panel for
  Allow or Deny.
- The **Model** box lists the agent's models from harness's model catalog:
  Default (the agent's own default, named when known), then the models by
  name (the id in the tooltip), then Other… for any id; the start accepts
  any id, and the session marks one the list does not name. The catalog is
  one file per machine, `agent-models.json` beside the global configuration
  (every project and browser shares it). It is filled from what the agents
  report: Codex's app-server `model/list` and Claude Code's answer to the
  stream-json `initialize` request (its documented aliases sonnet, opus and
  haiku if a version gives no list), plus the models the agents'
  transcripts recorded (marked seen). Every session started from harness ui
  refreshes its agent's list from its own handshake; opening the dialog
  refreshes a list that is empty or older than 24 hours with a short probe
  of the CLI in the background (no session, no turn, at most 30 s), and the
  dialog shows the list it has meanwhile. A failed refresh keeps the last
  list and says so quietly under the box. Sessions started elsewhere add
  only the models their transcripts record: an MCP client does not tell
  harness its models.
- The **Permission mode** box decides how much it may do without asking; its
  choices follow the agent, and Auto is the default for both. Claude Code:
  Auto (`--permission-mode auto`: what it judges safe runs, the rest asks),
  Manual (every tool that needs permission asks), Accept edits, Plan (reads
  and plans only) and Bypass permissions (unsafe: nothing asks). Codex: Auto
  (approval policy `on-request`, sandbox `workspace-write`: Codex decides when
  to ask, so the sandbox is what contains it), Manual (`untrusted`: commands
  it does not know to be safe wait for you), Read only (sandbox `read-only`)
  and Full access (unsafe: approval policy `never`, no sandbox). A resumed
  session keeps the mode it started with; the session shows its mode.
- The client gets `HARNESS_TASK_ID` in its environment and is recorded on the
  task as started by harness.
- The panel shows its messages, tool calls, approvals and turns live (over
  `/api/events`, polling while that is offline). Send starts a turn; Stop
  interrupts the running one (neither client can pause a turn, so pause is
  Stop, then a new message or Resume); Allow and Deny answer a permission
  request; Close ends the session. An ended session resumes with a new
  prompt in a new host.
- A host closes its session after an hour without a turn. Its state and log
  stay in `.harness/agents/` beside the project database; commands reach it
  through a named pipe open to your user only.
- `HARNESS_CLAUDE_COMMAND` and `HARNESS_CODEX_COMMAND` name the client when
  it is not on PATH as `claude` or `codex`; `HARNESS_AGENTS_PATH` names the
  host when it is not beside `harness`.

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
