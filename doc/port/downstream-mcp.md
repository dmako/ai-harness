# Downstream MCP providers (PORT-013)

TASK-0013 implements the process client and configurable generic provider from
the frozen Go revision `9cb401dd2de4f85dd089fea86a0b139d81b178c6`. It adds
provider adapters and process infrastructure; CLI composition and the public
code-intelligence MCP facade remain separate tasks.

## Boundaries and dependencies

`Harness.Core` owns the existing provider configuration records and a small
`IProviderProcessFactory`/`IProviderProcess` port. Moving the records out of
Infrastructure lets configuration loading and adapters share their types while
preserving the six-assembly dependency graph. Core exposes no SDK types.
`Harness.Infrastructure.Processes.ProviderProcessFactory` implements the port;
`Harness.Providers.Mcp` implements the MCP client, supervisor and generic mapping.
Construction is passive. Declared capabilities and cached health do not start a
process or issue RPCs.

The adapter uses `ModelContextProtocol.Core` 2.2.0, already defined in CPM, for
JSON-RPC, stdio framing, protocol negotiation, cancellation and typed tool
results. It explicitly offers protocol revision `2025-11-25`, which the SDK
supports, to interoperate with the pinned providers without the SDK's newer
default discovery exchange. Tool discovery follows cursors and rejects cycles;
limits are 256 pages and 10,000 tools. No additional process wrapper dependency
or runtime Python/Node dependency is introduced.

## Process and session lifecycle

Process options carry an executable, individual arguments, a working directory
and environment overrides. Environment inherits the host's values; a null
override removes a variable. An empty working directory inherits the current
directory. The generic adapter uses the project root when `dir = "project"` is
configured and otherwise inherits the current directory.
This task adds no new TOML environment directive. Commands resolve against the
effective PATH, arguments are passed without shell expansion, and Windows
batch files require an explicitly configured interpreter. NUL-containing
commands/arguments and invalid environment names are rejected.

The supervisor starts on first use and shares one startup across concurrent
calls. Defaults match Go: three failures per five minutes, with 500 ms
exponential backoff capped at eight seconds. Failed starts and disconnected
sessions consume the budget; caller cancellation does not. Calls during backoff
fail immediately. Health reads a small cached snapshot, so a slow handshake
cannot hold up a UI health section. Close cancels startup, waits for current and
retired sessions, and concurrent closes share the same cleanup task.

Startup defaults to 15 seconds and calls to 30 seconds. Both honor the earlier
caller deadline. A configured shorter call deadline always applies even when
the registry supplies a longer parent deadline. This deliberately corrects the
pinned Go client's inherited-context bug. Timeout terminates the session so a
server ignoring cancellation cannot leave a hung child; the supervisor can
restart it. Normal caller cancellation sends protocol cancellation and leaves a
healthy session reusable. The registry propagates adapter timeout status while
allowing sibling providers to complete. A launch finishing after startup timeout
is asynchronously reaped rather than becoming an unowned child.

Stderr has its own continuous reader and a bounded 64 KiB tail, separate from
protocol stdout. Diagnostics redact credentials. Newline-delimited frames are
limited to 16 MiB before unbounded SDK buffering; configured maxima are 64 MiB
per frame and 4 MiB for the stderr tail. Regex extraction uses a one-second
nonbacktracking matcher. Tool errors remain distinct from JSON-RPC errors and
transport failures.

## Descendant cleanup

On Windows, native `CreateProcessW` uses STARTUPINFOEX job-list and handle-list
attributes. The child enters a kill-on-close job atomically at creation, and only
its three redirected handles are inherited. This closes the race where a child
could spawn a grandchild and exit before post-launch job assignment. Closing the
job terminates descendants even after the direct child has exited. Unicode argv
quoting and environment blocks are built explicitly.

