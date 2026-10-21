# Parity regression and performance (PORT-030)

## Qualification status

The implementation and repeatable runners are available. **Full cross-platform qualification remains open until macOS execution is recorded.** No macOS host or remote CI repository is connected to this workspace. `.github/workflows/parity.yml` prepares Windows, Ubuntu 24.04 and macOS jobs, but a workflow file is not evidence that those jobs ran. It requires an authorized source clone URL; configure authentication on the runner for a private original repository. The local C# repository has no Git remote, so no workflow was pushed or dispatched.

The first configured Windows regression passed 804 tests with five expected Unix-only skips. The first complete Linux run passed all 451 unit tests, then exposed eight graphify comparison failures caused by a test hardcoding the Windows worker apphost. The fixture now launches the native worker on Unix; all 13 graphify/context scenarios pass on Linux. The latest lifecycle/context tests pass all 16 cases, including metadata-only initialization. The final Windows run exposed a review-loading race in the frontend; a live refresh could detach a card while its review form was loading. The port now marks pending review loading, checks for reviews again after asynchronous fetches and preserves forms during whole-page live rendering. The browser scenario holds a refresh response to reproduce that race deterministically. A new metadata test also required normalized Windows path separators; its full unit rerun passes 453 tests with one Unix-only skip. The final rollup passes 809 tests with five expected skips on Windows and 809 with five explicit platform/optional-provider skips on Linux. Windows web passes all 37 scenarios, including the held-response review race. Full reruns, explicit web/unit retests and their current state are recorded in `inventory/parity-validation.json`; read its counters and pending fields before making a qualification claim.

## Accountability and behavioral evidence

`python test/Harness.Compatibility/account_matrix.py --check` verifies `inventory/parity-accountability.json`. Every frozen inventory row maps to implementation acceptance evidence: 45 subsystems, six executables, 245 contract source files, 77 CLI functions, 76 HTTP routes, 30 MCP tools, 477 schema types, five provider packs and 19 migrations. This is complete row accountability, not a claim of statement/branch coverage.

The native suites cover CLI/setup and foreign-config preservation; all 30 advertised tool schemas and their domain workflows; provider handshakes, mappings, cancellation and process supervision; HTTP read/write guards, browser conflict review and SSE reconnect/cleanup; schema 1/4/5/13/19 fixtures and all 19 migration definitions; multiprocess task claims and sync; Unicode/moved/linked paths, permissions and cleanup. Source-package drivers and behavioral scenarios remain separate from SDK transport tests. Each area receipt identifies its exact oracle and test evidence.

`snapshot_source.py` exports the frozen commit from an original checkout or authorized clone, verifies all 245 contract hashes and includes the evaluation corpus. `prepare_oracles.py` builds all native package drivers sequentially, avoiding concurrent writes of test bridges. `regression.py` refuses missing drivers, supplies their environment explicitly and preserves TRX/logs and unique build binlogs. `--built` runs the compiled test assemblies when the host has only a compatible runtime. External semantic-provider and real-browser smoke tests remain explicit opt-ins; platform skips must be reviewed rather than counted as executed tests.

The complete original Go suite replay on Linux passed 820 tests and failed five client/setup tests because millisecond-resolution backup names collide. This is the original defect documented in `client-setup.md`; C# uses exclusive numeric-suffix backups. The affected Go packages are rerun with race instrumentation separately, and both outcomes are retained. Original-suite failures are recorded separately: they must not be hidden by changing the source pin, changing algorithms to satisfy map order, or reporting native C# scenarios as original Go tests. The receipt distinguishes those runs.

## Intentional differences and review status

| Classification | Difference | Evidence / review |
|---|---|---|
| Explicit user decision | UI default is `127.0.0.1:17477`, with address overrides retained | User instruction and harness memory `mem_585c52d21f13cecc`; UI default-port scenarios |
| Platform representation | .NET runtime/build identity and native opaque random values differ | Version normalization is narrow; stored formats, relationships and acceptance behavior remain checked |
| Documented engineering behavior | Timeout/cancellation supervision, path containment, strict HTTP validation and parser diagnostics follow the port's documented failure boundaries | `downstream-mcp.md`, `provider-facade.md`, `ui-writes-and-live.md`, and their receipts; do not silently normalize away functional failures |
| Proposed safety differences | Export redirect refusal/private Windows ACLs/sanitized diagnostics; explicit LAN exposure and linked-file rejection in companions | `developer-export-and-companions.md`; harness proposal `prop_45a01bd26e77f553` awaits review |
| Measured implementation tradeoff | Self-contained .NET startup, sync and process RSS exceed Go on the measured hosts | Tables below and raw samples; a passing local regression guard does not approve the Go-relative cost |
| Pending qualification | macOS execution and a clean-machine release run on every target architecture | Prepared CI and PORT-031 checks; no inferred pass from cross-compilation |

