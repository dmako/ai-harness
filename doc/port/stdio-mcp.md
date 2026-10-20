# Stdio MCP bootstrap

TASK-0007 / PORT-007 adds `harness mcp --project <directory>` through the official
`ModelContextProtocol.Core` 2.2.0 SDK. It advertises exactly four implemented tools:
`ping`, `diagnostics`, `state_put_test` and `state_get_test`. The remaining tools
retain their own port tasks; this bootstrap does not claim full harness parity.

```powershell
dotnet src/Harness.Cli/bin/Debug/net10.0/harness.dll mcp --project C:/path/to/repository
```

The directory defaults to the current directory and resolves to its Git root.
Non-Git directories are supported. Configuration is validated before the protocol
starts; invalid existing config exits 1 with a diagnostic only on stderr. Stdout
carries newline-delimited JSON-RPC exclusively. The existing CLI logging provider
routes SDK/application logs to stderr. EOF, Ctrl+C and Unix SIGTERM stop the
session; the latter two are cooperative normal shutdown for the MCP command.

## Boundaries and ownership

Core defines `IHarnessStateSession` and runtime metadata without SDK or SQLite
types. Infrastructure's `HarnessStateSession` implements it with a reusable
`HarnessDatabase` factory and serialized first-open/registration. Mcp references
Core and the SDK, never Infrastructure. Cli constructs the session and supplies
the existing host service provider and logger factory to `McpServer.Create`.
There is one application container. Direct SDK handlers and explicit schemas
avoid framework inference changing the frozen Go contracts; the high-level
`ModelContextProtocol` hosting package is not needed for this implementation.

`HarnessMcpServer.RunAsync` takes ownership of the session. It stops SDK request
processing before disposing session resources. The session waits for best-effort
machine registry bookkeeping, clears owned SQLite pools and releases its gate.
The caller owns the input/output streams; Cli closes its standard-stream handles
after the runner returns. No downstream provider sessions are started yet;
provider discovery/startup and their lifetime implementations remain PORT-012–015.

## Tool behavior

The four input/output schemas and descriptions under `src/Harness.Mcp/Schemas`
come from the pinned Go `tools/list` fixture and carry its MIT license. Names and
properties stay snake_case. Input validation rejects unknown keys, missing
required fields and wrong types before accessing storage, with the expected-
argument hint. The current validator deliberately implements these four tools'
small string-only input contracts; future tools must add their actual schema
validation rather than assume it is a general JSON Schema engine.

Success contains matching structuredContent and a JSON text content block.
Validation/storage failures contain text and `isError: true`, without structured
content. Unknown tools are JSON-RPC `-32602` errors. `tools/list` preserves the
Go cache envelope (`ttlMs: 0`, `cacheScope: public`) and four exact schemas.
The initialize handshake uses protocol `2025-06-18`, with Go-compatible logging
and tools capabilities. Other protocol revisions need separate conformance work;
the SDK's newer protocol features are not an automatic parity claim.

Ping never opens storage or runs Git. Diagnostics re-reads HEAD and reports
storage errors inside its successful result. It does not create a missing DB,
but an existing DB goes through the normal migrating/open/register path, matching
Go. This differs from CLI status/doctor, which remain read-only inspections.
Diagnostics reads existing sync state and the latest five runs without mutating
them; sync operations remain PORT-016. Its legacy `go_version` field describes
the actual .NET runtime. Low-level storage-error wording follows the platform's
.NET/SQLite implementation; the error envelope and contextual prefixes remain
stable and are tested separately from platform-specific error prose.

State test tools lazily create/open the database even for a missing-key read.
Git projects retain their project/worktree IDs and linked-worktree shared state.
Non-Git directories store state locally without inventing Git identities. The
first successful Git registration schedules a best-effort `last_mcp` registry
update off the request path. Repeated state calls reuse the factory without
reopening/migrating/registering on every call. Empty keys fail; keys allow 256
Unicode scalar values and values allow 65,536 UTF-8 bytes. A missing key returns
`found: false`; a stored empty value returns `found: true` with value omitted.

Git subprocess stdin is redirected and closed. Inheriting MCP stdin on Windows
caused Git HEAD reads to stall once the server was reading the protocol pipe.
The real Git-backed stdio comparison protects this integration correction.

## Framing, cancellation and disconnect

`CompatibleStdioTransport` implements the official SDK's transport interface with
a bounded 64-message queue and serialized whole-frame writes. Inbound lines are
bounded at 16 MiB, including the terminator, matching the pinned Go transport.
Oversized lines are consumed without growing the buffer beyond the bound.
Malformed JSON gets `-32700`; structurally invalid JSON-RPC gets `-32600` and the
session continues. Supported pre-handshake batches are screened as a whole;
well-formed batches after negotiating 2025-06-18 terminate as in Go. Full legacy
batch/protocol-version interoperability remains outside this bootstrap's claim.

Stdin EOF drains accepted requests until replies finish, bounded at 30 seconds.
SDK cancellation notifications reach the handler token. The C# SDK suppresses
replies for client-cancelled calls, so the adapter releases their EOF tracking
only after the handler finishes cancelling; it does not wait for a nonexistent
reply. Other replies continue to drain normally. This behavior has a real SDK
client/server cancellation test, including a subsequent ping and disconnect.
Synchronous SQLite work checks cancellation around provider operations and stays
bounded by the configured native busy timeout; it cannot interrupt native work
immediately. Force-killing a process skips graceful cleanup, but committed test
state survives restart and is checked by an independent process.

## Validation

Run the .NET solution tests using the [build instructions](bootstrap.md). The
integration suite uses the official SDK stdio client against independent CLI
processes: ping and diagnostics leave missing state absent, a value is written,
the client disconnects, an exclusive database handle opens, and a new process
reads the value. Unit tests also cover rune/byte boundaries, error envelopes,
caller stream ownership, malformed/oversized frames, cancellation and EOF drain.

The isolated raw-client/Go differential suite is:

```powershell
python test/Harness.Compatibility/Mcp/stdio.py --oracle artifacts/compatibility/oracle/harness.exe --candidate src/Harness.Cli/bin/Debug/net10.0/harness.dll --output artifacts/mcp-differential.json
```

It checks all four exact schemas, capabilities, cache metadata, tool/errors,
malformed-input recovery, Go-created sync diagnostics, Unicode values, forced
termination/restart, EOF responses, machine events, bad startup config,
unavailable storage and non-Git operation. All paths, homes and state are isolated;
no personal project database or registry is used. Stdout is parsed line by line.
Native Linux/macOS execution and actual Claude Code/Codex UI integration remain
release/dogfood checks. Detailed evidence is in
[mcp-validation.json](inventory/mcp-validation.json); local TRX/binlogs and binaries
remain ignored under `artifacts`.

The final solution run passed 80 tests with one Unix-permissions test skipped on
Windows. Strict Release/CI build and locked restore passed. All 43 raw-client/Go
checks passed against both the managed build and the Windows self-contained
single-file executable, including native SQLite persistence.

SDK references: [transport guidance](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html),
[2.2.0 session cancellation implementation](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.2.0/src/ModelContextProtocol.Core/McpSessionHandler.cs).
