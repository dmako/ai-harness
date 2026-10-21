# Graphify adapter and warm sessions

TASK-0015 / PORT-015 adds the optional `harness-graphify` companion executable.
It speaks the PORT-014 provider protocol and invokes the external
`graphify-csharp` CLI. Roslyn and the compiler workspace remain in graphify;
the harness adds no compiler packages or CPM dependencies. The companion
references Core, Infrastructure and Providers without changing the six main
assemblies' dependency policy.

## Discovery and queries

The executable is discovered on PATH, then in `~/.dotnet/tools`, including
the Windows `.exe` suffix. `--graphify` overrides it. `--root`, `--clean`,
`--stop-on-exit`, `--idle` (default two hours), and arguments following `--`
follow the pinned companion's interface. Duration components support Go's
units and signs; sub-tick durations round toward zero to .NET's 100 ns ticks.

Input discovery accepts root-level solutions/projects or searches two directory
levels, excluding build, package and hidden directories and directory links.
Solutions take precedence over projects; sibling `.slnx` wins over `.sln`.
Ambiguous inputs require explicit watch arguments such as
`-- --input src/App/App.slnx`. Configured graphify routing recognizes these
nested inputs as C# projects even when the shallow language detector does not.

Symbol search, callers, references and bounded transitive impact preserve the
pinned query verbs, provenance, path filtering, byte accounting and truncation.
Impact traverses four levels and deduplicates symbols. Interface/type impact
uses usages; methods use callers. A vanished session is resolved and retried
once. Query commands have a 30-second deadline, bounded stdout/stderr, and
caller cancellation. The Go adapter's optional context-deadline estimate for
whether another impact expansion fits is not reproduced: CancellationToken
does not expose a deadline; callers still bound the complete provider call.

## Ownership and lifecycle

The adapter reuses a reachable session for the canonical root, preferring ready
sessions. A new watch loads in the background. Queries wait at most three
seconds by default, then return a loading stage/retry hint. Passive health and
capabilities never start a watch. The context=false graphify pack is excluded
from light registry construction, before any process can start.

Normal adapter shutdown keeps its watch warm. `--stop-on-exit` stops only the
watch that adapter launched, identified by PID, even if queries selected another
tool's ready session while its own watch was loading. Borrowing a session never
claims its ownership. Idle sweeping stops only sessions with harness ownership
and a known last-query timestamp. Manual/external sessions remain intact.

The Go-compatible `graphify-sessions.json` records ownership, pending startup,
last use, discovery diagnostics and previous load time. Queries touch at most
once per minute; recent session listing shares a five-second cache. Per-root
startup locks prevent cooperating adapters starting duplicate watches. Record
updates use a cross-process exclusive file lease and atomic replacement;
changed records from a noncooperating writer are preserved when detected.

## Windows corrections and limits

The frozen Go Windows file lock is a no-op and its process-alive fallback always
returns true. This port uses real exclusive file leases and process liveness
checks. Cleanup uses Windows LocalAppData, matching the installed graphify's actual
descriptor location rather than the pinned adapter's RoamingAppData default.
It verifies root, safe session ID, dead PID, descriptor identity and unchanged
contents immediately before deletion. Busy,
changed, live and unknown-PID descriptors are preserved. An external writer
that replaces a descriptor between the final check and deletion is outside
this cooperating-lock protocol; the check/delete pair is not atomic.

Ordinary MCP processes retain kill-on-close jobs. Only the configured graphify
pack permits explicit child breakaway. Its watch redirects stdin/stdout to NUL
and stderr to a file, and starts outside the adapter's Windows job. A containing
job that forbids breakaway produces an explicit launch error. Unix watches use
an independent process group and file descriptors, with a reaper while the
adapter lives; this is not a new login session or a guarantee against host-wide
signals or shutdown. macOS native paths have not been executed in this task.

## Validation

The independent fake CLI covers discovery, query mapping, ownership, loading,
startup failure, last-use/cache throttling, idle sweeping, descriptor identity,
concurrent adapters and reuse across adapter shutdown. Nine output scenarios
compare directly with the adapter built from frozen Go commit
`9cb401dd2de4f85dd089fea86a0b139d81b178c6`.

An isolated package-free .NET project was also indexed by installed
graphify-csharp **0.2.1+18c3f8ec89381464a83cd8649420477a0ebf9112**. It returned
the interface caller, two references and a second-level transitive caller;
the test stopped only its own compiler session afterward. No personal source
repository or knowledge database was copied into the fixture.

Build the optional oracle with:

```powershell
python test/Harness.Compatibility/Graphify/build_oracle.py --source artifacts/compatibility/oracle/source --go artifacts/compatibility/toolchain/go/bin/go.exe --modules artifacts/compatibility/oracle/modules --output artifacts/graphify-oracle/graphify-oracle.exe
$env:HARNESS_GO_GRAPHIFY_ORACLE="$PWD/artifacts/graphify-oracle/graphify-oracle.exe"
$env:HARNESS_REAL_GRAPHIFY="C:/Users/david/.dotnet/tools/graphify-csharp.exe"
```

See [the validation receipt](inventory/graphify-validation.json) for commands,
platform coverage, test counts and source/artifact hashes.
