# Core

Source: [`src/Harness.Core`](../../src/Harness.Core).
The assembly has no project dependencies and owns models and contracts shared
by all adapters.

## Knowledge and tasks

Memory models distinguish goals, constraints, decisions, conventions, warnings
and facts, their scopes, provenance, validity and supersession. Validation and
credential redaction happen before persistence. Scope matching, similarity and
content hashes use stable Unicode case and word-character tables; nanosecond
RFC3339 timestamps retain their precision. Table attribution is embedded from
`Memory/LICENSE.txt`.

Task definitions include acceptance criteria, dependencies, scope and milestones.
Execution models distinguish claims, progress, review, blocking, deferral and
completion. Proposal models keep agent suggestions pending until a human review.
Services that need durable state are defined as interfaces here and implemented
in Infrastructure.

## Retrieval and analysis

Retrieval separates task parsing, evidence gathering, candidate merging, ranking
and packing. Evidence carries provider and source provenance. Packing accounts
for UTF-8 bytes, clips bodies or substitutes references, and avoids repeating
knowledge already present in a definition. Response rendering also checks its
final serialized size, including diagnostic fields.

Architecture contracts describe projects, dependencies, findings, boundary rules
and change partners. Findings distinguish facts from heuristics and unknown
coverage. Boundary rules are derived from project knowledge; applying a rule
does not create a task or mark a finding automatically.

## Shared infrastructure contracts

Provider contracts describe capabilities, requests, results, errors and lifecycle.
Web contracts define read/write/change sources without referencing ASP.NET.
Telemetry models and bounded channels support asynchronous recording and report
construction. Agent guidance defines the generated instruction block shared by
client setup commands.

`Serialization/HarnessJson.cs` resolves assembly-local generated JSON metadata.
Wire objects use explicit `JsonObjectMap` payloads; ordinary dictionary keys stay
data. Unknown model types fail instead of falling back to reflection. Product
assemblies register their own `ProjectJsonContext` metadata.
