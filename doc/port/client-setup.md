# Client setup and removal (PORT-025)

TASK-0025 ports client MCP configuration, agent guidance, the setup wizard and
safe removal from Go commit `9cb401dd2de4f85dd089fea86a0b139d81b178c6`.
It uses existing System.Text.Json and Tomlyn dependencies; CPM is unchanged.

## Commands and client boundaries

`install` and `uninstall` accept `--client claude-code|codex|all`, `--scope`,
`--project` and `--dry-run`. Installation also accepts `--command`.
Claude Code supports user (`.claude.json`), project (`.mcp.json`) and local
(`projects[root].mcpServers` in `.claude.json`) entries. Codex has a single user
configuration (`config.toml`) regardless of the requested scope, matching Go.
`CLAUDE_CONFIG_DIR` and `CODEX_HOME` override their locations. User setup works
outside Git; project/local setup requires a repository.

`IAgentClient` holds client-specific targets, editing and missing-client rules.
The command runner and wizard enumerate these adapters. Missing Claude user
state or a missing Codex directory is skipped rather than creating an apparently
installed client. Project Claude entries can be created. A project command under
the user home is written as `${HOME}/...`; command and arguments are separate
JSON/TOML values, including paths with spaces and Unicode.

The default command is the running executable. A framework-dependent launch
through `dotnet harness.dll` records the dotnet executable and assembly argument,
followed by `mcp`. Explicit commands must be executable files. Binaries under the
temporary directory are refused as default installations.

Bare `install`/`uninstall` shows status when redirected. In a terminal the wizard
supports movement, toggling, back, review, apply and cancellation. Existing
choices start checked; nothing is written before review is confirmed. Removals
are applied before additions. This task supplies the MCP and guidance sections;
TASK-0026 adds hook/completion setup sections with their command implementations.

## File ownership and preservation

JSON edits splice one member value or the smallest missing container into the
original UTF-8 bytes. Key order, whitespace and unrelated values remain intact.
Existing harness entry fields such as `env` survive installation. Before/after
semantic verification removes only the owned entry and compares the remainder.
Duplicate JSON keys and malformed/non-object configuration are refused.

Tomlyn syntax nodes locate the owned table and values. Existing comments,
unrelated tables, additional harness keys and environment subtables survive an
update. Syntax and semantic checks run before writing. Unsupported inline/dotted
layouts are refused. Complete multiline values can be replaced safely using
syntax spans. Removal touches only the harness table and its subtables, plus its
generated marker, and verifies unrelated data again.

The common file editor bounds reads/writes to 16 MiB, rejects malformed UTF-8,
edits symlink targets without replacing the links, and checks the original bytes
before publishing through a flushed sibling temporary file and atomic rename.
It preserves Unix modes, including permissions affected by umask. Client edits
produce exclusive sibling `.bak-harness-<UTC timestamp>` backups; collisions get
a numeric suffix. Dry runs validate the output but create no files/directories
or backups. Content checks are optimistic, not a filesystem compare-and-swap.

`instructions` maintains a single balanced harness block. Guidance normally lives
in `AGENTS.md`, imported by a marked `@AGENTS.md` in `CLAUDE.md`. A project with
only `CLAUDE.md` and no configured Codex keeps guidance there. Existing imports
outside the block are preserved. Broken/duplicate markers refuse the operation;
both files are preflighted before project-wide changes. Removing generated-only
files deletes them. Other text and existing line endings remain intact.

## Uninstall and data

Plain `uninstall` removes only the selected MCP entries. `uninstall --all` also
removes harness-owned client hooks, Git hook blocks (including `core.hooksPath`),
guidance and shell completion. Malformed files are reported and left intact;
other cleanup continues. Client hooks retain foreign items and group fields.
Profile cleanup removes balanced marked blocks; fish cleanup removes only the
exact generated completion line and keeps user additions. Windows redirected
homes never resolve a profile in the real user's Documents directory.

Project state is retained unless **both** `--all` and `--delete-data` are supplied
with an actual explicit `--project` option. Option-looking values do not satisfy
this guard. Deletion resolves the repository's state root (shared for linked
worktrees), refuses redirected `.harness` directories, removes that directory
and forgets the matching machine-registry project. Binaries remain installed.

## Compatibility evidence

The thin Go driver tests dry-run edits against the original packages, including
all scopes and guidance layout choices. Windows and Linux tests cover malformed
input, exact preservation, backup content/uniqueness, idempotence, dry runs,
symlinks, permissions, real CLI processes and deletion guards. A POSIX PTY smoke
drives the actual wizard, including cancellation and review-before-write. A
published Windows smoke launches MCP using the installed entry from a path with
spaces and exercises install/remove without touching real client homes.

Intentional robustness differences: duplicate JSON keys are refused; TOML value
comments are retained and safe multiline values can be edited; backup names
survive sub-millisecond collisions; instruction files are preflighted together;
fish user additions are preserved; redirected state deletion is refused; and
`uninstall --all` can clean user entries outside Git. Hook execution, payload
ingestion and completion generation remain TASK-0026.

The uninstrumented pinned Go tests have Unix assumptions on Windows and can
collide on millisecond backup names on Linux. Its complete client/instruction/
file-editing suites passed with Linux race instrumentation. The C# differential
checks use unchanged Go algorithms and have no timing workaround.

See [`inventory/clients-validation.json`](inventory/clients-validation.json) for
commands, results and source/artifact hashes. macOS and a Windows terminal UI
session were not exercised; Windows command processes and Linux terminal sessions
were tested.
