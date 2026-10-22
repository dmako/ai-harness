# Self-contained release packaging (PORT-031)

The port produces four release targets: `win-x64`, `linux-x64`, `linux-arm64`,
and `osx-arm64`. Intel macOS (`osx-x64`) is no longer supported.
All four executables use the shared
[`Release.pubxml`](../../configuration/Release.pubxml): self-contained .NET 10,
trimming and Native AOT enabled, with portable build symbols retained outside
archives. Native AOT executables load the included SQLite native library from
beside the executable; keep that library when moving or installing the tools.
UI, documentation, SQL migrations, and the five provider packs remain embedded.
The CLI uses System.CommandLine 2.0.13 with explicit typed bindings, replacing
Spectre.Console.Cli. Interactive terminal rendering still uses Spectre.Console.
See [Native AOT implementation and validation](native-aot.md) for TASK-0034.

The original PORT-031 inventory below describes the earlier untrimmed,
single-file release. Its user acceptance does not automatically qualify new
Native AOT builds on every supported OS/architecture.

The local preview is `dist/0.1.0-port-preview.2` (ignored build output).
[`release-validation.json`](inventory/release-validation.json) records executable
and archive hashes, sizes, license identities, probe counts, startup samples,
source hashes, historical automated checks, and final user acceptance. These are local preview artifacts,
not a published release: their metadata honestly records a modified working tree
based on commit `d09fb15` plus the source hashes in `RELEASE.json`.

## Archive layout and optional companions

Main archives contain **harness and harness-graphify**. Separate archives under
`developer-tools/` contain **harness-collect and harness-share**; normal installation
never adds these developer companions. Both kinds include `BUILD.json`, release
notes, the installation README (in new archives), the frozen upstream MIT notice, actual NuGet/runtime license metadata, and
available license/third-party notice texts. The root `SHA256SUMS` covers every
archive, release notes, and `RELEASE.json`.

Package inventory comes from each actual published `.deps.json`, including both
the .NET and ASP.NET runtime packs. Build analyzers are excluded. The repository
has no separate top-level license declaration for new C# contributions; `NOTICE.md`
records that fact rather than inferring a license. This preview does not resolve
that licensing choice.

| Target | Main archive MiB | Developer archive MiB | Execution evidence |
| --- | ---: | ---: | --- |
| Windows x64 | 82.14 | 92.41 | Native host, empty PATH and private user/cache; host has SDKs |
| Linux x64 | 80.92 | 90.81 | Clean Ubuntu 24.04 container, non-root, no network |
| Linux ARM64 | 76.78 | 86.18 | Clean image under CPU emulation; Linux functionality subsequently accepted by the user |
| macOS Apple Silicon | 77.33 | 86.93 | Native extraction/functionality manually verified by the user |

Sizes above describe the original preview. Its Intel macOS archives are historical
artifacts and are excluded from the supported release inventory.

The CLI apphost itself is 104.5–114.9 MiB on Unix and 108.2 MiB on Windows.
TASK-0034 replaces that deployment model with Native AOT; its size and execution
evidence are recorded separately rather than overwriting the historical inventory.

## Repeatable build

With the pinned SDK from `global.json`, Python 3, Git, and package-feed access:

```powershell
python scripts/publish_release.py --version 0.1.0-port-preview.2 --output dist/new-preview --verify-repeat
```

Use a fresh output directory. The script defaults to the current supported host
RID. Native AOT requires a native target toolchain: run on each target OS and
architecture (the script rejects other RIDs). Every restore uses locked
`packages.publish.lock.json`; every MSBuild invocation has its own binary log.
`--refresh-locks` is an explicit bootstrap option: review and commit lock changes
before locked CI. RID selection uses `-p:RuntimeIdentifier`, retaining the shared
four-RID lock set. Builds use an isolated artifacts directory, allowing Windows
and WSL qualification without sharing intermediate files. Profiles contain no
credentials or deployment settings.

The script pins version, commit, build timestamp/source epoch, path mapping,
archive order, timestamps, permissions, and tar ownership. `--verify-repeat`
publishes each executable twice and rejects unequal executable or SQLite library hashes. The original
five-target preview verified 20 executables; 16 of those belong to the four
currently supported targets. This is same-host binary reproducibility, not a claim that
independent builds on different SDK hosts produce identical archive bytes.

