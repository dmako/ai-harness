# Hooks and shell completion (PORT-026)

TASK-0026 ports the frozen Go implementation at
`9cb401dd2de4f85dd089fea86a0b139d81b178c6`. It uses the existing JSON span editor,
atomic file editor, marked blocks, project locator and local telemetry store;
there are no additional NuGet dependencies or schema migrations.

## Commands and setup

```text
harness hooks install --kind git
harness hooks install --kind claude-code,codex --scope user
harness hooks status
harness hooks uninstall --kind all --scope all
harness completion install --shell pwsh
harness completion uninstall --shell pwsh
```

`hook` is an alias for `hooks`. Bare `hooks` opens a hooks chooser; redirected
output shows status without writing. Bare `install` and `uninstall` now offer
MCP entries, agent instructions, hooks and shell completion before their review
screen. Explicit hook changes support `--project`, `--dry-run`, `--kind`,
`--scope` and `--command`. Installation selects one client scope; removal can
select all scopes. Git installation defaults to `harness` on PATH; client hooks
default to this executable. Framework builds use their sibling apphost for
client hooks. Pass a durable `--command` when installing from a temporary build.

Git hooks cover post-commit, post-checkout, post-merge and post-rewrite. They
honor absolute or repository-relative `core.hooksPath`, otherwise use the common
Git directory shared by linked worktrees. Their marked block launches a silent
background `sync` in the worktree where Git runs the hook, with stdin closed.
Existing shell content survives; foreign interpreters and damaged markers are
refused. New hooks have a shell shebang and executable Unix permissions.

Claude Code gets PostToolUse and SessionStart hooks in user, project or local
settings. Codex gets an asynchronous PostToolUse hook in user or project
`hooks.json`. Each command hook declares a ten-second client timeout. Installation
prints Codex's trust guidance: run `/hooks` in Codex and approve harness's hooks.
Absent user client directories are skipped. Only owned harness items are replaced or removed; foreign items, group fields,
events and surrounding configuration survive. Existing files are backed up.

## Event ingestion

`harness hook event --client claude-code|codex` runs before host initialization
and normal command parsing. Its best-effort budget is three seconds, including
reading stdin. It accepts at most 1 MiB of strictly decoded UTF-8 JSON. Invalid
input, open input pipes, unknown clients, unavailable repositories and database
contention produce exit zero and no normal output. `HARNESS_HOOK_DEBUG=1` prints
the outcome and a safe failure category to stderr, without echoing payloads.

The parser extracts session ID, cwd, tool name, classification, MCP server/tool,
file or notebook paths and harness get_context's task. It classifies native
read/search/edit tools, MCP calls and the frozen shell read/search heuristics.
For apply_patch it reads file-header lines only, with stable deduplication.
Prompts, responses, file contents, shell commands and patch bodies are never
persisted. Paths outside the repository are excluded. Git Bash `/c/...` paths
become native Windows paths. Events use the registered Git worktree identity and
existing context/session linkage.

Hooks only open existing project state. They do not initialize state, migrate
databases or register projects/worktrees. SQLite contention is bounded to one
second per operation; path processing and event insertion check cancellation.
The event transaction either commits all paths or rolls back. SessionStart
prints the frozen harness guidance only inside an initialized repository.

## Completion

`completion bash|zsh|fish|pwsh` prints the frozen shell script, always with LF
line endings. The hidden `__complete` endpoint reads the actual registered C#
command model through Spectre's public help interfaces. It returns subcommands
or leaf options, suppresses values for non-boolean options, resolves supported
aliases/default groups and excludes the hidden endpoint. New registered commands
automatically appear. Its encoding is names for Bash, `name:description` for zsh,
and tab-separated names/descriptions for fish and PowerShell. PowerShell passes
the current word first as `=word`, preserving an empty word on Windows.

Bash and zsh use marked profile blocks; zsh honors ZDOTDIR. Fish uses
XDG_CONFIG_HOME's completions directory. PowerShell queries
`$PROFILE.CurrentUserAllHosts`, with documented Windows/Unix fallback paths.
An explicitly redirected Windows home never resolves the real user's profile.
Profile edits use backups and preserve unrelated bytes, line endings and links.
Existing marked completion blocks are left unchanged; malformed markers refuse
the edit. Uninstall-all uses these same hook/completion implementations.

## Deliberate compatibility differences

- Completion lists commands currently registered in the C# port rather than
  advertising unimplemented Go commands. Descriptions come from that same model.
- Input size and encoding are validated explicitly. The Go stdin limit can
  silently accept a truncated prefix and does not cancel an open stdin read.
- Shell executable paths containing metacharacters are quoted defensively;
  Windows dollar/backtick characters are escaped for Git Bash. Newlines and NUL
  characters in executable paths are refused. Ordinary native paths match the Go
  quoting.
- Hook diagnostics report safe categories rather than raw external exception
  text. Event ingestion opens existing state without Go's migration path.
- Fish removal preserves foreign additions and makes a backup instead of
  deleting the entire completion file. These ownership protections extend the
  setup behavior established in PORT-025.

## Validation

The checked receipt is [hooks-validation.json](inventory/hooks-validation.json).
The frozen driver in `test/Harness.Compatibility/Hooks` compares payload metadata,
client JSON edits, command quoting and Git hook plans on Windows and Linux.
Tests also cover malformed input/configuration, backups, foreign content, CRLF,
Git Bash paths, a held-open stdin pipe, a locked SQLite database, large patch
metadata and atomic writes. The full solution suite runs with all sixteen Go
drivers and the real code intelligence providers.

`publish_smoke.py` exercises the self-contained executable from a path with
spaces: actual installed client commands through Git Bash, session guidance,
metadata-only storage, Git commit and linked-worktree refresh, native Bash and
PowerShell completion, all profile types and uninstall cleanup. The expanded
setup wizard is driven through actual Linux pseudo-terminals. The frozen Go
hook/client/CLI tests run under the race detector on Linux without source edits.
zsh and fish scripts are compared byte-for-byte with Go; interactive zsh/fish and
macOS execution were unavailable in this environment.

To rebuild the differential driver from the extracted pin:

```powershell
python test/Harness.Compatibility/Hooks/build_oracle.py `
  --source artifacts/compatibility/oracle/source `
  --go artifacts/compatibility/toolchain/go/bin/go.exe `
  --modules artifacts/compatibility/oracle/modules `
  --output artifacts/hooks-oracle/hooks-oracle.exe
$env:HARNESS_GO_HOOKS_ORACLE = "$PWD/artifacts/hooks-oracle/hooks-oracle.exe"
$env:HARNESS_GO_FINDINGS_SERVER = "$PWD/artifacts/compatibility/oracle/harness.exe"
dotnet test --project test/Harness.Unit/Harness.Unit.csproj --no-restore `
  --filter-namespace Harness.Unit.Hooks -bl:{{}}
python test/Harness.Compatibility/Hooks/record_validation.py --check
```

`build_oracle.py --goos linux` builds the matching Linux driver. The receipt
collector checks that both Windows test assemblies completed before recording
the full suite and stores normalized source hashes alongside artifact hashes.
