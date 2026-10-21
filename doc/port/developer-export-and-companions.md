# Developer export and companion tooling (PORT-029)

Source: Go `9cb401dd2de4f85dd089fea86a0b139d81b178c6`, `internal/devtelemetry`, `cmd/harness-collect`, `cmd/harness-share`, `cmd/harness-bench`, and `cmd/harness-e2e`.

## Developer export

`harness telemetry` defaults to `status`. `enable --endpoint URL [--token TOKEN]`, `disable`, `preview`, and `send [--auto]` implement the opt-in machine workflow. Missing state means off. Disabled manual send exits 2; automatic sends quietly succeed without sending. Preview neither sends nor saves state. A successful automatic send suppresses another for 24 hours; failures do not advance the timestamp. Manual sends bypass the interval. Every actual attempt records a local JSON log entry; failures do not affect sync or MCP. Sync launches a separate automatic-send process only when due. Requests have a 15-second deadline, and response bodies are not buffered.

The schema-1 projection explicitly selects context counts, tool/provider rates, knowledge counts, hook counts, language counts, cleaned findings, and versions. Project roots, actual project IDs, context units, misses, task text, memory contents, queries, finding actions and histories are never serialized. Machine/project pseudonyms use cryptographic randomness and persist locally. Paths, URLs, credentials and internal IDs in findings are redacted. The `go` field retains its wire name but identifies the .NET runtime. Export runs without provider startup or source reads.

State and log files live next to global `config.toml`. Unix files use 0600; collector machine directories use 0700. Windows files have inheritance disabled and an ACL allowing only the current account. Secret bytes are written after permissions are restricted. State replacement uses a private temporary file and atomic rename. Export logs contain timestamp, byte count, success and a generic failure reason, never tokens, bodies, or endpoint credentials.

Intentional differences from Go: endpoints with URL credentials/fragments and non-ASCII/control-character tokens are rejected; redirects are refused; certificate revocation is checked; Windows ACLs are enforced rather than emulating Unix permission bits; logs omit raw endpoints and exception messages. These changes constrain token disclosure without changing the aggregate payload contract. Cosmetic JSON whitespace, empty optional fields and runtime/build identity differ.

## Standalone companions

These are separate executables and are never installed or launched by the main CLI. They reuse the ASP.NET shared framework and existing embedded branding, with no new NuGet dependency. Default binds are `127.0.0.1:8766` for collection and `127.0.0.1:8765` for sharing. `--addr HOST:PORT` overrides the address; any non-loopback bind requires explicit `--lan`. Both respond to Ctrl-C/SIGTERM through the host with a five-second shutdown deadline. `--help` lists flags.

| Executable | Endpoint | Behavior |
|---|---|---|
| collect | POST `/v1/report` | Optional exact bearer token, 1 MiB maximum body, schema 1, `m_` plus 16 lowercase hex digits; 401/413/400 rejection, 202 acceptance; timestamped report plus atomic latest file |
| collect | GET/HEAD `/` | Most recent machines first, versions/platform, project/answer/context-hit/finding/report counts |
| collect | GET/HEAD `/machines/{id}[.json]` | Latest report for a validated machine ID; 404 otherwise |
| collect/share | GET/HEAD `/logo.svg`, `/favicon.png` | Embedded branding; no external network resources |
| share | GET/HEAD `/` | Release builds, sizes, checksums, platform recommendation (Apple silicon for Mac browsers), install guidance and extra-file links |
| share | GET/HEAD `/download/{archive}` | Exact startup allowlist, attachment disposition, byte ranges, modification time and HEAD support |
| share | GET/HEAD `/SHA256SUMS`, `/RELEASE_NOTES.md` | Only if present at startup |
| share | All other methods | 405 with `Allow: GET, HEAD`; filesystem remains read-only |

Collector defaults to `telemetry-reports`; sharing defaults to the most recently modified valid release under `dist`. Archives match `harness-<version>-(darwin|linux|windows|freebsd)-(amd64|arm64).(tar.gz|zip)`. Listed SHA256SUMS entries are displayed as supplied, matching Go; missing entries are computed. This display is not a signature or checksum verifier. Other files, directories, newly added files, encoded/traversal paths and linked files are not served. Collection rejects linked machine directories/files. LAN users must protect the collector's read endpoints and transport with their network boundary; optional bearer authentication covers ingestion, as in Go.

Intentional differences: explicit `--lan` is also required with a non-loopback `--addr` (Go permits implicit exposure); CSP authorizes a hash of the inline stylesheet rather than unsafe-inline; linked files are rejected. Page markup is rendered in C#, retaining the shared palette and behavior rather than Go's template engine. Finding totals and timestamps use a compact table presentation. Host HTTP parsing and malformed-request diagnostics follow Kestrel rather than Go net/http.

## Evaluation tools: keep result formats reproducible

`benchmarks/oracle_evaluation.py` builds the frozen **test-only** evaluation engine and runs it against a supplied C# harness apphost. Go is required only on the evaluation workstation, never by the product. This deliberately retains the original scoring, result JSON and resume semantics; rewriting an evaluator at the same time as its subject would invalidate comparisons. Supply case/setup paths explicitly: the frozen product snapshot excludes the original `bench` corpus. Obtain that corpus from the same pinned source revision.

| Go capability | C# port evaluation entry point / output |
|---|---|
| bench validate, snapshot, verify, derive | `oracle_evaluation.py --engine bench -- <command> ...`; unchanged case YAML, resolved commits and leakage checks |
| bench levela | Same runner with `levela --harness <C# apphost> --cases ... --setup ... --out ...`; unchanged `results.json` and `report.md`, provider arms and deterministic repeats |
| bench levelb, judge | Same explicit runner; unchanged model/arm/run directories, resume and budget limits. These launch paid Claude processes only when the operator explicitly requests them. No paid runs are part of build/test |
| bench arch | Same engine's original architecture evaluation remains the Go baseline; C# `Harness.Unit/Architecture` and `Harness.Integration/Architecture` replay the ported analyzer against pinned fixtures. Do not present Go arch results as C# analyzer measurements |
| e2e archive install/uninstall | `oracle_evaluation.py --engine e2e -- --archive <C# release archive>`; original PASS/FAIL output and isolated-home/runtime-free checks; augmented release smoke scenarios belong to PORT-031 |
| storage/provider/MCP/UI behavior | Native C# xUnit/MTP compatibility scenarios plus frozen package drivers in `test/Harness.Compatibility`; TRX captures pass/fail/skip, while Go evaluation JSON remains unchanged |

Example (replace paths): `python benchmarks/oracle_evaluation.py --source <frozen-source> --go <go> --modules <module-cache> --output artifacts/evaluation -- levela --harness <C#-harness> --cases <pinned-cases> --setup <pinned-setup> --out artifacts/levela`.

Validation is recorded separately in `inventory/companions-validation.json`. It distinguishes native C# results, original Go scenarios and evaluator help/build checks; it does not claim an agent replay or release-archive E2E before those are run.
