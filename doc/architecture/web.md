# Embedded web UI

Source: [`src/Harness.Web`](../../src/Harness.Web).
Web depends only on Core. It hosts embedded HTML, JavaScript, CSS and Markdown
without a Node build or runtime asset directory. `UiAssets` indexes documentation
sections and serves assets with the appropriate cache behavior.

ASP.NET Core slim hosting and generated request delegates support Native AOT.
HTTP handlers call Core read/write/change contracts supplied by the CLI.
Reads, author operations, graph/session controls and live notifications remain
separate boundaries. Each section loads independently so a slow optional backend
does not delay all other sections.

## Author operations

Writes use POST, `Content-Type: application/json`, `X-Harness-UI: 1` and a single
Origin matching the listener Host and port. Loopback Host validation applies to
assets and APIs. The boundary accepts a bounded 64 KiB JSON object, checks known
fields and types, and rejects duplicate fields, invalid UTF-8 and trailing data
before opening stores. Errors retain a JSON `error` envelope.

The server establishes human audit identity. Memory conflicts require explicit
replacement/keep-both choices. Reviewing a task does not create it; promotion
previews do not create wider stores. Finding/task/knowledge proposals stay
pending until an author approves or rejects them.

Machine › SCM connections (`GET /api/scm`; `POST /api/scm`,
`/api/scm/{name}/edit`, `/api/scm/{name}/remove` and `/api/scm/test`) is served by
the CLI's `UiReadService` over Harness.Scm's configuration store and providers.
Entries carry the token only masked; a test of the form's unsaved values may use
a saved entry's token by name, so an edit is tested without typing it again.
Entries list their aliases; add and edit take `aliases` (an array of strings, all of
them, validated by the store as for `harness scm alias`).

A project's SCM card on Agents › Tools & providers (`GET
/api/projects/{id}/scm?days=N`; `POST /api/projects/{id}/scm/choice` and
`/api/projects/{id}/scm/test`) shows which connection the project uses and how it
was chosen, lets the author choose among the connections serving its remote (kept
in scm.toml on this machine), tests it with doctor's project check, and counts
the `scm_` MCP tool calls in the project's database. It loads after the page,
independently of the provider cards, and never carries a token (see
[SCM integration](scm.md#harness-ui)).

## Live updates

Server-sent events publish invalidations rather than shipping unrestricted state.
The browser refreshes relevant sections and has polling fallback. Independent
SQLite writers, branch transitions and provider/session events invalidate their
views. Refreshing preserves active forms, confirmations and review selections.
Cancellation and connection limits stop abandoned streams and owned processes.

Integration tests exercise the HTTP boundary, live lifecycle and author writes.
Browser tests can use an explicitly configured installed Chrome executable with
an isolated profile; see [testing](../testing.md).

## Typed contracts for the Avalonia UI

The server builds its answers as JSON maps (the CLI's `UiReadService`), so the
Avalonia UI ([`src/Harness.Ui/Api`](../../src/Harness.Ui/Api), BP-010) holds the
typed contracts. Each area has its own file: `Projects`, `Workspaces`, `Tasks`,
`Live` and `Docs` so far. Each area's client methods are in a matching
`HarnessApi.<Area>.cs`. `UiJsonContext` generates their JSON code: no reflection,
and the contracts are strict:

- a member marked `required` must be in the answer;
- a non-nullable member may not be null (`RespectNullableAnnotations`).

So a renamed or missing field fails to deserialise, and the page shows an error
instead of an empty view.

Contract tests pin the contracts, rather than moving the server to typed answers.
That keeps this change out of the CLI's read service and the MCP tools that share
its views:

- `test/Harness.Integration/Web/UiContractTests.cs` compiles the contracts in
  (no Avalonia needed) and drives `HarnessApi` against a real harness ui on a
  seeded `SyncFixture`. It reads every endpoint the contracts cover, round-trips
  the writes, waits for a change on `/api/events`, and checks that a refusal
  carries the server's message.
- `UiFixtureShapeTests` checks that the headless tests' fixture files keep the
  server's shapes.
- `ContractTests` (in `Harness.Ui.Tests`) proves the strictness on the fixtures.

A view task that reads or writes a new endpoint:

1. adds its contract to its area's file and to `UiJsonContext`;
2. marks the members the server always sends as `required`;
3. adds the client method to `HarnessApi.<Area>.cs`;
4. extends `UiContractTests` with a read, or a write and the read that shows it,
   against the seeded project;
5. adds a fixture file at the route's path for its headless tests.

The first run of these tests found that `live.stale` is a reason string, not a
flag.
