# Harness CLI compatibility

These paths and flags come from `internal/cli/cli.go`, its command builders and
flag functions at the pinned commit. [cli.json](inventory/cli.json) retains the
exact declaration source, including conditional defaults and help descriptions.
The tables enumerate commands, not invocations to run during baseline capture.

## Parsing and process results

`harness help <command...>` and `<command...> --help` show help on stdout.
`help`, `-h`, `-help` and `--help` are recognized in the dispatcher;
`-v`/`--version` at the root dispatch to `version`. Flags may appear among
positional arguments; `--` stops option parsing. Invalid commands, flags and
argument counts return 2, ordinary execution failures return 1, success returns
0. Warnings alone do not make doctor fail. Hook events/session callbacks and
`telemetry send --auto` deliberately suppress operational failures.

Unless stated otherwise, boolean flags default to false, optional strings to
empty, `--project` to `.`, and `--json` requests machine-readable stdout.
`P/J` below means both `--project` and `--json`. These flags are command-specific,
not root-global flags. Standard Go flag parsing accepts single/double dash
forms; preserve argument/value behavior rather than assuming Spectre defaults.

## Core commands

| Canonical command | Flags and defaults |
| --- | --- |
| `version` | `--json` |
| `init` | `--project`, `--dry-run` |
| `status` | P/J; `--syncs 0` (at most ten recent runs) |
| `sync` | `--project`, `--full`, `--since <commit>` |
| `search <query...>` | P/J; `--kind all`, `--workspace`, `--deep`, `--path`, `--limit 10` |
| `metrics` | P/J; `--days 30` |
| `report` | P/J; `--days 30`, `--all` |
| `ui` | `--addr 127.0.0.1:7477`, `--no-open` |
| `doctor` | P/J; `--all` |
| `docs [section]` | No flags; no argument lists embedded guide/reference sections |
| `instructions` | `--project`, `--remove`, `--dry-run` |
| `mcp` | `--project` defaults empty, resolved from cwd to Git root |
| `help [command...]` | Dispatcher help |
| `__complete <shell> <words...>` | Hidden raw completion endpoint; shell is bash/zsh/fish/pwsh |

## Setup and hooks

| Canonical command | Flags and defaults |
| --- | --- |
| `install` | `--client all`, `--scope user`, `--project`, `--dry-run`, `--command` (this binary) |
| `uninstall` | `--client all`, `--scope user` (also accepts all), `--project`, `--dry-run`, `--all`, `--delete-data` |
| `hooks choose` | `--project`, `--scope user`; default for bare `hooks` |
| `hooks install` | `--kind git`, `--project`, `--scope user`, `--dry-run`, `--command` (Git: harness; clients: this binary) |
| `hooks uninstall` | `--kind git`, `--project`, `--scope user` (also all), `--dry-run` |
| `hooks status` | `--kind all`, `--project`, `--scope user` |
| `hooks event` | `--client claude-code`; reads event payload from stdin |
| `hooks session` | `--client claude-code`; session context callback |
| `completion status` | No flags; default for bare `completion` |
| `completion install` | `--shell` defaults detected shell, PowerShell on Windows |
| `completion uninstall` | `--shell` defaults detected shell |
| `completion zsh` | No flags; prints script |
| `completion bash` | No flags; prints script |
| `completion fish` | No flags; prints script |
| `completion pwsh` | No flags; prints script |

Bare install/uninstall opens a setup wizard in a terminal and prints its status
otherwise. Flags choose the noninteractive path. `--delete-data` requires both
`--all` and an explicit `--project`; the default project is insufficient. Hook
kinds accept comma-separated `git`, `claude-code`, `codex` or `all`. `hook` is an
alias for the `hooks` group.

## Knowledge and tasks

Every command in this table has P/J. `task` aliases `tasks`.

