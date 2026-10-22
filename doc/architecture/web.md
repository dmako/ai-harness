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

## Live updates

Server-sent events publish invalidations rather than shipping unrestricted state.
The browser refreshes relevant sections and has polling fallback. Independent
SQLite writers, branch transitions and provider/session events invalidate their
views. Refreshing preserves active forms, confirmations and review selections.
Cancellation and connection limits stop abandoned streams and owned processes.

Integration tests exercise the HTTP boundary, live lifecycle and author writes.
Browser tests can use an explicitly configured installed Chrome executable with
an isolated profile; see [testing](../testing.md).
