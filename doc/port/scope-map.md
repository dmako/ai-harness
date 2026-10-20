# C# port implementation scope

Every implemented source area is assigned below. Plan keys remain stable; approved task IDs are resolved from harness on 2026-10-20. Every row is in scope, with no silent subsystem exclusion.

## Internal subsystems

| Pinned source area | Plan and approved task |
| --- | --- |
| `internal/activity` | PORT-024 / TASK-0024 |
| `internal/actor` | PORT-008 / TASK-0008, PORT-011 / TASK-0011 |
| `internal/arch` | PORT-021 / TASK-0021 |
| `internal/archread` | PORT-021 / TASK-0021 |
| `internal/brand` | PORT-004 / TASK-0005, PORT-027 / TASK-0027 |
| `internal/buildinfo` | PORT-004 / TASK-0005 |
| `internal/bus` | PORT-024 / TASK-0024 |
| `internal/cli` | PORT-004 / TASK-0005, PORT-006 / TASK-0006, PORT-009 / TASK-0009, PORT-011 / TASK-0011, PORT-014 / TASK-0014, PORT-025 / TASK-0025, PORT-026 / TASK-0026, PORT-029 / TASK-0029 |
| `internal/clients` | PORT-025 / TASK-0025 |
| `internal/clones` | PORT-022 / TASK-0022 |
| `internal/codefiles` | PORT-016 / TASK-0016 |
| `internal/config` | PORT-006 / TASK-0006, PORT-014 / TASK-0014 |
| `internal/coupling` | PORT-022 / TASK-0022 |
| `internal/devtelemetry` | PORT-029 / TASK-0029 |
| `internal/dotnet` | PORT-015 / TASK-0015, PORT-020 / TASK-0020 |
| `internal/ecosystem` | PORT-020 / TASK-0020 |
| `internal/fileedit` | PORT-025 / TASK-0025, PORT-026 / TASK-0026 |
| `internal/findings` | PORT-021 / TASK-0021, PORT-023 / TASK-0023 |
| `internal/fsutil` | PORT-005 / TASK-0004, PORT-006 / TASK-0006, PORT-013 / TASK-0013, PORT-016 / TASK-0016, PORT-025 / TASK-0025 |
| `internal/git` | PORT-006 / TASK-0006, PORT-016 / TASK-0016, PORT-022 / TASK-0022 |
| `internal/gitsync` | PORT-016 / TASK-0016 |
| `internal/goarch` | PORT-020 / TASK-0020, PORT-021 / TASK-0021 |
| `internal/graphify` | PORT-015 / TASK-0015 |
| `internal/gui` | PORT-027 / TASK-0027, PORT-028 / TASK-0028 |
| `internal/health` | PORT-006 / TASK-0006 |
| `internal/hooks` | PORT-026 / TASK-0026 |
| `internal/instructions` | PORT-025 / TASK-0025 |
| `internal/machine` | PORT-006 / TASK-0006, PORT-019 / TASK-0019 |
| `internal/mcp` | PORT-007 / TASK-0007, PORT-009 / TASK-0009, PORT-011 / TASK-0011, PORT-014 / TASK-0014, PORT-018 / TASK-0018, PORT-023 / TASK-0023 |
| `internal/memory` | PORT-008 / TASK-0008, PORT-009 / TASK-0009 |
| `internal/overview` | PORT-006 / TASK-0006, PORT-024 / TASK-0024, PORT-027 / TASK-0027 |
| `internal/project` | PORT-006 / TASK-0006 |
| `internal/providers` | PORT-012 / TASK-0012, PORT-013 / TASK-0013, PORT-014 / TASK-0014 |
| `internal/readcache` | PORT-017 / TASK-0017, PORT-027 / TASK-0027 |
| `internal/redact` | PORT-008 / TASK-0008, PORT-024 / TASK-0024, PORT-029 / TASK-0029 |
| `internal/report` | PORT-024 / TASK-0024 |
| `internal/retrieval` | PORT-017 / TASK-0017, PORT-018 / TASK-0018 |
| `internal/stack` | PORT-020 / TASK-0020 |
| `internal/storage` | PORT-005 / TASK-0004 |
| `internal/system` | PORT-020 / TASK-0020, PORT-021 / TASK-0021 |
| `internal/tasks` | PORT-010 / TASK-0010, PORT-011 / TASK-0011 |
| `internal/telemetry` | PORT-024 / TASK-0024 |
| `internal/tsarch` | PORT-020 / TASK-0020, PORT-021 / TASK-0021 |
| `internal/workspace` | PORT-019 / TASK-0019 |
| `internal/wsearch` | PORT-019 / TASK-0019, PORT-020 / TASK-0020 |

