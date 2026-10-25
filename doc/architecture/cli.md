# CLI and client integration

Source: [`src/Harness.Cli`](../../src/Harness.Cli).
The CLI is the composition root for Core, Infrastructure, Providers, Mcp and Web.

`CliCommands` declares the System.CommandLine tree. `CommandBindings` creates
typed arguments, options and settings without reflection-based attributes.
`CliApplication` normalizes supported aliases and legacy flag spellings, routes
output/errors, owns the host lifetime and maps cancellation to exit code 130.
`hooks`/`hook`, `tasks`/`task` and `workspace`/`workspaces` are aliases.

Command handlers delegate feature work to named runners instead of putting
implementations into dispatch switch arms. CLI views own their presentation and
JSON shape; domain stores remain reusable by MCP and HTTP adapters. Interactive
setup renders with Spectre.Console; it does not bind commands.

## Client setup and hooks

Setup detects supported Claude Code and Codex scopes, previews edits and applies
selected changes. JSON/TOML editors preserve unrelated settings, reject ambiguous
input and detect conflicts. Uninstall removes owned entries and instruction
blocks. Deleting project data requires an explicit project selection and option.

Generated agent instructions share one Core guidance block. Hook installation
preserves foreign hooks and interpreters, quotes executable paths and handles
supported shell/profile formats. Hook events parse a bounded UTF-8 payload,
retain an allowlist of useful fields and redact credentials before recording.

Shell completion derives options and commands from the same registered command
tree. Shell-specific formatting and profile edits remain separate from command
execution. The embedded usage guide and reference can be read with `harness docs`
and do not require a running UI.

## UI composition

The UI command constructs `UiReadService`, write adapters, project catalogs,
session controls and change sources from Infrastructure services. This keeps
Web's project reference limited to Core. The default listener is
`127.0.0.1:17477`; use `--addr` to override it and `--no-open` to suppress browser
launch. Metadata uses `runtime` in JSON and displays Runtime in the UI.
