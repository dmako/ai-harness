# Graphify adapter

Source: [`src/Harness.Graphify`](../../src/Harness.Graphify),
[`Providers/Graphify`](../../src/Harness.Providers/Graphify) and
[`Infrastructure/Graphify`](../../src/Harness.Infrastructure/Graphify).

`harness-graphify` is a standalone MCP-facing adapter for the external compiler
backend. It references Core, Infrastructure and Providers; it ships in the main
archive but does not bundle the backend or start it for every light request.

Discovery finds repository inputs, resolves duplicate solution formats and
accepts explicit backend arguments. Sessions expose symbol search, callers,
references and impact with qualified locations and evidence. Startup reports a
bounded loading answer so other providers can continue answering.

Session management observes backend descriptors and records ownership, activity
and load progress with locked atomic file updates. It reuses reachable external
sessions without taking ownership of them. Stop-on-exit affects sessions the
adapter started; borrowed sessions remain running. Cleanup and idle sweeping
ignore unrelated processes and retain enough diagnostics to explain failures.

Local fake-worker tests cover loading, disposal, ownership, restart, failed
startup and stale descriptors. An optional real-backend check is enabled only
when its executable is configured explicitly.
