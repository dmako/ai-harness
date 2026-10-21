# UI writes and live updates (PORT-028)

TASK-0028 completes the embedded UI's author operations. The listener still
defaults to `127.0.0.1:17477`; explicit `--addr` overrides remain available.
No package, database schema, Node build, or product dependency is added.

## Writes and review

`UiRoutes.Writes` registers the frozen Go HTTP write paths: project and wider
memory corrections/retirement, promotion previews and confirmation, memory
proposal approval/rejection, task creation and proposal review, finding marks
and tasks, architecture proposal review, project/workspace editing, and
graphify session controls. Existing memory, task, finding, workspace, registry,
and session services perform the operations. Human identity comes from the
server's established `HumanActor`; callers cannot supply audit identity.

The Web assembly retains its Core-only project dependency. Core defines the
write and change-source contracts, and CLI adapters connect those contracts to
infrastructure. HTTP handlers enforce the boundary before opening stores:

- POST with `Content-Type: application/json` and `X-Harness-UI: 1`.
- One Origin exactly matching the listener's HTTP Host, including its port.
- Loopback Host validation before any API or asset response.
- A bounded 64 KiB JSON object, recognized fields, and typed values.

Failures retain the JSON `error` envelope. Compared with Go's single decode
from a limited reader, the C# boundary deliberately rejects trailing JSON,
invalid UTF-8, non-object bodies, and repeated field names. These checks
prevent ambiguous bodies from reaching a store; regression tests cover them.

The original confirmation, correction, conflict-choice, and review forms are
preserved. A conflicting memory proposal stays pending until the author
chooses replacement or keeping both. Opening task review does not create a
task. Promotion preview gathers evidence without creating wider storage.
Workspace finding decisions live in workspace storage; resulting tasks live
in the selected member's project database.

## Live updates

`/api/events` serves SSE with a three-second retry hint, twenty-second
heartbeats, ten-second write deadlines, and at most sixteen streams. Each
subscriber has a single wake slot and coalesced project/kind changes, so a slow
page does not accumulate an event queue. One background watcher serves all
pages; the last disconnect cancels and disposes it. Reconnection establishes
a fresh baseline. Host shutdown cancels the watcher.

The watcher samples every 1.5 seconds using a read-only SQLite connection per
registered project. Unchanged database ticks query only `PRAGMA data_version`.
After a change it compares cached table fingerprints for tasks, knowledge,
proposals, findings, activity, and sync. Append-only activity uses its rowid
watermark. Registry and workspace files use modification-time/size stamps;
unreadable databases retry after ten seconds. No provider is started and no
database is initialized or migrated by watching. Like the frozen Go watcher,
the database watch set consists of registered project stores.

Events contain project IDs and changed kinds rather than memory or task
contents. Changes invalidate the overview cache. Session-list invalidation
uses its separate gate and does not make project reads wait for graphify.

The only frontend change adds fifteen-second fallback polling when SSE is
unavailable, including browsers without EventSource. Polling stops when the
stream opens and skips hidden pages. It uses the existing live-refresh queue,
which defers refresh while a correction or review form is open. Existing
selective section loading, reconnect, manual reload, and error display remain.
Asset comparison removes only this small polling addition before checking the
frozen JavaScript; all other embedded frontend bytes remain identical.

## Validation

`test/Harness.Integration/Web` uses actual CLI/Kestrel processes and independent
SQLite writers. Coverage includes rejected Host/Origin/header/body requests,
memory and task reviews with human audit, promotion preview/commit, project
and workspace edits, project/workspace finding triage, graphify controls,
stream limits, cross-process changes, disconnect, watcher disposal, and
reconnect. Differential writes replay against separate isolated databases
using the frozen Go executable; only generated identities and timestamps are
normalized.

Set `HARNESS_GO_UI_ORACLE` to the frozen full Go executable. Set
`HARNESS_UI_BROWSER` to an installed Chrome or Edge executable to run the
headless browser test. The browser test drives the actual DOM confirmation
forms and verifies storage outcomes, live refresh, blocked-SSE fallback
polling, and return to live status. Browser automation uses the browser's
loopback debugging endpoint and adds no product dependency.

```powershell
$env:HARNESS_GO_UI_ORACLE = "$PWD/artifacts/compatibility/oracle/harness.exe"
$env:HARNESS_UI_BROWSER = 'C:/Program Files/Google/Chrome/Application/chrome.exe'
dotnet test --project test/Harness.Integration/Harness.Integration.csproj --no-restore `
  --filter-namespace Harness.Integration.Web -bl:{{}}
```

The earlier [PORT-027 receipt](inventory/ui-validation.json) records the read
port at its original commit. Its source hashes are historical after this
task's write and polling additions.

The [PORT-028 receipt](inventory/ui-writes-validation.json) records the full
Windows run, its Web subset, Linux Web checks, browser workflow results,
self-contained offline smoke tests, and normalized source hashes. Repeat the
published write smoke and verify the receipt with:

```powershell
python test/Harness.Compatibility/Web/publish_writes_smoke.py `
  --executable artifacts/ui-writes-publish/harness.exe `
  --output artifacts/ui-writes-publish-smoke.json
python test/Harness.Compatibility/Web/record_write_validation.py --check
```
