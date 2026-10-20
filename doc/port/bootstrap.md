# Solution bootstrap and builds

TASK-0005 / PORT-004 creates `Harness.slnx` with the six production projects from
the [boundary proposal](solution-and-packages.md), plus unit tests, integration
tests and the existing storage process worker. All nine projects build with the
repository's analyzers and warnings as errors. Core has no project references;
Infrastructure, Providers, Mcp and Web reference Core; Cli references all five.
The existing MSBuild dependency guard enforces that direction.

The executable currently implements help and version. Providers, MCP and Web are
project boundaries for their existing later tasks; this bootstrap does not claim
their commands, tools, endpoints or UI are implemented. The existing SQLite
implementation remains in Infrastructure and its tests run through the solution.

## Restore, build and test

Install .NET SDK 10.0.402. Root `global.json` allows stable servicing patches within
that feature band (`latestPatch`) and selects native Microsoft Testing Platform.
It replaces the previous `10.0.0`/`latestMajor` setting, which could select a later
major SDK. Projects use one `net10.0` target, C# 14, nullable references and CPM.

From the repository root, in PowerShell:

```powershell
dotnet restore Harness.slnx -bl:{{}}
dotnet build Harness.slnx --no-restore -m -bl:{{}}
dotnet test --solution Harness.slnx --no-build --no-restore -bl:{{}} --report-trx
dotnet src/Harness.Cli/bin/Debug/net10.0/harness.dll --help
dotnet src/Harness.Cli/bin/Debug/net10.0/harness.dll version --json
```

Each MSBuild invocation needs its own binary log; the placeholder creates a fresh
name. Use ordinary `-bl:{}` syntax in shells that do not require PowerShell's
brace escaping. `NuGet.Config` maps the current public dependencies to nuget.org
and prevents inherited machine mappings from excluding them. User/global NuGet
configuration is not changed. Add an explicit source mapping if a future task
introduces a private dependency.

Every project has a `packages.lock.json`. The single-file CLI profile uses a
separate `packages.publish.lock.json`: the SDK adds ILLink analysis tooling for
single-file publishing even with trimming/AOT disabled. Restore locally to update lock files
after intentional dependency changes, review their diffs, and commit them with
the project/CPM edits. CI enables locked restore; it must not silently accept a
changed dependency graph:

```powershell
dotnet restore Harness.slnx --locked-mode -p:ContinuousIntegrationBuild=true -bl:{{}}
dotnet build Harness.slnx -c Release --no-restore -m -p:ContinuousIntegrationBuild=true -bl:{{}}
```

PackageReference entries remain versionless. The current application obtains
Hosting/DI/logging from the ASP.NET Core shared framework referenced by Web and
propagated to Cli. There is no redundant Hosting package reference. Tests use
only `xunit.v3` 4.0.0 with its MTP v2 integration and the centrally injected TRX
reporter. The legacy `TestingPlatformDotnetTestSupport` bridge was removed; it
does not work with the selected MTP/.NET 10 combination. Unused v2/VSTest and
GitLab-specific entries in the shared CPM catalog are not consumed.

For the Go interoperability test, build and set `HARNESS_GO_STORAGE_ORACLE` as
described in [storage validation](storage-implementation.md). It ran in this
task's validation. Without that helper, the test explicitly skips. The Unix
permission test explicitly skips on Windows. Test workers now locate the root
using `Harness.slnx`, rather than stopping at an intermediate Directory.Build.props.

## Composition and command behavior

`CliApplication.RunAsync` takes arguments and caller-owned stdout/stderr writers.
Spectre uses one lazily built Generic Host service provider, with deterministic
ownership and asynchronous disposal. It does not create a second container.
Short help/version commands do not start background hosted services, read appsettings
files, open project databases or start providers. Future long-running transport
commands must explicitly run the host lifecycle when adding hosted services.

All ILogger output goes to the supplied stderr writer. Help and version use
stdout without ANSI sequences. The process entry point handles Ctrl+C and passes
the cancellation token through Spectre to commands; cooperative cancellation
returns 130. Usage errors return 2 and operational failures return 1. Unit tests
exercise shared services, async disposal, cancellation and both output streams;
a process test runs version JSON from outside the repository.

