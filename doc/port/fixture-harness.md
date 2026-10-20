# Executable compatibility fixtures

TASK-0002 provides a Python standard-library development harness before the C#
solution is bootstrapped. It runs executable contracts in disposable Git
repositories with isolated home, profile, application-data, Git configuration,
temporary and `HARNESS_CONFIG_DIR` paths. The product has no Python dependency.

The saved Windows fixture uses Go commit
`9cb401dd2de4f85dd089fea86a0b139d81b178c6`, tree
`068ee1a00db7659e70863a45a0d50642c606e251`, built with Go 1.27.1. Its metadata
records the full executable version and SHA-256. It is separate from the
machine's installed harness. The source snapshot, toolchain, module cache and
oracle executable are local ignored artifacts.

## Build the pinned oracle

Use a Go 1.27.1 compiler. Go's official portable archives and checksums are
available from [Go downloads](https://go.dev/dl/). A compiler is needed only to
rebuild the oracle; it is not needed for offline fixture tests.

```powershell
python test/Harness.Compatibility/oracle.py `
  --source C:/repos/personal/ai-harness `
  --go artifacts/compatibility/toolchain/go/bin/go.exe
```

The builder exports the pinned Git objects into a fresh output directory,
excluding repository state, and uses `-mod=readonly`, the original `go.sum`,
isolated Go caches, and an explicit build identity. It refuses an existing
source directory to prevent stale source from entering the build. A rebuild
can use a fresh `--output` beneath `artifacts/compatibility`. The compiler may
need network access to download the pinned public modules.

## Capture and compare

```powershell
python test/Harness.Compatibility/compatibility.py capture `
  --executable artifacts/compatibility/oracle/harness.exe `
  --fixture test/fixtures/compatibility/go-windows-amd64.json

python test/Harness.Compatibility/compatibility.py compare `
  --executable artifacts/compatibility/oracle/harness.exe `
  --fixture test/fixtures/compatibility/go-windows-amd64.json
```

Capture verifies the source commit and sibling `oracle.json` executable hash,
and checks expected scenario outcomes before writing a fixture. Comparison
captures the same scenarios in a fresh sandbox and reports field-level
differences with a nonzero exit code. It never updates expected results. For
the C# implementation, point `--executable` at its apphost or self-contained
executable. A C# candidate does not yet exist at this milestone; Go-to-Go replay
checks the runner's determinism, not C# parity.

The UI cases require loopback socket access. All subprocess communication
apart from the loopback UI uses stdio; no external provider, index, paid agent
or developer telemetry service is invoked. Process reads, CLI invocations,
HTTP requests and shutdowns have bounded deadlines. The deliberately stalled
provider takes about 30 seconds to exercise the pinned Go registry deadline:
an 800 ms provider configuration does not override an already-deadlined parent
context. Preserve this observed behavior in the baseline and review any fix in
the provider implementation task.

## Captured scenarios

| Surface | Coverage |
| --- | --- |
| CLI | Version JSON, root/task help, unknown command/flag, missing argument, init, status; stdout and stderr are separate from exit code |
| MCP | Initialization, all 30 tools with SDK-generated input/output schemas, ping, Unicode state put/get, invalid arguments, unknown tool, malformed JSON frame, recovery after malformed input and state retrieval after process restart |
| Providers | Native harness-provider/1 and generic MCP mappings; normal results, tool errors, genuine timeouts, stderr diagnostics and disconnects; incoming/outgoing provider messages are retained |
| UI | Project/workspace JSON, missing route, foreign Host, missing write guards and malformed guarded JSON |
| Storage | Synthetic schema versions 1, 4, 5, 13 and 19 migrate to 19 and retain a Unicode diagnostic value with integrity intact |

The fixture includes complete protocol schemas and messages. It is representative
coverage for building the port, not the complete release regression suite from
PORT-030. Only Windows amd64 has been exercised here; another platform needs its
own recorded fixture and validation.

## Normalization and strict comparison

Allowed substitutions are deliberately narrow:

- The exact sandbox root, test-tool directory, executable and Python interpreter
  paths become named placeholders; unrelated paths and suffixes stay visible.
- Actual build version, commit and date values are substituted only in their
  corresponding metadata fields; `go_version` is treated as runtime metadata.
- Complete RFC3339 values in named lifecycle timestamp fields become `<TIME>`;
  timestamps inside source snippets or arbitrary strings are preserved.
- Opaque IDs with known harness prefixes are consistently mapped in named ID
  fields and textual envelopes. Repeated identities keep the same placeholder.
  Source snippets and ordinary content are not treated as generated IDs.
- Numeric `duration_ms` and `latency_ms` observations become duration markers.
  Exact elapsed runtime is not asserted.
- JSON inside MCP text content is compared structurally and retains its separate
  presence alongside structured content. CRLF/LF in CLI streams is normalized.

Object property order is ignored; equivalent JSON numbers such as 1 and 1.0
compare equally. Array order, field presence, explicit null,
empty collections, numeric versus string/boolean types, exit codes, errors and
stdout/stderr separation and JSON versus text stream formats remain significant. Unexpected protocol stdout,
transport failures and hangs fail capture rather than being normalized away.
Provider negotiation messages are retained, so intentional SDK-level negotiation
differences require review when the C# client is implemented.

## Historical databases and offline tests

The five committed SQLite files are generated from
`doc/port/inventory/storage.json`, with a fixed migration timestamp and one
synthetic diagnostic row. They contain no personal project records. Their
manifest records schema versions and hashes; tests open them read-only. The
runner copies them into its disposable repository before migration.

To create them at a new, empty fixture destination, use
`compatibility.py databases --output <empty-directory>`. It refuses to overwrite existing databases. Existing fixtures
should change only after intentional baseline review.

```powershell
python -m unittest discover -s test/Harness.Compatibility -p test_compatibility.py -v
```

Offline tests cover environment isolation, semantic comparisons, identifier
relationships, preservation of source content, injected exit/stderr/payload
regressions, synthetic database hashes/integrity, generated schemas and every
fake provider mode. They need Python and Git, but no Go compiler or .NET SDK.
