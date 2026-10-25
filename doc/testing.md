# Building, testing and measuring

Use the SDK pinned in `global.json`. Dependencies use Central Package Management
and committed lockfiles. Product code belongs in `src`, tests in `test`, benchmark
scripts in `benchmarks`, and documentation in `doc`.

```powershell
dotnet restore Harness.slnx --locked-mode '-bl:artifacts/restore-{}.binlog'
dotnet test --solution Harness.slnx --no-restore '-bl:artifacts/test-{}.binlog' --report-trx --results-directory artifacts/tests
python scripts/format_csharp.py --check
```

The .NET 10 Microsoft Testing Platform runner forwards unrecognized options to
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

`.github/workflows/tests.yml` runs standalone tests on Windows, Linux and macOS.
The release workflow adds native publish, repeat hashes and archive probes for
all four supported RIDs. Local execution does not imply those CI jobs ran.

## Performance

```shell
python benchmarks/performance.py --candidate /absolute/path/harness --output artifacts/performance.json --samples 10 --warm-samples 50
```

The benchmark records raw samples, median, p95 and median absolute deviation for
new-process version startup, MCP initialization, and repeated light `get_context`
calls in one session after five warmups. It creates private state without
providers; measure real provider workloads separately. Compare reports from the
same host and workload rather than treating one smoke sample as a baseline.
