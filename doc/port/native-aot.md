# Trimming and Native AOT (TASK-0034)

Product assemblies enable `IsAotCompatible` in `Directory.Build.props`, so normal
builds run trimming and AOT analyzers with warnings treated as errors. The shared
release profile enables `PublishTrimmed`, `PublishAot`, and `SelfContained`.
It disables the managed single-file bundler: Native AOT already produces a native
executable, and SQLite is delivered as a native library beside it.

## Implementation

- System.CommandLine 2.0.13 replaces Spectre.Console.Cli. `CliCommands` defines the
  command tree and aliases; `CommandBindings` creates typed options, arguments,
  defaults, and settings directly. Shell completion reads that same command tree.
  There is no reflection-based attribute binder or alternate native-only parser.
  Spectre.Console remains for interactive terminal rendering.
- `HarnessJson` uses assembly-local generated JSON metadata. Heterogeneous wire
  objects use `JsonObjectMap`; regular dictionary keys remain data, while wire
  member names follow the selected naming policy. Lazy sequences are written
  without generating serializers for iterator implementation types. Unknown DTOs
  fail explicitly; there is no reflection fallback. Add new named wire models to
  the relevant `ProjectJsonContext` when extending the protocol.
- Configuration DTOs use ordinary setters so absent properties retain their
  initializers during generated JSON deserialization. Tomlyn uses a generated
  `TomlTable` context. Backlog export writes YAML representation nodes rather
  than reflecting over dictionaries; string scalars are quoted, and appended
  task sequences omit the YAML document terminator.
- Web, Collect, and Share enable ASP.NET's request delegate generator. HTTP JSON
  responses use generated metadata or explicit JSON nodes/text. Existing embedded
  assets, documentation, migrations, and provider packs remain embedded.

## Build and deployment

Use the pinned .NET SDK and a native compiler for the target host. Windows needs
the Visual Studio C++ build tools and Windows SDK; Linux needs `clang` and
`zlib1g-dev`; macOS needs Xcode command line tools. See Microsoft's
[Native AOT prerequisites](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
and [ASP.NET Core AOT guidance](https://learn.microsoft.com/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0).

```powershell
python scripts/publish_release.py --version 0.1.0-aot --output dist/aot --verify-repeat
```

The publisher defaults to the current host RID and rejects other targets. Build
each of `win-x64`, `linux-x64`, `linux-arm64`, and `osx-arm64` on its own target
host; the release workflow has a native job for each. Build outputs are isolated
per invocation so Windows and WSL do not share NuGet assets or intermediate
assemblies. Publish lockfiles retain the shared four-RID graph. Review changed
locks with `--refresh-locks`, then use the default locked restore in CI.

Archives include `e_sqlite3.dll`, `libe_sqlite3.so`, or `libe_sqlite3.dylib` when
present in the publish output. Keep that library beside the executables. The
publisher rejects conflicting SQLite library copies and checks executable and
library hashes on repeat publication. Symbols and managed build outputs remain
build artifacts, outside release archives. The release manifest records
`native_aot: true`, `trimmed: true`, and `single_file: false`.

Build Linux distribution artifacts on the oldest supported target distribution;
building on a newer WSL distribution does not establish compatibility with older
glibc versions. The CI baseline uses Ubuntu 24.04. Runtime OS libraries, including
OpenSSL 3, are still required; see the root README.

## Qualification

TASK-0034 has its own validation record in
[`native-aot-validation.json`](inventory/native-aot-validation.json). The earlier
PORT-031 release inventory and user acceptance remain historical evidence for
the previous untrimmed bundles. They do not qualify changed AOT artifacts on
macOS or ARM64. The existing packaging probe supports both deployment models:
it verifies SQLite use and either legacy extraction or the AOT sidecar, with no
language runtimes on the child process PATH.

The Windows main archive is approximately 21 MiB, compared with 82 MiB for the
previous untrimmed release. Both comparisons include the main CLI, Graphify
adapter, native dependencies, README, and license notices.

The final managed build has zero warnings. Unit validation reports 465 passed
and one platform skip. Locked restores pass for all sixteen application/RID
combinations. The full integration run reports 364 passed, six skipped, and
three failures; all three failures pass in the subsequent sixteen-case targeted
run. The targeted run also covers a corrected loading-session startup assumption
and a UI fixture polling race with its worker's session-file writes. These
results are recorded separately rather than claiming a completely green full
integration invocation.

To verify a candidate after publishing and running its packaging probe:

```shell
python test/Harness.Compatibility/Packaging/verify_aot.py --release dist/aot --smoke artifacts/native-smoke.json --output artifacts/native-aot-verification.json
```

This checks the Native AOT runtime marker and platform executable headers,
archive and license checksums, repeated executable hashes, native SQLite
sidecars, absence of managed/debug build outputs, and that the runtime probe
used these exact main and developer archives. The release workflow performs this
check for each native RID job; macOS and ARM64 execution of this changed AOT
release remains pending until those jobs or equivalent native checks run.
