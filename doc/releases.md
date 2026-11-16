# Release packaging and validation

Supported native hosts are Windows x64 (`win-x64`), Linux x64 (`linux-x64`), Linux
ARM64 (`linux-arm64`) and Apple Silicon macOS (`osx-arm64`). Intel macOS is not
supported. See the root [README](../README.md) for OS libraries and unsigned
macOS download trust steps.

Release executables are trimmed Native AOT .NET 11 applications. Build each RID
on its native host with the pinned SDK and compiler prerequisites: Visual Studio
C++ tools and Windows SDK, Linux `clang` and `zlib1g-dev`, or Xcode command-line
tools. Build Linux releases on the oldest supported distribution; binaries built
on a newer WSL distribution do not establish compatibility with older glibc.

```shell
python scripts/publish_release.py --version 0.1.0-preview --output dist/preview --verify-repeat
```

The publisher defaults to the current host and rejects other target RIDs. Each
invocation isolates intermediate/output directories. Committed
`packages.publish.lock.json` files cover the four-RID publish graph. Use
`--refresh-locks` only to regenerate a deliberately changed graph, review it,
then return to locked restores. The main archive contains `harness`,
`harness-graphify` and `harness-agents`; developer-tools contains `harness-collect` and `harness-share`.
Keep their included SQLite native library beside the executables. The browser UI is embedded in `harness`.

Archives carry README, build metadata, release notes and actual dependency
licenses/notices. The repository's [MIT license](../LICENSE), with copyright to
Matus Zakarovsky and David Makovsky, is included as the archive-root `LICENSE`.
`THIRD_PARTY_NOTICES.json` and `LICENSES` contain dependency/runtime notices and
the BSD notice for the Unicode tables; the project's own MIT license is separate.
`RELEASE.json` declares MIT and records source hashes, SDK, build state,
binary/archive hashes and repeat verification; `SHA256SUMS` covers release files.
Symbols and managed build outputs stay outside the archives.

```shell
python test/Harness.Packaging/run_probe.py --archive dist/preview/harness-VERSION-OS-ARCH.zip --developer-archive dist/preview/developer-tools/harness-VERSION-OS-ARCH.zip --output artifacts/native-smoke.json
python test/Harness.Packaging/verify_aot.py --release dist/preview --smoke artifacts/native-smoke.json --output artifacts/native-aot-verification.json
```

Use the actual archive filenames (`.tar.gz` on Unix). The runtime probe covers
CLI metadata, SQLite initialization, client configuration preservation, embedded
docs/packs, task lifecycle and YAML, MCP tools/knowledge/context, Graphify's
adapter handshake, UI assets/APIs and opt-in telemetry/companion services.
The verifier binds receipts to both exact archives and checks native executable
markers/headers, repeat hashes, SQLite sidecars, license checksums and absence of
managed/debug build output.

The release workflow runs these checks on each native host and additionally
starts the Linux executable in a runtime-dependencies-only container without
network access. This container check is a startup smoke; full functional probes
run in isolated child environments on the native host. Those hosts may have SDKs
installed even though the application children cannot resolve them through PATH.

## The Avalonia UI bundle

The Avalonia harness ui (BP-010) is a WebAssembly bundle: the publish output's
`wwwroot` of `src/Harness.Ui.Browser`.

**It is built once, not per runtime.** It is the same for every runtime and is not
part of the Native AOT publishes. Building it needs the `wasm-tools` workload
(SkiaSharp's WebAssembly libraries are linked into it); the executables do not.
So the release workflow's `ui-bundle` job builds it once:

- installs the workload;
- restores the browser host in locked mode;
- publishes the bundle;
- reports its size against the budget (`scripts/ui_bundle.py`);
- uploads it as an artifact.

**Each runtime's packaging step checks and ships it.** Every `qualify` job
downloads the bundle and passes it to `publish_release.py --ui-bundle`, which
checks that it is one and within the size budget. It records it in `RELEASE.json`
as `ui_bundle`: files, raw and Brotli bytes, a hash of its tree, and
`shipped: true`. The main executable embeds each Brotli variant when present
and otherwise the raw file; the server streams decompression for browsers that
do not accept Brotli. The main archive needs no UI directory.

A Windows x64 test build embedding the 5,625,039-byte Brotli bundle increased
`harness.exe` from 52,538,880 to 58,182,656 bytes (+5,643,776); the main ZIP
changed from 34,307,788 to 34,305,876 bytes. These are local builds with different
working-tree changes and are a size comparison, not a reproducibility claim. Release manifests
record each executable and archive size alongside the bundle size.

The TASK-0164 Windows/Edge headless run measured 58 files, 21,239,127 raw bytes
and 5,625,039 bytes with Brotli. Five warm Board reloads took 2.68, 2.27, 2.17,
1.91 and 1.99 seconds (median 2.17 seconds), compared with the spike's roughly
1.4-second warm median. These were separate runs of different application
versions, not a controlled comparison. Set `HARNESS_UI_TIMING_FILE` during the
browser smoke test to record five samples on another host.

BP-010 exit check: the view and action coverage is recorded by TASK-0151 through
TASK-0160; typed API contracts are exercised by `UiContractTests`; the release
serves the bundle at `/` and removes the old assets; both workflows publish the
browser host after installing `wasm-tools`; `Harness.Ui.Tests` checks view layout
and contrast and the browser smoke checks load, errors and a live update; the
screen-reader limitation and in-app find are documented in the guide and
`doc/architecture/avalonia-spike.md`. Workflow configuration is checked locally;
the hosted jobs are qualified when the branch runs in CI.

Locally, the same as the CI:

```shell
dotnet workload install wasm-tools
dotnet publish src/Harness.Ui.Browser -c Release -o artifacts/ui
python scripts/publish_release.py --version 0.13.0-preview --output dist/preview --ui-bundle artifacts/ui/wwwroot
```