Root `-v`/`--version`, `help`, `-h`, `-help`, `--help`, and `help version` are
supported. Full Go command/flag/help-text parity remains governed by the baseline
as the remaining commands are implemented. Help lists only implemented commands.

Version JSON retains the frozen six-field envelope, including the legacy
`go_version` key, whose value now describes the actual .NET runtime. Human output
uses the same version/commit/date/runtime/platform layout. SDK source-control
metadata supplies the commit. The date is empty in JSON ("unknown" in text)
unless `BuildTimestamp` is supplied; `modified` reflects the optional
`RepositoryModified` build input and defaults to false when unrecorded. No build
step scans the worktree to infer dirty state. Release automation should verify
its checkout and supply that value explicitly.

## Stable metadata and SourceLink

The old target invoked Git processes for every project and embedded the current
UTC time on each build. It was replaced by SDK source-control integration and
stable CLI-only assembly metadata. Copyright no longer reads the current year.
`VERSION_LABEL`, `BuildTimestamp` and `RepositoryModified` can be provided as
explicit build inputs; do not use wall-clock time in a reproducible release.
Timestamp values should describe the selected source commit in UTC.

CI uses portable PDBs and the .NET SDK's bundled SourceLink providers, which
support GitHub, GitLab, Azure Repos and Bitbucket without a provider-specific
PackageReference. No remote was configured in this checkout, so a downloadable
source mapping could not be verified. A release checkout with a remote should
inspect the produced PDB's source mapping. See [the SourceLink project guidance](https://github.com/dotnet/sourcelink#using-source-link).

Managed development builds default to AnyCPU; x64 and ARM64 configurations are
available. The misspelled/ineffective `PlatformTargets` property was removed.
The SDK infers the executable's actual PlatformTarget from its publish RID;
`linux-arm64` evaluation was verified to produce `arm64`. The existing RID list
remains win-x64, linux-x64, linux-arm64, osx-x64 and osx-arm64. Native non-Windows
execution still belongs in the corresponding CI/release checks.

## Release publish settings

The CLI's `Release.pubxml` profile publishes a self-contained single file with
native-library extraction enabled, embedded CLI symbols, and trimming and NativeAOT
disabled. Referenced projects emit optional portable PDB sidecars; the executable
runs without those files. To embed symbols for every project, also pass
`-p:DebugType=embedded` as a global publish property. Supply the RID explicitly:

```powershell
dotnet publish src/Harness.Cli/Harness.Cli.csproj -c Release -r win-x64 -p:PublishProfile=Release -o artifacts/publish/win-x64 -bl:{{}}
```

When intentionally updating publish dependencies, generate its lock with
`dotnet restore src/Harness.Cli/Harness.Cli.csproj -p:PublishProfile=Release -p:RuntimeIdentifier=win-x64 -bl:{{}}`.
Use the singular MSBuild property here: `dotnet restore -r` narrows the plural RID
set, whereas publishing retains the shared RID list. Review and commit both
ordinary and publish lock files after an SDK/dependency update.

Use the other supported RIDs in their native CI jobs and run the executable there.
Self-contained output includes the ASP.NET Core runtime needed by the Web project.
A framework-dependent alternative can be published without this profile; it needs
both .NET and ASP.NET Core installed. Native SQLite is extracted at startup;
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` can select a persistent directory private to the
current user. Do not enable AOT/trimming until reflection-based commands, tools
and serialization have been validated. See [Microsoft's single-file deployment guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).

Windows validation ran the self-contained CLI and, separately, a single-file
storage worker with the same publish settings. The worker loaded extracted
e_sqlite3.dll and applied all 19 migrations to a disposable database. This proves
the current Windows packaging path, not clean-machine or cross-platform release
parity. The full release/migration tasks remain outstanding.

## Validation receipts

The full solution suite passed 37 tests, with one Unix-permission test skipped on
Windows. Debug and Release/CI builds passed with zero warnings/errors. Locked
restore, ARM64 property evaluation, Windows single-file version output and native
SQLite migration smoke checks passed. No-op build receipts check that output
assembly hashes and write times remain unchanged. Detailed commands, dependency
versions and artifact hashes are retained in
[bootstrap-validation.json](inventory/bootstrap-validation.json); local binaries,
TRX reports and binlogs stay under ignored `artifacts`.
