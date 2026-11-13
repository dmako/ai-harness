# MCP server and retrieval

Source: [`src/Harness.Mcp`](../../src/Harness.Mcp).
MCP depends only on Core. It uses the MCP SDK for protocol models and adapts
Core service contracts to public tools.

Forty-two embedded schemas under `Schemas` define tool descriptions and input and
output shapes. Tools cover project context and search, packages and providers,
knowledge reads/writes/proposals, task execution/proposals, architecture evidence
and findings, merge requests on the project's SCM, and diagnostics. Integration tests check advertised schemas against
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

## SCM tools

`Scm/ScmTools` serves the eleven `scm_` tools over the session's `IScmSession`
(Harness.Scm: the project's SCM resolution, the machine's SCM connections and
the providers the CLI composes). Each acts on the current project's SCM and
project as its git remotes (or `[scm]`) name them; a merge request is its
project-scoped number (GitLab iid, Forgejo index, GitHub pull request number).

| Tool | Does |
| --- | --- |
| `scm_get_projects` | projects the token's user can access (`search`, `visibility`; `scm` names an SCM before the project matches one) |
| `scm_list_merge_requests` | the project's merge requests by `state` (opened, merged, closed) |
| `scm_get_merge_request_details` | one merge request in full: description, branches, SHAs, approvals |
| `scm_get_merge_request_comments` | its comments, general and on diff lines, oldest first |
| `scm_add_merge_request_comment` | a general comment |
| `scm_add_merge_request_diff_comment` | a comment on one line of the diff (`file_path`, `line_number`, `line_type` new or old) |
| `scm_get_merge_request_diff` | its changes per file within a 60 KB budget, truncation marked per file |
| `scm_set_merge_request_title`, `scm_set_merge_request_description` | edit the merge request |
| `scm_approve_merge_request` | approve as the token's user |
| `scm_create_merge_request` | open a merge request (`from`, `to`, `title`, optional `description`, `assignees`, `reviewers`); answers the new merge request as `scm_get_merge_request_details` does, with `warnings` for assignees or reviewers the SCM refused |

Write tools are always available. Allowed values (`state`, `visibility`,
`line_type`) are checked by the tools, since the schema validator has no
`enum`. Without an SCM, or without a match for the project, a tool answers
with the cause and the fix (`harness scm add`, `[scm]`, `harness doctor`);
provider failures become tool errors starting with their kind (`unauthorized`,
`forbidden`, `not_found`, `invalid`, `rate_limited`...) and the fix, never the
token. The MCP tool count is pinned in FindingsProcessTests,
MemoryMcpProcessTests, CouplingProcessTests and `test/Harness.Packaging/run_probe.py`.
