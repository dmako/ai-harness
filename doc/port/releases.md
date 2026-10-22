# Self-contained release packaging (PORT-031)

The port produces five release targets: `win-x64`, `linux-x64`, `linux-arm64`,
`osx-x64`, and `osx-arm64`. All four executables use the shared
[`Release.pubxml`](../../configuration/Release.pubxml): self-contained .NET 10,
single-file apphosts, bundled native SQLite, embedded debug information, and
trimming/NativeAOT disabled. UI, documentation, SQL migrations, and the five
provider packs are embedded resources. No new application package dependencies
were introduced for packaging.

The local preview is `dist/0.1.0-port-preview.2` (ignored build output).
[`release-validation.json`](inventory/release-validation.json) records executable
and archive hashes, sizes, license identities, probe counts, startup samples,
source hashes, and remaining qualification work. These are local preview artifacts,
not a published release: their metadata honestly records a modified working tree
based on commit `d09fb15` plus the source hashes in `RELEASE.json`.

## Archive layout and optional companions

Main archives contain **harness and harness-graphify**. Separate archives under
`developer-tools/` contain **harness-collect and harness-share**; normal installation
never adds these developer companions. Both kinds include `BUILD.json`, release
notes, the frozen upstream MIT notice, actual NuGet/runtime license metadata, and
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
| Linux ARM64 | 76.78 | 86.18 | Same clean image under CPU emulation; native hardware pending |
| macOS Intel | 81.73 | 91.77 | Cross-published only; native execution pending |
| macOS Apple Silicon | 77.33 | 86.93 | Cross-published only; native execution pending |

The CLI apphost itself is 104.5–114.9 MiB on Unix and 108.2 MiB on Windows.
This cost is explicit; trimming or NativeAOT must be a separate task with startup,
reflection/protocol, assets, SQLite, and cross-platform evidence before adoption.

## Repeatable build

With the pinned SDK from `global.json`, Python 3, Git, and package-feed access:

```powershell
python scripts/publish_release.py --version 0.1.0-port-preview.2 --output dist/new-preview --verify-repeat
```

Use a fresh output directory. `--rids` selects targets. Every restore uses locked
`packages.publish.lock.json`; every MSBuild invocation has its own binary log.
`--refresh-locks` is an explicit bootstrap option: review and commit lock changes
before locked CI. RID selection uses `-p:RuntimeIdentifier`, retaining the shared
five-RID lock set. Profiles contain no credentials or deployment settings.

The script pins version, commit, build timestamp/source epoch, path mapping,
archive order, timestamps, permissions, and tar ownership. `--verify-repeat`
publishes each apphost twice and rejects unequal executable hashes: all 20 final
executables passed. This is same-host binary reproducibility, not a claim that
independent builds on different SDK hosts produce identical archive bytes.

[`release.yml`](../../.github/workflows/release.yml) provides manual build/test/
publish qualification on native Windows, Ubuntu x64/ARM64, and macOS Intel/ARM64
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
The local WSL Ubuntu 26.10 host has OpenSSL 4 only, so DLL test success there is
not evidence that Microsoft's self-contained runtime works without additional
native prerequisites. Release smoke tests use the official .NET 10 Ubuntu 24.04
runtime-dependencies-only image, which has no language runtimes installed.
See [Microsoft's Linux dependency guidance](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual).

Bundled SQLite extracts to a writable native-library cache. On locked-down hosts,
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

## Qualification still pending

Native macOS execution on both architectures, physical Linux ARM64 hardware,
and Windows console Ctrl-C/Break behavior still need execution evidence. Windows
service processes were force-cleaned by the probe; Unix services exited cleanly
on SIGTERM. No Windows clean-VM run or hosted CI run is claimed.

Startup values in the receipt are one smoke sample per command; first `version`
includes initial bundle extraction and `warm version` reuses the extraction cache.
ARM64 emulation timings are not native performance results. The larger repeated
Go/C# performance experiment and its remaining tradeoffs are documented in
[`parity-and-performance.md`](parity-and-performance.md).

TASK-0031 remains open for native qualification. TASK-0030 likewise remains open
for macOS parity execution; its Windows/Linux suite and measured regression
guards already passed.