The review-loading guards are another documented engineering correction to the frozen frontend. `ui-review-port-changes.json` lists the exact reversible patch, so the Go byte-comparison still checks every other asset byte.

Other area-specific documented differences remain in their original receipts and implementation documents. This catalog classifies them; it does not manufacture user approval for an engineering proposal.

## Measurement method

`benchmarks/performance.py` uses independent synthetic repositories with identical 101-file C# contents and Git history, isolated user/config/state, no remote services and the same deterministic provider process implementation. Initialization and an initial sync precede measurement. Each comparison has nine new-process samples, five context warmups and 30 measured calls per established session with zero, one and four providers. It records complete raw samples, versions, executable SHA-256, medians/p95/MAD, actual provider request counts, serialized result bytes and harness-process RSS. RSS excludes fixture-provider children. Cold means a new process; OS caches are not flushed. Results are not general claims about a large real solution or semantic-indexer memory.

Calibration precedes verification. The local candidate regression limit is the measured calibration p95 plus three median absolute deviations, checked against an independent verification median for each latency and response-size series. These limits detect regressions against a measured candidate baseline; they are **not invented claims of Go-equivalent startup or memory**. Both latest host verifications meet every recorded limit. Limits and raw data are linked by hashes in the receipt.

Linux measurements run under WSL Ubuntu 26.10 with binaries on the mounted Windows filesystem and synthetic repositories under Linux temporary storage. This host has OpenSSL 4 only. The Microsoft self-contained runtime needs compatible native dependencies, so measurements explicitly use an isolated OpenSSL 3 library directory copied from the installed core24 runtime. System libraries were not changed. Do not present this as an unmodified Ubuntu 26.10 deployment test. Release qualification should run a supported base image with its ordinary dependencies; [Microsoft documents the Linux dependencies](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual).

## Latest measured comparison

Numbers are medians in milliseconds, except response bytes and RSS. The receipt retains every sample and the older calibration candidate used to verify the metadata-discovery improvement.

| Windows workload | Go | C# |
|---|---:|---:|
| New-process version | 13.38 | 167.29 |
| New-process MCP initialization | 51.85 | 286.39 |
| Warm context, no provider | 0.86 | 20.48 |
| Warm context, one provider | 73.07 | 52.31 |
| Warm context, four providers | 76.56 | 49.44 |
| No-op sync, including process startup | 478.20 | 978.77 |
| One-file sync, including process startup | 491.14 | 1020.24 |
| Response bytes, zero / one / four providers | 250 / 950 / 1022 | 250 / 950 / 1022 |
| Process RSS MiB, zero / one / four providers | 29.7 / 32.9 / 34.2 | 68.5 / 88.6 / 88.4 |

| Linux/WSL workload | Go | C# |
|---|---:|---:|
| New-process version | 111.01 | 566.20 |
| New-process MCP initialization | 157.80 | 785.51 |
| Warm context, no provider | 0.80 | 2.81 |
| Warm context, one provider | 14.97 | 6.82 |
| Warm context, four providers | 16.35 | 11.68 |
| No-op sync, including process startup | 224.75 | 990.81 |
| One-file sync, including process startup | 277.19 | 1024.54 |
| Response bytes, zero / one / four providers | 250 / 950 / 1022 | 250 / 950 / 1022 |
| Process RSS MiB, zero / one / four providers | 27.6 / 29.4 / 30.2 | 77.2 / 99.3 / 102.0 |

The initial Windows pilot had approximately 674 ms provider-free context and 786 ms with one provider. It identified repeated Git subprocess work. Ordinary discovery/HEAD/ref reads now use metadata directly, matching Go and allowing initialization without Git. Reftable HEAD reads retain the Git backend. Packed/loose/symbolic refs, linked worktrees, detached/unborn states, SHA-1/SHA-256 IDs and invalid/cyclic refs have regression scenarios. No time-based HEAD cache can hide a commit or branch switch.

Recent context history uses a session-bounded 16-entry cache keyed by exact commit, ordered requested paths, limit, and Git shallow/graft/config/packed-ref/replacement metadata. Results are read at the identified immutable commit; failures are not cached. Tests check reuse, distinct path filters and invalidation after a commit. This improves the repeatedly used MCP path while retaining visible measurements of the remaining cold-process costs. Benchmark limits should be recalibrated only with reviewed raw evidence, not enlarged merely to turn a failed run green.
