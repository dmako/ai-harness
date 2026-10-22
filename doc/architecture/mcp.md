# MCP server and retrieval

Source: [`src/Harness.Mcp`](../../src/Harness.Mcp).
MCP depends only on Core. It uses the MCP SDK for protocol models and adapts
Core service contracts to public tools.

Thirty embedded schemas under `Schemas` define tool descriptions and input and
output shapes. Tools cover project context and search, packages and providers,
knowledge reads/writes/proposals, task execution/proposals, architecture evidence
and findings, and diagnostics. Integration tests check advertised schemas against
these shipped files rather than an external executable.

The stdio transport negotiates MCP, validates JSON-RPC envelopes and supports
line-delimited messages and compatibility framing. Frames and request concurrency
are bounded. Disconnects cancel active work, drain responses and close owned
provider sessions. Diagnostics go to stderr rather than corrupting stdout.

Argument adapters validate recognized fields, recursive objects, array members,
nullable shapes and supported schema unions before calling services. The schema
validator covers the facets actually advertised; extend it before publishing
schemas that require additional facets. Responses expose structured content and
an equivalent JSON text representation with canonical key ordering.

`get_context` combines local knowledge, provider evidence and recent changes.
Task parsing produces search terms and named symbols. Gathering is bounded;
merging avoids duplicate candidates; ranking records its reasons. Packing favors
useful definitions and can return references when bodies do not fit. Final
rendering enforces the serialized UTF-8 budget, including debug output. Each
returned unit retains file, line, provider, evidence and scope provenance.

Diagnostics and CLI build metadata identify the .NET runtime using the `runtime`
field. Human-only approval operations remain outside agent tool shortcuts;
suggested work and knowledge are represented as pending proposals.
