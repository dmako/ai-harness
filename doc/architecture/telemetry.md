# Telemetry and developer tools

Local recording is implemented in Core and Infrastructure, with CLI reports and
MCP diagnostics. The optional [Collect](../../src/Harness.Collect) and
[Share](../../src/Harness.Share) executables reference Infrastructure and Web.
They are packaged separately and are not installed by the main CLI.

## Local observations

Bounded channels decouple provider calls, context outcomes and tool events from
SQLite persistence. Slow subscribers have isolated queues; overflow behavior is
explicit. Shutdown flushes accepted events. Recorder failures are contained and
do not create missing project storage. Retention deletes old source rows and
associated context evidence at defined boundaries.

Context observations connect returned units with later reads and changes to
report hits, misses, touched files and outcomes. Sync, expiry, lost history and
task completion resolve observations through the same service boundary. Reports
include metrics, activity, trends and bypass information without changing client
configuration. Provider versions are recorded after successful discovery.

## Opt-in export

Developer export is off until explicitly enabled. Preview displays the sanitized
report before sending it. Export uses an authenticated configured endpoint,
redacts credentials and sensitive identifying data, and limits automatic sends.
Disabling stops future export; local diagnostics remain available independently.

`harness-collect` accepts bounded authenticated reports at `/v1/report` and
persists validated data in its selected directory. `harness-share` serves an
allowlist of release archives, checksums and notes, including archive HEAD
requests, and rejects writes and unlisted paths. Both default to local access;
LAN exposure requires explicit selection. Their ASP.NET routes use generated
metadata and request delegates for Native AOT.
