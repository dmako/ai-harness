# MCP HTTP and provider compatibility

All source locations refer to the pinned Git commit in [the baseline](inventory/baseline.json). Input fields below are derived from exact Go JSON tags; SDK-generated JSON Schemas must be captured and compared in PORT-002. Output and nested field declarations, pointer/slice types and schema descriptions are retained in [schema-types.json](inventory/schema-types.json).

## MCP tools

| Tool | Required input fields by source tag | Optional input fields by source tag | Input and output roots |
| --- | --- | --- | --- |
| `architecture_findings` | none | `scope`, `key`, `full`, `area`, `kind`, `hide_marked`, `gone`, `snippets` | `findingsInput` → `findingsOutput` |
| `architecture_propose` | `kind` | `finding`, `scope`, `title`, `reason`, `evidence` | `proposeInput` → `proposeOutput` |
| `change_coupling` | none | `file`, `check`, `files`, `limit` | `couplingInput` → `couplingOutput` |
| `constraints_current` | none | `scope` | `scopeInput` → `memoryListOutput` |
| `decisions_for` | `scope` | none | `decisionsForInput` → `memoryListOutput` |
| `diagnostics` | none | none | `struct{}` → `diagnosticsOutput` |
| `get_context` | `task` | `path`, `budget`, `tokens`, `debug`, `scope`, `depth` | `contextInput` → `contextOutput` |
| `goals_current` | none | `scope` | `scopeInput` → `memoryListOutput` |
| `library_adoption` | `library` | `depth` | `adoptionInput` → `wsearch.AdoptionReport` |
| `memory_get` | `id` | `history` | `memoryGetInput` → `memory.Detail` |
| `memory_proposals` | none | `status`, `limit` | `memoryProposalsInput` → `memoryProposalsOutput` |
| `memory_propose` | `type`, `content` | `scope`, `source_ref`, `rationale`, `supersedes` | `memoryProposeInput` → `memoryProposeOutput` |
| `memory_put` | `type`, `content` | `scope`, `source_ref`, `source_type`, `confidence`, `valid_until`, `supersedes`, `reason`, `force` | `memoryPutInput` → `memoryPutOutput` |
| `memory_review` | `id`, `action` | `content`, `type`, `scope`, `supersedes`, `keep_both`, `note` | `memoryReviewInput` → `memoryReviewOutput` |
| `memory_search` | none | `query`, `types`, `scope`, `include_inactive`, `limit` | `memorySearchInput` → `memoryListOutput` |
| `memory_supersede` | `id`, `content`, `reason` | `type`, `scope`, `source_ref`, `source_type` | `memorySupersedeInput` → `memoryPutOutput` |
| `package_api` | `action` | `query`, `package`, `version`, `type`, `limit` | `packageInput` → `packageOutput` |
| `ping` | none | none | `struct{}` → `pingOutput` |
| `project_overview` | none | none | `struct{}` → `overviewOutput` |
| `provider_status` | none | none | `struct{}` → `providerStatusOutput` |
| `search_project` | `query` | `kind`, `path`, `limit`, `debug`, `scope`, `depth` | `searchInput` → `searchOutput` |
| `similar_concerns` | `concern` | `depth` | `concernInput` → `wsearch.ConcernReport` |
| `state_get_test` | `key` | none | `stateGetInput` → `stateGetOutput` |
| `state_put_test` | `key`, `value` | none | `statePutInput` → `statePutOutput` |
| `task_claim` | `id` | `lease_minutes` | `taskClaimInput` → `taskOutput` |
| `task_complete` | `id`, `claim_token`, `summary` | `commit`, `acceptance`, `validation`, `proposals` | `taskCompleteInput` → `taskOutput` |
| `task_get` | `id` | `events` | `taskGetInput` → `taskOutput` |
| `task_next` | none | `milestone`, `claim`, `lease_minutes` | `taskNextInput` → `taskOutput` |
| `task_propose` | `title`, `goal`, `acceptance` | `type`, `priority`, `milestone`, `depends_on`, `scope`, `reason` | `taskProposeInput` → `taskProposeOutput` |
| `task_update` | `id`, `claim_token` | `status`, `reason`, `note`, `acceptance` | `taskUpdateInput` → `taskOutput` |

