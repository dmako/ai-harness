# Local observability (PORT-024)

TASK-0024 ports local telemetry, reports and activity reads from frozen Go commit
`9cb401dd2de4f85dd089fea86a0b139d81b178c6`. The implementation uses the existing
SQLite schema and BCL `System.Threading.Channels`; CPM dependencies are unchanged.

## Recording and responsiveness

`EventBus<T>` gives each subscriber an independent bounded queue (256 events by
default). Publishing uses `TryWrite` and drops the incoming event when that
subscriber is full. Consumers run outside the MCP call, contain writer failures,
and expose dropped-event and failure counts. Closing completes the queues and
drains accepted events. Provider version discovery is a separate subscriber;
empty observations are retried and the first nonempty version is persisted.

`TelemetryRecorder` opens existing state without initializing or migrating it.
Database contention is bounded to two seconds per write. Tool and provider
observers cannot fail the public tool response. The recorder redacts credential
patterns before persistence and stores bounded metadata rather than raw tool
arguments or returned source. Context provenance contains paths, names, ranks,
positions and provider/capability attribution for the units actually returned.

Retention follows Go's insertion checkpoints: approximately 20,000 tool calls,
10,000 provider calls, 2,000 context calls and 50,000 client events; checkpoint
intervals allow temporary excess between pruning passes. Context unit rows are
removed with their parent call. This is local recording with no network exporter.
OpenTelemetry and Serilog can be added through observers or independent bus
subscribers; neither is required for the port.

## Reads and outcomes

The CLI supports `metrics`, `context stats` and `report`, with `--days` and
`--json`; `report --all` reads registered projects. These views open existing
state read-only and do not start providers. Reports combine metrics, context
outcomes, knowledge usage and provider trends. Counts, integer byte averages,
nearest-rank p50/p95, UTC daily histograms, warning thresholds, action text and
recovery after clean calls are compared with the original implementation.

Context scoring runs best-effort after synchronization and task completion.
Git changes are matched to previously returned units, including missed callees;
old or lost-history calls are closed without inventing outcomes. Repeated
resolution is idempotent. Read-after evidence uses linked client sessions,
strictly later reads/edits and the original two-hour/next-context boundary.
The context hit rate remains a mean of per-answer rates, rather than a ratio of
all returned units.

The activity reader groups tool calls into sessions with a 30-minute idle gap,
and merges task, memory and compacted synchronization events. It retains Go's
titles, levels, timestamp precision and optional JSON fields. HTTP adapters and
UI sections remain in their planned tasks; client installation and hook event
ingestion remain PORT-025/026.

## Compatibility and validation

The thin driver in `test/Harness.Compatibility/Telemetry` calls the pinned Go
packages directly for metrics, context statistics, trends, report findings,
complete reports, bypass mappings, activity and outcome resolution. Synthetic
fixtures exercise redaction, retention, independent slow/failing subscribers,
shutdown flush, missing/older schemas, provider versions, Git changes and public
MCP/CLI behavior. The published smoke script checks all three CLI readers after
the MCP process flushes on disconnect.

Deliberate robustness differences are limited to valid Unicode boundaries when
clipping UTF-8, normalizing/redacting linked task text consistently, conditional
outcome updates under concurrent resolution, and not initializing databases
from background telemetry or read-only commands. The previous C# provider
recorder now follows Go's drop-incoming and complete-drain behavior.

Windows and WSL Linux receipts, full regression results, source hashes and
reproduction commands are recorded in
[`inventory/telemetry-validation.json`](inventory/telemetry-validation.json).
macOS was not exercised. An existing Windows Graphify test fixture was adjusted
to read shared append-only logs and ignore an unfinished final record after the
full suite exposed a sharing race.
