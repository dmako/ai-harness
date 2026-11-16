# Building, testing and measuring

Use the SDK pinned in `global.json`. Dependencies use Central Package Management
and committed lockfiles. Product code belongs in `src`, tests in `test`, benchmark
scripts in `benchmarks`, and documentation in `doc`.

```powershell
dotnet restore Harness.slnx --locked-mode '-bl:artifacts/restore-{}.binlog'
dotnet test --solution Harness.slnx --no-restore '-bl:artifacts/test-{}.binlog' --report-trx --results-directory artifacts/tests
python scripts/format_csharp.py --check
```

The .NET 11 Microsoft Testing Platform runner forwards unrecognized options to
the test applications. Do not pass the MSBuild `-m` flag to `dotnet test`: it
causes exit code 5 during the test-host handshake, reported as zero tests run.
Use `--max-parallel-test-modules` when test-module concurrency needs a limit;
see the [Microsoft Testing Platform command reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-mtp).

Each MSBuild invocation should retain its own uniquely named binlog. Format C#
with the pinned local CSharpier tool through `scripts/format_csharp.py`. Respect
`.editorconfig`, `configuration/CodingStyle.editorconfig` and
`configuration/NamingConvention.editorconfig`, including the soft 120-character
line target, mandatory control-flow braces, pattern matching and `is null` /
`is not null`. Assertions use AwesomeAssertions; JSON test data use raw literals.

## Test boundaries

- `Harness.Unit` covers domain rules, retrieval budgets, configuration, schemas,
  client-file editing, redaction and local analysis with synthetic fixtures.
- `Harness.Integration` covers SQLite migrations, recovery, process contention,
  project/workspace lifecycle, sync, MCP, providers, UI and developer tools.
  Historical schemas are created from selected embedded migrations at runtime.
- `Harness.ProviderWorker` and `Storage/Worker` are deterministic .NET process
  fixtures. They never ship as product dependencies.
- `Harness.Packaging/run_probe.py` exercises main and developer archives through
  their actual executables. Python runs on the test host; child PATH is empty,
  DOTNET_ROOT is nonexistent and HOME/config/cache are private. Unix fixtures use
  native temporary storage so WSL permission checks are meaningful.

Tests are independent of another repository or a separate language toolchain.
Optional installed tools remain explicit: set `HARNESS_UI_BROWSER` for Chrome,
`HARNESS_REAL_GRAPHIFY` for its backend, or `HARNESS_REAL_CODEBASE_MEMORY` for that
server. Missing optional tools, Unix-only link/mode scenarios on Windows, and an
occupied default UI port produce explained skips rather than false passes.

The browser tests (`UiBrowserTests`, with `HARNESS_UI_BROWSER` set to a 64-bit
Chrome or Edge) share one headless browser per run, the `SharedBrowser` assembly
fixture. Each page a test opens gets a CDP browser context of its own, disposed
when the test ends, so storage, cookies and cache never pass between tests; a
browser that died or stopped answering is replaced before the next test. A test
that passes `BrowseAsync` a profile gets a browser of its own on that profile.
The whole-UI check runs its theme/width passes on parallel pages.

`.github/workflows/tests.yml` runs standalone tests on Windows, Linux and macOS.
The release workflow adds native publish, repeat hashes and archive probes for
all four supported RIDs. Local execution does not imply those CI jobs ran.

### The Avalonia UI (BP-010)

`Harness.Ui.Tests` runs with the solution. These are the headless view tests: the
shared views against `FixtureApi`, the UI checks, the contracts' strictness, and
the router (see `test/Harness.Ui.Tests/README.md`). They need no workload.

The browser bundle is different: `src/Harness.Ui.Browser` needs the `wasm-tools`
workload, because SkiaSharp's WebAssembly libraries are linked into
`dotnet.native.wasm`. It is therefore not built by the solution build
(`<Build Project="false" />` in `Harness.slnx`). Build it on its own, into a new
folder, since a publish into a used folder keeps the old fingerprinted files
beside the new ones:

```powershell
dotnet workload install wasm-tools
dotnet publish src/Harness.Ui.Browser -c Release -o artifacts/ui
python scripts/ui_bundle.py artifacts/ui/wwwroot
```

The tests that use the bundle read it from `HARNESS_UI_TEST_BUNDLE`, else from
`artifacts/ui/wwwroot`, and skip, saying how to build it, when it is missing:

- `UiBrowserSmokeTests`, the browser smoke test. It also needs `HARNESS_UI_BROWSER`.
- `UiBundleBudgetTests`. The Brotli total must stay within 6.0 MB, 25 % over the
  spike's 4.81 MB. Raise the budget here and in `scripts/ui_bundle.py` together,
  on purpose.

The smoke test stages the bundle beside a copy of the executable and serves it at `/`;
`HARNESS_UI_TEST_BUNDLE` is a test-only override for the source bundle.
Set `HARNESS_UI_NATIVE_EXECUTABLE` to run the browser smoke against an extracted
release executable with its embedded bundle instead. The release workflow does
this on Linux x64 after building the package.

The `ui` job of `tests.yml` does all of this on Linux with Chrome, in order:

1. installs the workload;
2. runs the locked restore with it installed;
3. publishes the bundle;
4. reports its size in the step summary;
5. runs `Harness.Ui.Tests`, the bundle tests, the contract tests and the
   asset-serving tests;
6. uploads the bundle.

## Avalonia designer previews

Open a view under `src/Harness.Ui/Views` in an Avalonia-compatible IDE and open
its XAML preview, using the **Debug** configuration. The designer uses the
`d:DataContext` declared by the view and the application styles in
`Harness.Ui/App.axaml`. `StyleGallery` is self-contained and needs no data context.
See Avalonia's [previewer and design-time settings guide](https://docs.avaloniaui.net/docs/app-development/xaml-preview-and-design-settings)
for IDE setup and troubleshooting.

Preview data lives in `Harness.Ui/Preview`. Project, workspace, task and
knowledge examples are fictitious. The preview HTTP handler serves embedded test
fixtures and refuses writes; it never contacts a harness server. Each preview
gets its own in-memory preferences and services. The fixtures are embedded only
in Debug builds, so use Debug when opening previews. Production data contexts
are supplied by the host as usual.

Some reusable controls, such as `SectionView`, `DialogLayer`, and `TaskCard`,
normally receive body templates or commands from a parent view. Their standalone
previews show the available sample state; preview the parent page to inspect the
complete composition. Use `PreviewTests` for a headless check of sample data,
rendering and runtime data-context isolation.

## Performance

```shell
python benchmarks/performance.py --candidate /absolute/path/harness --output artifacts/performance.json --samples 10 --warm-samples 50
```

The benchmark records raw samples, median, p95 and median absolute deviation for
new-process version startup, MCP initialization, and repeated light `get_context`
calls in one session after five warmups. It creates private state without
providers; measure real provider workloads separately. Compare reports from the
same host and workload rather than treating one smoke sample as a baseline.