`task_complete` has a custom schema override: validation entries can be command strings or check objects, and validation can be null or an array. Domain requirements, such as reporting every task criterion, remain stricter than schema presence alone. `goals_current` and `constraints_current` share the `currentOf` handler factory with different memory types. See [mcp.json](inventory/mcp.json) for registration locations, the custom schema and argument hints.

## Harness provider protocol

Transport is stdio MCP. Handshake tool `harness_provider_info` reports `protocol`, `name`, capability descriptors and optional `refresh`. The version is `harness-provider/1`; `harness-provider/1.<minor>` is accepted and other major versions are rejected.

Each capability tool replaces dots with underscores in the capability name. Request properties are optional `query`, `path`, `limit`, `target`, `budget`, `package`, `version` and `type`. Responses contain `items` and optional `truncated`, `summary`, `text`. Protocol server results explicitly use an empty items array when there are no items. Indexed providers may offer `harness_refresh` with optional `full` and `changed`, returning optional `skipped` and `detail`.

Generic third-party MCP mappings (`type = "mcp"`) remain supported alongside native harness-provider adapters (`type = "harness"`). Full exact capability types and pack TOML are in [providers.json](inventory/providers.json).

Capability identifiers at the pin:

- `code.symbol_search`
- `code.text_search`
- `code.file_search`
- `project.overview`
- `code.callers`
- `code.references`
- `code.context`
- `code.impact`
- `code.flow`
- `package.search`
- `package.info`
- `package.types`
- `package.type_definition`
- `arch.dependencies`
- `arch.cycles`
- `arch.change_coupling`
- `arch.duplicates`
- `arch.dead_code`

## Registered HTTP routes

The UI default is `127.0.0.1:7477`. All UI handlers are behind loopback Host validation. Writes require JSON POST, same-origin Origin and `X-Harness-UI: 1`. `withDB` opens project state; `withWrite` adds write guards; `handleJSON(true, ...)` is a guarded machine/workspace write. Preserve handler-specific errors and bodies. Provider probes remain on demand. Go GET patterns also accept HEAD under ServeMux behavior.