The provider row includes protocol, generic MCP/process adapters, built-in routing and conformance contracts. The mcp row includes the client, server and tool-name inventory. Go/TypeScript architecture readers here are existing lightweight structural/manifest readers; this assignment does not authorize new full-language analyzers.

## Executables

| Source entrypoint | Plan and approved task |
| --- | --- |
| `cmd/harness` | PORT-004 / TASK-0005, PORT-007 / TASK-0007, PORT-025 / TASK-0025, PORT-031 / TASK-0031 |
| `cmd/harness-bench` | PORT-002 / TASK-0002, PORT-029 / TASK-0029, PORT-030 / TASK-0030 |
| `cmd/harness-collect` | PORT-029 / TASK-0029 |
| `cmd/harness-e2e` | PORT-002 / TASK-0002, PORT-029 / TASK-0029, PORT-030 / TASK-0030 |
| `cmd/harness-graphify` | PORT-015 / TASK-0015 |
| `cmd/harness-share` | PORT-029 / TASK-0029 |

`harness-bench` maps to development tooling in `benchmarks`; `harness-e2e` maps to `test`. Neither must become a shipped production dependency. Collect/share remain separate optional tools; PORT-029 must port them or record an explicit reviewed exclusion before full parity is claimed.

## Supporting files and acceptance

| Source material | Port ownership |
| --- | --- |
| `docs/guide.md`, `docs/reference.md`, embedded docs, GUI static assets | PORT-027; retain shipped content, keep C# repo documentation in `doc` |
| Original `internal/storage/migrations/*.sql` | PORT-005; retain all 19 scripts and migration ledger |
| `internal/config/packs/*.toml` | PORT-014; retain pack overrides and capability routing |
| `bench`, case snapshots and evaluation reports | PORT-002/029/030; adapt to `benchmarks`, retain result contracts |
| `scripts`, `Makefile`, archive layout, LICENSE | PORT-004/030/031; port build/release/e2e behavior and retain MIT notices |
| Source tests, including provider conformance fixtures | PORT-002 plus owning feature task; run parity across all surfaces in PORT-030 |
| Data cutover, rollback and real client acceptance | PORT-032/033 |

## Explicit future work

Native symbol indexing and full language analyzers (source M6-M8/M11/M12) remain deferred under source Checkpoint A. Reusing external graphify and lightweight project/architecture readers is in scope. C#/.NET gets first priority; occasional TypeScript work needs overview and repetition evidence.

Cross-machine/team synchronization (source M17) and an organization server (M18) remain future work. Their design notes are not implemented contracts. No cloud transport, organization identity model or distributed task authority is introduced by this port baseline.

Mandatory NativeAOT, an ORM schema rewrite, a replacement frontend and embedded compiler hosting are not baseline requirements. PORT-003 may evaluate choices; implementation must preserve approved observable behavior.

Task prerequisites remain recorded in the original proposal reasons; they were not automatically converted into enforced harness dependency edges on approval. Resolve plan keys using `inventory/scope-map.json` before taking later implementation tasks. This task does not edit unrelated queue definitions.
