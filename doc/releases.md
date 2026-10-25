# Release packaging and validation

Supported native hosts are Windows x64 (`win-x64`), Linux x64 (`linux-x64`), Linux
ARM64 (`linux-arm64`) and Apple Silicon macOS (`osx-arm64`). Intel macOS is not
supported. See the root [README](../README.md) for OS libraries and unsigned
macOS download trust steps.

Release executables are trimmed Native AOT .NET 10 applications. Build each RID
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
then return to locked restores. The main archive contains `harness` and
`harness-graphify`; developer-tools contains `harness-collect` and `harness-share`.
Keep their included SQLite native library beside the executables.

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
