# Embedded UI read APIs (PORT-027)

TASK-0027 serves the frontend from the frozen Go commit
`9cb401dd2de4f85dd089fea86a0b139d81b178c6` through ASP.NET Core Minimal APIs.
HTML, JavaScript, CSS, branding and the guide/reference documentation are
embedded resources. There is no Node build or external asset dependency.

## Running

```text
harness ui
harness ui --no-open
harness ui --addr 127.0.0.1:0 --no-open
```

The default listener is **127.0.0.1:17477**, as requested during this task;
this intentionally replaces Go's 7477 default. The command prints the actual
bound URL, including an ephemeral port when `--addr` uses zero. Explicit IPv4
and IPv6 loopback addresses and localhost are accepted. The browser opens after
binding unless `--no-open` is supplied. Browser-launch failure leaves the
listener running. Ctrl-C/SIGTERM initiate bounded graceful shutdown.

## Read surfaces

`UiRoutes.Reads` lists forty project, workspace and machine API registrations.
The documentation index and section routes are registered with the assets.
They cover projects and summaries, memories and their wider levels, proposals,
tasks/milestones/audit events, activity, search, metrics, context quality,
reports, provider coverage, hook status, stacks, dependency/adoption evidence,
workspace design issues, duplicates, communication diagrams, findings and
bounded source snippets. GET and HEAD use the same route handlers.

The Web assembly depends only on Core and the ASP.NET Core shared framework.
The Core read interface connects thin HTTP handlers to application adapters in
the CLI composition root. Those adapters reuse the existing project lifecycle,
memory/task/finding/telemetry stores, evidence readers and provider registry.
No package or schema migration is introduced. The integration-test lock file
adds only its existing Web project reference.

Project lists share a two-second cache keyed by registry/workspace files and
database/WAL stamps. An uncached single-project request inspects only its
project. Evidence readers cache repositories, manifests and file bodies within
the request; the structure view passes its prepared architecture to finding
assembly. Session lists reuse the existing five-second graphify cache with a
separate gate, so a slow session command does not block project lists.

Ordinary page reads do not start providers. Explicit architecture, coupling and
health-probe requests require `X-Harness-UI: 1`. Provider work and session lists
run only when their independent frontend section requests them. Architecture
and coupling have sixty-second budgets; session lists have twenty seconds.
Existing database reads do not initialize or migrate state. Task list reads
can recover expired claims using an existing read/write connection, matching
the task store's established semantics.

## HTTP boundaries

The listener rejects non-loopback addresses. A Host check runs before assets
or API content and refuses non-loopback names, including DNS-rebinding-style
Host values. Accepted responses carry `nosniff`, no-store (except cached icons)
and `default-src 'self'; frame-ancestors 'none'`. Assets are a literal embedded
allowlist; no physical directory is served. Invalid encoded/traversal paths
are rejected. Snippets use the existing bounded repository reader, which
rejects escapes and symlinks outside the repository. Machine metadata excludes
the telemetry token.

## Validation and scope

The HTTP integration suite in `test/Harness.Integration/Web` runs real Kestrel
listeners and CLI processes. `HARNESS_GO_UI_ORACLE` points to the frozen full Go
executable; the differential test compares thirty-eight read responses and
seven byte-identical assets against it, plus every documentation section. It also checks common health status
fields. Request timestamps are normalized; runtime-specific health diagnostics
come from the C# inspection services. The shipped reference explicitly states
the new default port. The original frontend assets remain byte-identical.

Tests exercise actual default-port binding, explicit port zero, Host/CSP/path
guards, schema preservation, metadata privacy, independent concurrent reads,
request cache reuse and an actual provider process that starts only on an
explicit trusted probe. Windows and Linux run the same tests.

TASK-0028 owns mutation routes and SSE/live updates. The frozen frontend is
shipped intact, so its mutation controls and live stream become functional
with that task. No writes or stream implementation are claimed here.
Interactive browser verification was unavailable in this environment; HTTP
checks and frozen asset comparisons cover local delivery.

The checked receipt is [ui-validation.json](inventory/ui-validation.json).
To repeat the focused checks and published smoke test:

```powershell
$env:HARNESS_GO_UI_ORACLE = "$PWD/artifacts/compatibility/oracle/harness.exe"
dotnet test --project test/Harness.Integration/Harness.Integration.csproj --no-restore `
  --filter-namespace Harness.Integration.Web -bl:{{}}
python test/Harness.Compatibility/Web/publish_smoke.py `
  --executable artifacts/ui-publish/harness.exe --output artifacts/ui-publish-smoke.json
python test/Harness.Compatibility/Web/record_validation.py --check
```
