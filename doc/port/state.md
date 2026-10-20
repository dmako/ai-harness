# Harness state compatibility

Locations below describe the pinned Go product. They are contract paths in user
repositories and user profiles, distinct from this repository's development
directory conventions. Source paths refer to the commit in `inventory/baseline.json`.

## Repository state

| Path | Ownership and behavior | Source and port plan |
| --- | --- | --- |
| Main worktree `.harness/harness.db` plus `-wal` and `-shm` | Shared project database; never a Git fixture; migrate safely and preserve IDs/history | `internal/storage`, `internal/project`; PORT-005/006 |
| `.harness/config.toml` in the current worktree | Optional project overrides; linked worktrees may have their own config | `internal/config`; PORT-006 |
| `.harness/.gitignore` | Created only when missing; excludes database sidecars | `internal/project`; PORT-006 |
| `.harness/backups/schema-NN-*.db` | Backup before migration; retain five; owner-only | `internal/storage/backup.go`; PORT-005 |
| Worktree Git directory `harness-sync.lock` | Serializes sync per worktree on supported Unix systems; Windows baseline is a no-op | `internal/gitsync`, `internal/fsutil`; PORT-016 |
| Configured `storage.db_path` | May override default; resolution is relative to the state root | `internal/config`; PORT-005/006 |

The database contains diagnostics, projects/worktrees, memories/history/audits
and proposals, tasks/claims/dependencies/acceptance/checks/audits/proposals,
milestones, file hashes/pending paths/sync runs, findings/snapshots/marks/proposals
and local telemetry. `inventory/storage.json` contains every exact migration,
including constraints and indexes. Do not replace the migration ledger with an
ORM-generated schema.

## Machine user and workspace state

The global configuration directory is `%AppData%\harness` on Windows,
`~/.config/harness` on Linux and `~/Library/Application Support/harness` on
macOS, unless `HARNESS_CONFIG_DIR` overrides it. Preserve the source's XDG and
platform resolution behavior. Tests must always override the directory.

| Relative path or file | Contents | Source and port plan |
| --- | --- | --- |
| `config.toml` | Global configuration and provider commands | `internal/config`; PORT-006/014 |
| `projects.json` and its advisory lock | Machine registry of projects, roots, worktrees, schema and client/use metadata | `internal/machine`; PORT-006/019 |
| `workspaces.json` and its advisory lock | Explicit workspace membership and names | `internal/workspace`; PORT-019 |
| `knowledge/user.db` | User-level memories stored in a separate harness database | `internal/workspace/knowledge.go`; PORT-008/019 |
| `knowledge/workspace-<workspace-id>.db` | Workspace-level knowledge; membership checked on writes | `internal/workspace/knowledge.go`; PORT-008/019 |
| `graphify-sessions.json` | Harness-owned warm-session tracking; graphify's own descriptors stay owned by graphify | `internal/graphify`; PORT-015 |
| `graphify-sessions.json.lock`, `graphify-logs/<name>.log` | Session update lock and detached graphify process logs | `internal/graphify`; PORT-015 |
| `telemetry.json`, `telemetry.log` | Opt-in developer endpoint/token/random IDs/send state and send log | `internal/devtelemetry`; PORT-029 |

Registry/session files use their source's atomic write/locking and best-effort
failure semantics. Missing registries, stale worktrees and unavailable providers
must not silently become new projects or successful empty evidence.

## Files owned by other tools

| Location | Allowed edit | Port plan |
| --- | --- | --- |
| `~/.claude.json`, repository `.mcp.json` | Only harness MCP entry at the selected scope | PORT-025 |
| `~/.codex/config.toml` | Only harness MCP server table | PORT-025 |
| Claude settings and Codex `hooks.json` at supported scopes | Only harness hook items, respecting client trust behavior | PORT-026 |
| `AGENTS.md`, existing `CLAUDE.md` | Only marked guidance or harness import | PORT-025 |
| Git hooks at `core.hooksPath` or common Git hooks directory | Only marked sync blocks; refuse unsupported non-shell hooks | PORT-026 |
| zsh/bash profiles, fish completion file, PowerShell all-hosts profile | Only harness completion block/file, with source backup behavior | PORT-026 |
| Adjacent `*.bak-harness-*` files | Backups when the source makes them; preserve permissions | PORT-025/026 |

Installation and uninstall must preserve unrelated content. Data removal requires
the explicit deletion flags and named project from the CLI contract.

## Companion and development outputs

`harness-collect` writes reports beneath its selected `--dir` (default
`telemetry-reports`). `harness-share` reads releases from `--dir` or the newest
directory below `dist`; it does not edit releases. Their server routes are in
`inventory/http.json` and port ownership is PORT-029. The Go bench/e2e tools
create disposable worktrees/results; their C# successors belong under
`benchmarks` and `test`. No generated index or cloud synchronization store is
introduced by this baseline.