[`release.yml`](../../.github/workflows/release.yml) provides manual build/test/
publish qualification on native Windows, Ubuntu x64/ARM64, and macOS ARM64
runners. It runs the complete frozen-driver suite before packaging, repeats
publishes, executes the runtime-isolated probe, and additionally runs Linux in a
runtime-dependencies-only container. It uploads artifacts and evidence and never
publishes a release. Runner labels follow the
[GitHub-hosted runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
No C# repository remote is configured locally, so this workflow has **not run**.

## Runtime prerequisites and package checks

The core needs no preinstalled .NET, Node, Python, Go, or npm runtime. Git remains
a prerequisite for sync/history; without it, the packaged CLI initializes and
serves stored knowledge, and sync returns the explicit install-Git diagnostic.
Semantic provider backends remain optional external tools with their own
requirements. `harness-graphify` is the shipped protocol adapter, not a bundled
Roslyn backend.

Linux still needs the supported OS native dependencies, including OpenSSL 3.
An earlier WSL Ubuntu 26.10 test host had OpenSSL 4 only, so DLL test success there was
not evidence that Microsoft's self-contained runtime works without additional
native prerequisites. Release smoke tests use the official .NET 10 Ubuntu 24.04
runtime-dependencies-only image, which has no language runtimes installed.
The user subsequently verified Linux after a system update and installing
`libssl3t64`, and macOS after a system update and explicitly trusting the
unsigned downloads. The [installation README](../../README.md#platform-prerequisites)
contains distribution-dependent prerequisites and per-file macOS trust steps,
with links to Microsoft and Apple documentation.

Current Native AOT packages carry a SQLite sidecar and do not extract a managed
bundle. The following extraction requirements apply to the historical
untrimmed single-file packages only. Bundled SQLite extracts to a writable native-library cache. On locked-down hosts,
set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to an owner-private directory on executable
storage. The probe uses a private `0700` parent on Unix, an owner-only inheritable
DACL on Windows, and confirms native SQLite extraction and successful database
use. Docker's temporary filesystem must allow execution: `--tmpfs
/tmp:rw,exec,mode=1777`. The container runs as UID 65532 with no network and
read-only release/probe mounts. No system library or global runtime was installed.

The test-only probe instruments the hash-verified frozen Go `harness-e2e` runner.
Original install/MCP/doctor/uninstall scenarios remain; instrumentation supplies
normal private Windows cache/temp variables, explicitly extracts the optional
developer archive, and adds UI/docs/assets, all five pack expansions, adapter
handshake without a backend, collector/export/share, permissions and Unix SIGTERM
checks. It is **not** reported as an unmodified upstream test. The initial Windows
fixture omitted writable Windows cache variables; the initial Linux tmpfs was
noexec. Their failures are preserved in `artifacts`, alongside corrected runs.

To build and run the native probe (Go is a build tool only):

```powershell
python test/Harness.Compatibility/Packaging/build_probe.py --source artifacts/parity-complete-source --go artifacts/compatibility/toolchain/go/bin/go.exe --output artifacts/release-probes/probe-windows-amd64.exe --os windows --arch amd64
python test/Harness.Compatibility/Packaging/run_probe.py --probe artifacts/release-probes/probe-windows-amd64.exe --archive dist/0.1.0-port-preview.2/harness-0.1.0-port-preview.2-windows-amd64.zip --developer-archive dist/0.1.0-port-preview.2/developer-tools/harness-0.1.0-port-preview.2-windows-amd64.zip --output artifacts/release-windows-smoke-final.json
python test/Harness.Compatibility/Packaging/record_validation.py --release dist/0.1.0-port-preview.2
```

The Windows and Linux CLI integration tests also exercise all original root
commands and list/start/stop session behavior against a fake backend. Packaging
review found and filled the missing `docs` and `providers sessions` registrations.

## Final qualification and acceptance

On 2026-10-22 the user confirmed native extraction and functionality on Windows,
Linux, and macOS, and explicitly verified the inventory. Windows worked as
expected; Linux needed an OS update and `libssl3t64`; macOS needed an OS update
and explicit trust for the unsigned/quarantined executables. This is manual
user-reported acceptance, recorded separately from the original automated runs in
[`release-acceptance.json`](inventory/release-acceptance.json).

TASK-0031 is complete for the four supported RIDs. TASK-0030 was also closed on
the user's earlier manual macOS confirmation. The hosted workflow remains prepared
but unexecuted locally; no new clean-VM, per-architecture transcript, or Windows
console-signal test is inferred from the user's broad functionality verification.
Original probe limitations and hashes remain in the validation receipt.

Startup values in the receipt are single smoke samples: first `version` includes
initial bundle extraction, while `warm version` reuses the extraction cache.
ARM64 emulation timings are not native performance results. Repeated Go/C#
measurements and their documented tradeoffs remain in
[`parity-and-performance.md`](parity-and-performance.md).