| Canonical command | Additional flags and defaults |
| --- | --- |
| `tasks list` | Repeatable/comma-separated `--status`, `--milestone`, `--all` (otherwise excludes done and deferred) |
| `tasks milestones` | None |
| `tasks show <id>` | `--events` |
| `tasks next` | `--milestone`; reads next task without claiming |
| `tasks claim <id>` | `--lease 2h` |
| `tasks complete <id>` | `--summary`, `--commit`, repeatable `--acc N=pass[:evidence]` or `N=n/a:why`, `--acc-all`, repeatable `--check command=pass` |
| `tasks block <id>` | Required `--reason` |
| `tasks defer <id>` | Required `--reason` |
| `tasks ready <id>` | `--note` |
| `tasks release <id>` | `--reason` required for another claimant's task |
| `tasks import [file]` | `--dry-run`; default `docs/backlog.yaml` |
| `tasks add` | `--title`, `--goal`, `--type feature`, `--priority medium`, `--milestone`, `--id`, `--prefix`; repeatable `--depends`, `--accept`, `--scope` |
| `tasks export [file]` | `--dry-run`; default `docs/backlog.yaml` |
| `tasks proposals` | `--status pending` |
| `tasks approve <proposal>` | `--prefix` |
| `tasks reject <proposal>` | `--note` |
| `memory show <id>` | None |
| `memory retire <id>` | Required `--reason` |
| `memory promote <id>` | Required `--to workspace:<name>` or `user`; `--force` |
| `proposals list` | `--status pending` |
| `proposals show <id>` | None |
| `proposals approve <id>` | `--content`, repeatable `--supersede`, `--keep-both`, `--note` |
| `proposals reject <id>` | `--note` |
| `context stats` | `--days 30` |

Task CLI operations act as the human and differ from token-protected MCP
operations. Task IDs normalize according to source rules; completion still
requires acceptance and validation. Reimport updates definitions without
resetting execution state. No group default is inferred where the source only
prints a command list.

## Projects workspaces and providers

| Canonical command | Flags and defaults |
| --- | --- |
| `projects list` | `--json`; default for bare `projects` |
| `projects forget <id>` | No flags |
| `projects rename` | `--project`, `--to`, `--reset-name`, `--title`, `--clear-title` |
| `workspace list` | No flags; default for bare `workspace` |
| `workspace create <workspace>` | `--discover <folder>`, `--title` |
| `workspace rename <name>` | `--to`, `--title`, `--clear-title` |
| `workspace show <workspace>` | No flags |
| `workspace join <workspace>` | `--project` |
| `workspace leave <workspace>` | `--project` |
| `workspace delete <workspace>` | No flags |
| `workspace adoption <library>` | P/J; `--deep` |
| `workspace concern <words...>` | P/J; `--deep` |
| `providers sessions list` | `--json`; default for `providers` and `providers sessions` |
| `providers sessions start` | `--project` |
| `providers sessions restart` | `--project` |
| `providers sessions stop [id]` | `--project` |

`workspaces` aliases `workspace`. A group default handles bare invocations as
declared by the builder; it must not make unrecognized subcommands valid.

## Developer telemetry

| Canonical command | Flags and defaults |
| --- | --- |
| `telemetry status` | No flags; default for bare `telemetry` |
| `telemetry enable` | Required `--endpoint`; optional `--token` |
| `telemetry disable` | No flags |
| `telemetry preview` | No flags; prints outgoing aggregate payload without sending |
| `telemetry send` | `--auto` enforces enabled/daily checks, suppresses failures and stays quiet |

## Companion executables

| Executable | Command and flag surface | Port plan |
| --- | --- | --- |
| `harness-graphify` | `--root` (cwd), `--graphify` (PATH or dotnet tools), `--clean`, `--stop-on-exit`, `--idle 0` (effective 2h); arguments after `--` are watch arguments; serves provider stdio | PORT-015 |
| `harness-e2e` | Required `--archive`; fresh-machine isolated release checks | PORT-002/029/030 |
| `harness-bench` | `validate`, `snapshot`, `verify`, `derive`, `levela`, `judge`, `levelb`, `arch`; flags and defaults listed below | PORT-002/029/030 |
| `harness-collect` | `--dir telemetry-reports`, `--addr` (loopback 8766, all interfaces with LAN), `--lan`, `--token` | PORT-029 |
| `harness-share` | `--dir` (newest release below dist), `--addr` (loopback 8765, all interfaces with LAN), `--lan` | PORT-029 |

Benchmark flags are declared in a shared parser before command dispatch:
`--cases bench/cases`, `--root ~/.cache/harness-bench/snapshots`, `--resolve`,
`--config` (global harness config), `--providers` (enabled search providers),
`--setup bench/setup.toml`, `--repeat 2`, `--out` (mode-specific result directory),
`--harness harness`, `--models haiku,sonnet`, `--arms` (none plus search providers),
`--runs 3`, `--first-run 1`, `--task fix`, `--run-budget haiku=1,sonnet=3`,
`--budget 80`, `--parallel 3`, `--v`, and `--claude` (detected executable).
The parser's declared defaults and the command's effective defaults remain
distinct. Running paid agent benchmark modes requires separate authorization;
inventory capture never invokes them.

Companion parsers have their own help/exit handling. The raw flag declarations
and entrypoint source hashes are in `inventory/cli.json` and
`inventory/baseline.json`. Benchmark modes/flags must be inventoried from their
own functions, rather than assuming the main CLI dispatcher applies to them.
