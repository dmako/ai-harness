# C# port compatibility baseline

TASK-0001 freezes the implemented behavior of `ai-harness` at commit
`9cb401dd2de4f85dd089fea86a0b139d81b178c6`. The port preserves the CLI, MCP,
provider, HTTP and persisted-state contracts while prioritizing C#/.NET code
intelligence and Claude Code plus Codex workflows. TypeScript support focuses
on project overview and repetition detection.

## Baseline authority

The authority is the Git object tree at that commit, read with `git archive`.
The neighboring working copy and the installed `harness.exe` are separate
artifacts. The installed executable reports commit `7038401`; it must not be
used as an executable oracle for this pin. Building the pinned Go oracle and
capturing actual wire responses is implemented by the
[fixture harness](fixture-harness.md) in PORT-002.

The capture contains 30 registered MCP tools, their input/output schema source
roots and tagged type declarations, 19 migrations, 76 HTTP registrations across
the UI and companion servers, 45 internal subsystems, six executable entry
points, and CLI command/flag declarations. Source hashes and the tree ID are in
[baseline.json](inventory/baseline.json), together with the original MIT license
and copyright notice covering the captured source declarations.

At capture, the source repository had three modified tracked files:

| File | Difference from the pin | Baseline treatment |
| --- | --- | --- |
| `scripts/e2e.sh` | 31 CRLF sequences in the pin, zero in the working copy; otherwise identical after CRLF normalization | Use pinned bytes; record both SHA-256 hashes |
| `scripts/release.sh` | 77 CRLF sequences in the pin, zero in the working copy; otherwise identical after CRLF normalization | Use pinned bytes; record both SHA-256 hashes |
| `.harness/harness.db` | Local binary state differs | Exclude contents from the baseline; never copy personal project knowledge into fixtures |

No semantic script patch is required. The raw differences remain visible through
the hashes and newline counts; the source files have not been changed.

## Contract inventories

| Artifact | Contents |
| --- | --- |
| [CLI reference](cli.md) | Canonical command paths, flags, defaults, aliases, dispatcher and exit behavior |
| [MCP and HTTP reference](surfaces.md) | All MCP schema roots, required/optional input fields, all HTTP registrations, provider contract |
| [State reference](state.md) | Database, machine/user/workspace state, client files, locks and generated files |
| [Scope map](scope-map.md) | Every implemented subsystem and executable mapped to plan keys and approved harness task IDs |
| [Compatibility policy](compatibility-policy.md) | Observable behavior, comparison rules, performance priorities and permitted Windows corrections |
| [Solution and packages](solution-and-packages.md) | PORT-003 assembly boundaries, enforced dependency graph, CPM choices, API validation and deployment constraints |
| [Storage implementation](storage-implementation.md) | PORT-005 migration/transaction/backup implementation, test commands, native timeout behavior and platform limits |
| [Solution bootstrap](bootstrap.md) | PORT-004 CLI composition, stable SDK/build metadata, locked restore, testing and release publish settings |
| [Project lifecycle](project-lifecycle.md) | PORT-006 strict TOML, Git/worktrees, identities, safe init/status/doctor and atomic machine registry |
| [Stdio MCP](stdio-mcp.md) | PORT-007 official SDK server, four bootstrap tools, lazy storage, wire compatibility, cancellation and process persistence |
| [Memory storage](memory-storage.md) | PORT-008 typed knowledge, SQLite search/history/audit, layered precedence, credential rejection and Go database roundtrips |
| [CLI source contracts](inventory/cli.json) | Exact command builders, flag declaration functions, conditional defaults and companion flag declarations |
| [MCP roots](inventory/mcp.json) and [schema types](inventory/schema-types.json) | Exact Go type declarations and JSON/schema tags, including nested types; custom completion schema and argument hints |
| [HTTP registrations](inventory/http.json) | Method/path patterns, handler wrappers and pinned source locations |
| [Storage](inventory/storage.json) | Original SQL migrations and hashes |
| [Providers](inventory/providers.json) | Protocol types, full capability model and embedded provider packs |

Schema source inventories deliberately retain the Go SDK's inputs rather than
claim to reproduce its JSON Schema inference. PORT-002 must capture `tools/list`
from a binary built at the pin and verify SDK-generated input/output schemas,
nullability, rejection of unknown properties and result envelopes. Source tags
alone do not establish every SDK inference rule. The runtime schemas and
representative responses captured by PORT-002 are saved in
`test/fixtures/compatibility/go-windows-amd64.json`.

## Validation and reproduction

From the repository root:

```powershell
python test/Harness.Compatibility/baseline.py
python test/Harness.Compatibility/baseline.py --check-source C:/repos/personal/ai-harness
```

The first command verifies the saved inventories and scope coverage offline.
The second compares them with the pinned Git objects without editing either
repository. To deliberately recapture the same pin:

```powershell
python test/Harness.Compatibility/baseline.py --source C:/repos/personal/ai-harness
```

Python is a development-time documentation utility only. The shipped product
retains its runtime independence from Python and Node. No C# implementation or
behavioral parity claim is made by this task.
