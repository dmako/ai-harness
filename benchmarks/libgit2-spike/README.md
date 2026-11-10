# Spike: replace git processes with LibGit2Sharp? (TASK-0097)

GitReader, a fully managed alternative, was evaluated the same way in TASK-0098, with the same answer: [GITREADER.md](GITREADER.md).

**Recommendation: no.** Keep the external git. Cut the number of git starts instead (see [Follow-ups](#follow-ups)).

Since TASK-0118 the projects target net11.0 (SDK 11.0.100) and still build and Native AOT-publish on win-x64. The measurements here and in `results/` were taken on .NET 10 (SDK 10.0.403) and were not repeated.

LibGit2Sharp 0.32.0 (libgit2 `5853918`, from LibGit2Sharp.NativeBinaries 2.0.324) has three problems:

- **Small saving.** On Windows it saves about 25–35 ms on each *small* git call. That is only about 150 ms of a 9 s `harness sync`.
- **Large regressions on a big repository.** It is 2–20× *slower* for the calls that dominate there:
  - churn `log`: 3.9 s instead of 0.18 s on the large repository;
  - retrieval `log`;
  - `status`.
- **Unsafe under Native AOT.** Some of its APIs silently return wrong answers or kill the process, and trimming and AOT analysis give no warning.

On Linux, starting git costs about 4 ms, so there is nothing to win there.

## How it was measured

`LibGit2Spike` in this folder is a standalone project. It is not in `harness.slnx`, and the empty `Directory.*` files keep the repository's build rules out. It is published as trimmed Native AOT, like harness.

- **`bench <repo> [runs]`** runs each of harness's hot git commands two ways:
  - as a git process, the way `GitRepository.RunAsync` runs it (same environment, stdin closed);
  - in process through LibGit2Sharp.

  It checks that both give the same answer. "first" is the first call in the process. "median" is a warm repeat. "fresh repo" opens a new `Repository` for each call.
- **`formats <empty folder>`** builds repositories in the formats git supports and checks whether libgit2 can open them and gives the same answers.

```
dotnet publish -c Release -r win-x64 -o <out>     # Windows: needs vswhere on PATH (VS Installer folder)
<out>/LibGit2Spike bench <repository> 7
<out>/LibGit2Spike formats <empty folder>
```

**Machines**
- Windows 11, git 2.56.0.windows.4.
- WSL Ubuntu, git 2.55.0, with repositories cloned to the Linux file system.

**Repositories**
- **ai-harness-cs:** 155 commits, 576 files.
- **a large private repository** (called *large* below): 17,887 commits, 5,100 files, 2.4 GiB pack.

The raw output of every run is in [`results/`](results/); the large repository's file paths are redacted there.

## Where harness runs git

There are 16 distinct invocations, all through `GitRepository.RunAsync`. The one exception is `config --get core.hooksPath`, which runs through `SetupQuery`.

- **HEAD and refs:** read as files, so no process, except on reftable repositories.
- **Number in the table:** the invocation number from the TASK-0097 inventory.
- **Hot:** runs per sync, per MCP call, or per harness ui request.

| # | git command | used by | hot? | LibGit2Sharp |
|---|---|---|---|---|
| 1, 2 | `symbolic-ref`, `rev-parse HEAD` | HEAD on **reftable** repositories only | hot (reftable) | **unsupported**: libgit2 rejects `extensions.refstorage` |
| 3 | `cat-file -e <rev>^{commit}` | sync (2× per incremental sync, same commit), metrics | hot | `Lookup<Commit>` |
| 4 | `ls-files -z --cached --others --exclude-standard` | full sync, every architecture/evidence read, stack badges, snippets | hottest | `Index` + `RetrieveStatus(...).Untracked` |
| 5 | `status --porcelain=v1 -z -uall` | every sync, coupling (MCP and ui) | hot | `RetrieveStatus` |
| 6 | `diff --name-status -z -M <base> --` | incremental sync | hot | `Diff.Compare<TreeChanges>(tree, Index\|WorkingDirectory)`: *differently*: it also returns untracked files as added, which must be filtered out |
| 7 | `ls-files -z --others --exclude-standard` | incremental sync | hot | `RetrieveStatus(...).Untracked` |
| 8 | `ls-tree -r -z <base>` | sync renames (rare) | conditional | tree walk |
| 9 | `hash-object -- <paths>` | sync renames (rare) | conditional | *not verified*: no hash-only call was found (`ObjectDatabase.CreateBlob` writes the object into the repository) |
| 10 | `check-ignore -v -- .harness/knowledge.toml` | ui project list/requests, `memory share` (when the file exists) | hot (ui) | *differently*: `Ignore.IsPathIgnored` answers yes or no, without the `source:line:pattern` that harness shows |
| 11 | `log --no-merges --since=180d --name-status -M` | every `change_coupling`, ui coupling | hot | commit walk + `TreeChanges` per commit: much slower, and rename pairing differs (below) |
| 12 | `log -n 5 --no-merges -- <paths>` | `get_context` retrieval (cached per session) | hot | commit walk comparing the paths' tree entries: *differently*, without git's history simplification |
| 13 | `diff --name-only -z --no-renames <from> <to>` | telemetry resolve (sync, `task_complete`) | hot, one per pending call | `TreeChanges` |
| 14 | `-c core.attributesFile=… diff -U0 <from> <to> -- <file>` | telemetry resolve, up to 10 per call | hot | *differently*: no per-call `-c`, and `Compare<Patch>` crashes under AOT. Blob-to-blob `ContentChanges` works, but its hunks can split differently |
| 15 | `show <commit>:<path>` | telemetry resolve | hot | `Blob.GetContentText()` |
| 16 | `config --get core.hooksPath` | `hooks` CLI, setup, ui hooks endpoint | ui | `Config.Get<string>` |

**Behaviour git applies implicitly** (from `formats`; identical on Windows and Linux)

| repository | libgit2 |
|---|---|
| plain, index v4, untracked cache, `extensions.worktreeConfig`, linked worktree, submodule, alternates (`--shared`), shallow clone, `core.autocrlf=true` with a CRLF work tree, packed refs, commit-graph + multi-pack-index | same HEAD, file list, dirty paths and old file contents as git |
| SHA-256 objects (`--object-format=sha256`) | **cannot open**: "unknown object format 'sha256'" |
| reftable (`--ref-format=reftable`) | **cannot open**: "unsupported extension name extensions.refstorage" |
| split index (`update-index --split-index`) | opens, but **index reads fail**: "unsupported mandatory extension: 'link'" |
| sparse index (`sparse-checkout --sparse-index`) | opens, but **index reads fail**: "unsupported mandatory extension: 'sdir'" |
| sparse checkout (cone, no sparse index) | **wrong status**: files outside the cone show as dirty |
| partial clone (`--filter=blob:none`) | **old file contents fail**: missing blobs are not fetched (git fetches them on demand) |

**Other differences**
- **No `core.fsmonitor` or untracked cache:** libgit2 does not use them. They are what keep git's `status` fast in big work trees.
- **No `safe.directory` check by default:** libgit2 does its own owner validation (`GlobalSettings.SetOwnerValidation`), which harness would have to configure.

## Performance

All times are in ms. Each cell is **git → LibGit2Sharp** (median of warm runs). The **libgit2 first** column is libgit2's first call: on Windows ai-harness-cs for the first table, on the large repository for the second.

### Windows

| command | ai-harness-cs | large | libgit2 first |
|---|---:|---:|---:|
| `ls-files` full (#4) | 38 → 19 | 142 → 126 | 31 |
| `status` (#5) | 40 → 17 | 153 → **228** | 17 |
| `cat-file -e` (#3) | 27 → 0.0 | 35 → 0.0 | 0.7 |
| `diff <base>` + untracked (#6, #7) | 90 → 34 | 424 → 435 | 36 |
| churn `log` 180 days (#11) | 111 → 98 | 182 → **3,878** | 215 / **4,248** |
| retrieval `log -n 5 -- paths` (#12) | 32 → 16 | 37 → **55** | 17 (fresh repo on large: **248**) |
| `diff --name-only` (#13) | 30 → 0.8 | 32 → 4.4 | 1.5 |
| `diff -U0` one file (#14) | 30 → 1.6 | 30 → 0.5 | 2.8 |
| `show` (#15) | 33 → 0.6 | 31 → 0.0 | 2.4 |
| `config --get` (#16) | 29 → 0.1 | 24 → 0.1 | 1.5 |
| `check-ignore` (#10) | 27 → 0.1 | 32 → 0.1 | 0.4 |

Opening a repository costs 1.7–2.5 ms, and the native library loads in about 2 ms.

### Linux (WSL)

| command | ai-harness-cs | large |
|---|---:|---:|
| `ls-files` full | 6.1 → 8.9 | 39 → 52 |
| `status` | 11 → 8.5 | 96 → 53 |
| `cat-file -e` | 3.6 → 0.0 | 3.7 → 0.0 |
| `diff <base>` + untracked | 14 → 18 | 108 → 118 |
| churn `log` | 24 → 31 | 141 → **1,285** |
| retrieval `log` | 4.1 → 1.5 | 6.0 → **49** (fresh repo **255**) |
| `diff --name-only` / `diff -U0` / `show` / `config` / `check-ignore` | 3.5–4.8 → ≤ 0.4 | 3.4–6.2 → ≤ 2.8 |

### Effect on whole operations

These are git's starts in a real `harness sync` on Windows (GIT_TRACE2: 6 processes per incremental sync, 3 per full sync), priced with the times above. The trace itself inflates git's start time about 2.5×, so its own timings are not used.

| path | git | LibGit2Sharp | whole operation |
|---|---:|---:|---:|
| incremental sync, ai-harness-cs: `cat-file` ×2, `diff`, `ls-files --others`, `status`, `ls-files` | ≈ 230 ms | ≈ 70 ms | `harness sync` takes **8.5–10 s**, so the saving is ≈ 1.7 % |
| incremental sync, large repository | ≈ 790 ms | ≈ 790 ms | no gain |
| ui/MCP coupling, ai-harness-cs: churn `log` + `status` | ≈ 150 ms | ≈ 115 ms | small gain |
| ui/MCP coupling, large repository | ≈ 335 ms | ≈ **4,100 ms** | **12× slower** |
| telemetry resolve per pending call (`diff`, up to 10 `diff -U0`, k `show`) | ≈ 30 ms × (2 + 10 + k) | ≈ 1–5 ms | the only real win: hundreds of ms per pending call on Windows |

These git start costs were measured on an idle machine: 22–35 ms per start from a .NET process. TASK-0090 measured about 100 ms per start while the harness ui ran many reads at once. Under that load, every start that is *avoided* is worth more. That favours cutting starts (below) as much as it would favour libgit2.

## Native AOT

**Builds.** A trimmed Native AOT app that references LibGit2Sharp publishes for win-x64 and linux-x64 with **no IL/trim/AOT warnings**.

**What works.** These run and give the same answers as git, and as the same code without AOT:
- `Repository` open, `Head`, `Lookup`, `Index`;
- `RetrieveStatus`, `Diff.Compare<TreeChanges>`, `<PatchStats>`, blob-to-blob `ContentChanges`;
- the commit walk, `Config.Get`, `Ignore.IsPathIgnored`, `Blame`, `Refs`, `Worktrees`.

**Silently wrong under AOT, though the build gives no warning:**
- **Wrong answers:** APIs that return a string through libgit2's `git_buf` give **null** instead of an answer:
  - `Repository.Discover` returns null, while the same call without AOT returns `C:\…\.git\`. Seen on Windows and Linux.
  - `Branch.TrackedBranch` returns null, while without AOT it returns `refs/remotes/origin/…`.
  - Likely cause: LibGit2Sharp passes the buffer as a class, and AOT marshalling hands native code a copy, so the result never comes back. Rooting the whole assembly for the trimmer does not help.
- **Process crash:** `Diff.Compare<Patch>` **kills the process** with a NullReferenceException that can't be caught, while it works without AOT.

So using LibGit2Sharp under AOT would need a vetted subset of APIs, covered by tests that run on AOT builds on every release platform. Every upgrade would have to be re-checked. The alternative is harness's own `[LibraryImport]` bindings to the few libgit2 functions it needs. Both are ongoing costs.

**Native binaries and size**
- **Platforms:** LibGit2Sharp.NativeBinaries 2.0.324 ships libgit2 for all four release platforms:

  | platform | file | size |
  |---|---|---:|
  | win-x64 | `git2-5853918.dll` | 2.01 MB |
  | linux-x64 | `libgit2-5853918.so` | 2.04 MB |
  | linux-arm64 | `libgit2-5853918.so` | 2.00 MB |
  | osx-arm64 | `libgit2-5853918.dylib` | 1.21 MB |

  It also ships win-x86/arm64, linux-arm/musl/ppc64le and osx-x64.
- **Packaging:** publishing copies the library beside the executable, and LibGit2Sharp loads it from there. That is the same pattern as the SQLite sidecar (`doc/native-aot.md`).
- **Dependencies:** the Linux build links only libc, libpthread and libdl (no OpenSSL).
- **Size:** each release grows by the native library plus about 2.0 MB of AOT code per executable that uses it (probe: 2.95 MB against 0.94 MB for an empty app). That is about **+3–4 MB** for `harness`, plus about 2 MB for each further executable that uses git.

## Risks

- **libgit2 lags behind git.**
  - It cannot open SHA-256 or reftable repositories, or read split or sparse indexes.
  - It does not fetch the missing objects of a partial clone.
  - Its status is wrong in sparse checkouts.
  - It does not use fsmonitor or the untracked cache.
  - Every new git repository feature will reach harness late. Today these repositories just work, because harness runs whatever git the user has.
- **Answers differ from git's.**
  - Rename pairing in churn: the large repository gives 9,781 entries with git against 9,773 with libgit2.
  - `diff -U0` hunk splitting: libgit2 does not use git's indent heuristic or `diff.*` config. ai-harness-cs gave 16 hunks with git on Windows against 19 with libgit2, while git on Linux gave 19.
  - `log -- paths` has no history simplification.
  - `check-ignore` has no `-v` detail.
  - Git gives harness user-visible answers (coupling, rename-aware sync), so each difference would need a decision.
- **Licensing.**
  - libgit2 is GPLv2 *with the linking exception*: linking and distributing the binary are allowed; changing libgit2 itself is under GPLv2.
  - It bundles zlib, PCRE2 (BSD), ISC-licensed code and LGPL winhttp definitions.
  - LibGit2Sharp is MIT.
  - This is compatible with harness's MIT licence, but `doc/third-party-notices` and the release archives would have to carry libgit2's licence file (1,482 lines).
- **Thread safety.** A `Repository` instance must not be used by two threads at once. The ui's parallel reads (`Parallel.ForEachAsync`, four at a time) would need a repository per thread, or a lock.
- **Memory and handles.**
  - A `Repository` holds open pack files and mapped memory, and keeps caches that grow. Every `TreeChanges`, `Patch` and `Repository` must be disposed.
  - That suits short CLI runs, but needs care in the long-running `harness ui` and MCP server: bounded lifetime, and reopening when HEAD or packs change. Otherwise, on Windows, held pack files can block `git gc` or repack.
- **What it would remove.** The stdin hazard of git processes on the MCP server (mem_6e62c00914eb20f4) and the 10 s process timeouts would go away. But harness already handles both.

## Follow-ups

These cut git starts without a library. They are proposed into BP-005; none are created.

1. **Sync:**
   - Check that the previous sync's commit exists once, not twice: `cat-file -e` runs twice for the same commit per incremental sync.
   - Take the untracked files from one `ls-files` run instead of two.
   - Read `core.hooksPath` from the config files, or cache it, instead of running `git config` per ui request.
2. **Telemetry resolve:**
   - Batch the per-call fan-out: one `diff --name-only` per distinct (from, to) pair.
   - Read old files through one `cat-file --batch` instead of one `show` each.
   - Run one `diff -U0` over all of a call's paths instead of one per file.
   This is where LibGit2Sharp's only real win was, and batching gets most of it while keeping git's answers.
3. **Why does `harness sync` take 8.5–14 s on ai-harness-cs** when its git calls take about 0.2 s? The time is in harness's own work, and that is the bigger target.