| Method and pattern | Pinned source | Handler registration |
| --- | --- | --- |
| `GET /favicon.png` | `cmd/harness-collect/internal/collect/collect.go:58` | `func(w http.ResponseWriter, r *http.Request) { // Safari takes no SVG favicon` |
| `GET /logo.svg` | `cmd/harness-collect/internal/collect/collect.go:54` | `func(w http.ResponseWriter, r *http.Request) { // the favicon` |
| `GET /machines/{id}` | `cmd/harness-collect/internal/collect/collect.go:67` | `func(w http.ResponseWriter, r *http.Request) {` |
| `GET /{$}` | `cmd/harness-collect/internal/collect/collect.go:62` | `func(w http.ResponseWriter, r *http.Request) {` |
| `POST /v1/report` | `cmd/harness-collect/internal/collect/collect.go:33` | `func(w http.ResponseWriter, r *http.Request) {` |
| `GET /` | `internal/gui/gui.go:117` | `http.FileServerFS(page))` |
| `GET /api/` | `internal/gui/gui.go:113` | `func(w http.ResponseWriter, _ *http.Request) {` |
| `GET /api/activity` | `internal/gui/gui.go:95` | `handleJSON(false, activityFeed))` |
| `GET /api/docs` | `internal/gui/gui.go:90` | `handleJSON(false, docSections))` |
| `GET /api/docs/{slug}` | `internal/gui/gui.go:91` | `handleJSON(false, docSection))` |
| `GET /api/events` | `internal/gui/gui.go:52` | `newHub(maxStreams)) // live updates (live.go)` |
| `GET /api/graphify/sessions` | `internal/gui/gui.go:92` | `handleJSON(false, graphifySessions))` |
| `GET /api/knowledge` | `internal/gui/gui.go:72` | `handleJSON(false, levelMemories))` |
| `GET /api/knowledge/memories/{mid}` | `internal/gui/gui.go:73` | `handleJSON(false, levelMemoryDetail))` |
| `GET /api/machine` | `internal/gui/gui.go:89` | `handleJSON(false, machineInfo))` |
| `GET /api/projects` | `internal/gui/gui.go:48` | `listProjects)` |
| `GET /api/projects/{id}/applies` | `internal/gui/gui.go:68` | `withDB(applies))` |
| `GET /api/projects/{id}/arch-proposals` | `internal/gui/gui.go:84` | `withDB(archProposals))` |
| `GET /api/projects/{id}/architecture` | `internal/gui/gui.go:61` | `projectArchitecture)` |
| `GET /api/projects/{id}/context` | `internal/gui/gui.go:53` | `withDB(contextStats))` |
| `GET /api/projects/{id}/coupling` | `internal/gui/gui.go:62` | `projectCoupling)` |
| `GET /api/projects/{id}/health` | `internal/gui/gui.go:60` | `projectHealth)` |
| `GET /api/projects/{id}/hooks` | `internal/gui/gui.go:66` | `withDB(projectHooks))` |
| `GET /api/projects/{id}/memories` | `internal/gui/gui.go:55` | `withDB(memories))` |
| `GET /api/projects/{id}/memories/{mid}` | `internal/gui/gui.go:67` | `withDB(memoryDetail))` |
| `GET /api/projects/{id}/metrics` | `internal/gui/gui.go:54` | `withDB(metrics))` |
| `GET /api/projects/{id}/milestones` | `internal/gui/gui.go:58` | `withDB(milestones))` |
| `GET /api/projects/{id}/proposals` | `internal/gui/gui.go:57` | `withDB(proposals))` |
| `GET /api/projects/{id}/providers` | `internal/gui/gui.go:65` | `withDB(providerStatus))` |
| `GET /api/projects/{id}/report` | `internal/gui/gui.go:64` | `withDB(projectReport))` |
| `GET /api/projects/{id}/snippet` | `internal/gui/gui.go:50` | `handleJSON(false, projectSnippet))` |
| `GET /api/projects/{id}/stack` | `internal/gui/gui.go:106` | `handleJSON(false, projectStack))` |
| `GET /api/projects/{id}/structure` | `internal/gui/gui.go:63` | `handleJSON(false, projectStructure))` |
| `GET /api/projects/{id}/summary` | `internal/gui/gui.go:49` | `projectSummary)` |
| `GET /api/projects/{id}/task-proposals` | `internal/gui/gui.go:81` | `withDB(taskProposals))` |
| `GET /api/projects/{id}/tasks` | `internal/gui/gui.go:56` | `withDB(taskList))` |
| `GET /api/projects/{id}/tasks-next-id` | `internal/gui/gui.go:80` | `withDB(nextTaskID))` |
| `GET /api/projects/{id}/tasks/{tid}/events` | `internal/gui/gui.go:59` | `withDB(taskEvents))` |
| `GET /api/search` | `internal/gui/gui.go:94` | `handleJSON(false, search))` |
| `GET /api/stacks` | `internal/gui/gui.go:105` | `handleJSON(false, projectStacks))` |
| `GET /api/workspaces` | `internal/gui/gui.go:96` | `handleJSON(false, listWorkspaces))` |
| `GET /api/workspaces/discover` | `internal/gui/gui.go:97` | `handleJSON(false, discoverProjects))` |
| `GET /api/workspaces/{name}/adoption` | `internal/gui/gui.go:99` | `handleJSON(false, workspaceAdoption))` |
| `GET /api/workspaces/{name}/dependencies` | `internal/gui/gui.go:98` | `handleJSON(false, workspaceDependencies))` |
| `GET /api/workspaces/{name}/duplicates` | `internal/gui/gui.go:100` | `handleJSON(false, workspaceDuplicates))` |
| `GET /api/workspaces/{name}/findings` | `internal/gui/gui.go:103` | `handleJSON(false, workspaceFindings))` |
| `GET /api/workspaces/{name}/issues` | `internal/gui/gui.go:101` | `handleJSON(false, workspaceIssues))` |
| `GET /api/workspaces/{name}/snippet` | `internal/gui/gui.go:51` | `handleJSON(false, workspaceSnippet))` |
| `GET /api/workspaces/{name}/stack` | `internal/gui/gui.go:104` | `handleJSON(false, workspaceStack))` |
| `GET /api/workspaces/{name}/system` | `internal/gui/gui.go:102` | `handleJSON(false, workspaceSystem))` |
| `GET /apple-touch-icon.png` | `internal/gui/gui.go:133` | `asset("image/png", brand.LogoPNG, true))` |
| `GET /brand.css` | `internal/gui/gui.go:130` | `asset("text/css; charset=utf-8", brand.CSS, false))` |
| `GET /favicon.png` | `internal/gui/gui.go:132` | `asset("image/png", brand.LogoPNG, true))` |
| `GET /logo.svg` | `internal/gui/gui.go:131` | `asset("image/svg+xml", brand.LogoSVG, true))` |
| `POST /api/graphify/sessions/{action}` | `internal/gui/gui.go:93` | `handleJSON(true, changeGraphify))` |
| `POST /api/knowledge/memories` | `internal/gui/gui.go:74` | `handleJSON(true, addLevelMemory))` |
| `POST /api/knowledge/memories/{mid}/retire` | `internal/gui/gui.go:76` | `handleJSON(true, changeLevelMemory(true)))` |
| `POST /api/knowledge/memories/{mid}/supersede` | `internal/gui/gui.go:75` | `handleJSON(true, changeLevelMemory(false)))` |
| `POST /api/projects/{id}/arch-proposals/{pid}/approve` | `internal/gui/gui.go:85` | `withWrite(approveArchProposal))` |
| `POST /api/projects/{id}/arch-proposals/{pid}/reject` | `internal/gui/gui.go:86` | `withWrite(rejectArchProposal))` |
| `POST /api/projects/{id}/findings/{action}` | `internal/gui/gui.go:78` | `withWrite(projectFinding))` |
| `POST /api/projects/{id}/memories/{mid}/promote` | `internal/gui/gui.go:71` | `withWrite(promoteMemory))` |
| `POST /api/projects/{id}/memories/{mid}/retire` | `internal/gui/gui.go:70` | `withWrite(retireMemory))` |
| `POST /api/projects/{id}/memories/{mid}/supersede` | `internal/gui/gui.go:69` | `withWrite(supersedeMemory))` |
| `POST /api/projects/{id}/proposals/{pid}/approve` | `internal/gui/gui.go:77` | `withWrite(approveProposal))` |
| `POST /api/projects/{id}/proposals/{pid}/reject` | `internal/gui/gui.go:88` | `withWrite(rejectProposal))` |
| `POST /api/projects/{id}/rename` | `internal/gui/gui.go:112` | `handleJSON(true, renameProject))` |
| `POST /api/projects/{id}/task-proposals/{pid}/approve` | `internal/gui/gui.go:82` | `withWrite(approveTaskProposal))` |
| `POST /api/projects/{id}/task-proposals/{pid}/reject` | `internal/gui/gui.go:83` | `withWrite(rejectTaskProposal))` |
| `POST /api/projects/{id}/tasks` | `internal/gui/gui.go:79` | `withWrite(createTask))` |
| `POST /api/workspaces` | `internal/gui/gui.go:107` | `handleJSON(true, createWorkspace))` |
| `POST /api/workspaces/{name}/delete` | `internal/gui/gui.go:110` | `handleJSON(true, deleteWorkspace))` |
| `POST /api/workspaces/{name}/findings/{action}` | `internal/gui/gui.go:87` | `handleJSON(true, workspaceFinding))` |
| `POST /api/workspaces/{name}/join` | `internal/gui/gui.go:108` | `handleJSON(true, joinWorkspace))` |
| `POST /api/workspaces/{name}/leave` | `internal/gui/gui.go:109` | `handleJSON(true, leaveWorkspace))` |
| `POST /api/workspaces/{name}/rename` | `internal/gui/gui.go:111` | `handleJSON(true, renameWorkspace))` |

## Share server dynamic dispatch

`harness-share` dispatches directly in `share.Handler`, so it has no ServeMux registrations in the table. It permits GET/HEAD only: `/`, `/logo.svg`, `/favicon.png`, `/download/<archive>` for archives present at startup, and `/SHA256SUMS` or `/RELEASE_NOTES.md` when present. Unknown paths return 404; other methods return 405 with `Allow: GET, HEAD`. Exact dispatch source is retained in `inventory/http.json`.

## Streaming and caching

`GET /api/events` carries change notifications rather than full state. The source has a 1500 ms poll interval, 20 s heartbeats, 10 s retry before reopening unavailable databases, and a maximum of 16 streams. Watchers are active only while clients are connected; refetch only affected section kinds. Live streams, writes and page requests must not wait on the slowest independent section. Source: `internal/gui/live.go`, `internal/gui/static/app.js`.