On Unix, `posix_spawn` creates a new process group before executing the server,
with explicit argv/environment, pipe file actions and working directory. A
dedicated reaper waits for the direct child. Shutdown closes stdin, allows two
seconds for graceful exit, then sends SIGTERM and SIGKILL to the group; process
exit also kills the group immediately to release pipes inherited by descendants.
The final reap has a five-second bound. Pipes are disposed even if reaping fails.
The implementation requires Linux glibc 2.29+ or modern macOS for
`posix_spawn_file_actions_addchdir_np`; supported packaging RIDs are 64-bit.

Process groups contain ordinary descendants, including grandchildren surviving
their parent. A Unix process deliberately escaping with `setsid` is outside that
group, and abrupt host death does not supply Windows job-object semantics.
Deployments needing containment across host crashes require a service manager
or container. Native paths were exercised on Windows x64 and Linux x64 under
WSL; macOS has not been executed in this environment.

## Generic mapping behavior

`GenericMcpProvider.Build` consumes the existing capability mapping records.
The refresher interface is exposed only when refresh is configured. It preserves
ordered `first`/`all` chains, provider-local limits, partial errors, miss markers,
probe calls and UTF-8 response byte accounting. The accounting measures mapped
tool text rather than transport envelope bytes.

Arguments support the pinned query, quoted-regex, root, path, absolute-path,
limit, expanded-limit, budget, package, version, type, target, name and project
placeholders. Whole numeric placeholders become numbers, empty whole
placeholders are omitted, and literal JSON values retain their types. Absolute
request paths are constrained lexically to the configured root. This intentional
correction rejects outside-root paths instead of silently mapping them.

Responses prefer structured content, otherwise concatenate text blocks and
decode JSON or configured regex groups. Mapping supports dotted fields, array
indices, wildcard arrays/objects, nearest object keys, predicates, overview text,
HTML-unescaped regex captures, normalized paths and the provider item fields.
Regex quoting matches Go's metacharacter set, including Unicode inputs. Error
excerpts and detail prefixes avoid splitting UTF-8 code points.

Project resolution caches positive results only. An unknown index returns
`ProviderNotIndexedException` with the resolver hint; linked-worktree indexing
requires explicit opt-in. Refresh renders typed `full`/`changed` inputs, honors
skip patterns and reports bounded details. Health probes and smoke tests do not
create a real provider index.

## Validation and reproduction

The [validation receipt](inventory/downstream-mcp-validation.json) records source
and lock hashes, test counts and build artifacts. The independent test server
has no product references. It exercises Unicode/safe argv, environment/cwd,
handshake failure, stderr flooding, pagination, tool/RPC errors, oversized frames,
parent cancellation, 800 ms calls under a longer parent deadline, concurrent
startup/close, disconnect/restart, uncooperative shutdown, late launch and
grandchildren exiting before or after the direct child.

Nine differential scenarios run identical mappings and arguments through the
pinned Go generic adapter and C#. They cover symbols, wildcard predicates,
regex, all/first chains, overview, files, architecture, misses and mapping errors.
The real configured `codebase-memory-mcp` 0.10.0 completed handshake and discovered
14 tools with isolated home/config/database directories. Its unindexed degraded
health was expected; the smoke test made no indexing request.

Build the optional Go bridge from the pinned source and downloaded toolchain:

```powershell
python test/Harness.Compatibility/Providers/build_oracle.py --source artifacts/compatibility/oracle/source --go artifacts/compatibility/toolchain/go/bin/go.exe --modules artifacts/compatibility/oracle/modules --output artifacts/provider-oracle/provider-oracle.exe
$env:HARNESS_GO_PROVIDER_ORACLE="$PWD/artifacts/provider-oracle/provider-oracle.exe"
$env:HARNESS_REAL_CODEBASE_MEMORY='path/to/codebase-memory-mcp.exe'
dotnet test --solution Harness.slnx --no-restore -bl:artifacts/downstream-tests.binlog --report-trx --results-directory artifacts/downstream-results
```

The existing storage and memory oracle variables must also be set to execute
their optional differential suites. Tests skip optional external executables
when not configured. Linux validation runs the portable integration assembly
with the installed .NET 10 runtime; the Windows real-provider executable and Go
bridge are excluded there. Release validation includes the self-contained,
single-file Windows x64 CLI publish with the publish profile and locked restores.
