# Spike: replace git processes with GitReader? (TASK-0098)

**Recommendation: no.** Keep the external git, as TASK-0097 decided for LibGit2Sharp ([README.md](README.md)).

GitReader 1.18.0 (NuGet `GitReader`, Apache-2.0, fully managed) passes the Native AOT check that LibGit2Sharp failed. It breaks on most other counts:

- **Missing features.** It has no diff, no rename detection, no path-filtered history, no config access and no ignore check. Harness would have to write those itself.
- **Wrong answers on ordinary repositories.**
  - With `core.autocrlf` (the Windows default) it reports every checked-out file as modified.
  - It ignores `.git/info/exclude`.
  - It reads the main work tree from a linked worktree.
  - It cannot read an index that has extended flags, which sparse checkout and `git add -N` set.
  - It does not follow alternates, and fails on shallow clones.
- **10–200× slower than git on a large repository** for everything that touches the work tree or walks history. It hashes every file on each status, and loads every tree.
- **Windows processes don't shut down cleanly.** After reading the large repository, they keep hundreds of handles after `Dispose`, take from 27 s to more than 30 minutes to exit, and can't be killed while they do.

The only operations where it wins are single object reads (commit exists, `show`, `diff --name-only`) on a repository that is already open and cached. That is the same narrow win LibGit2Sharp had, and batching the git calls gets it without a library (TASK-0097 follow-ups).

## How it was measured

GitReader was added as a column to the TASK-0097 benchmark (`LibGit2Spike`, this folder). It runs the same commands on the same repositories and machines, published as trimmed Native AOT:

- **Repositories:** ai-harness-cs at `022af29`, and the large private repository (17,887 commits, 2.4 GiB pack).
- **Machines:** Windows (git 2.56) and WSL Ubuntu (git 2.55).

GitReader offers HEAD and refs, commits, trees, blobs, work-tree status and untracked files. Everything else is written in [`GitReaderOps.cs`](GitReaderOps.cs) on top of those, the way harness would have to:

- tree-to-tree diff (equal subtrees are skipped);
- exact-content rename pairing;
- a commit-date-ordered history walk;
- path lookup;
- a tree and commit cache per repository.

Two modes were added for this spike:

- **`exit <repo> status|churn|recent`** runs one GitReader read on a fresh repository, disposes it, prints the process's handles and memory, and returns. It was used to time how long the process takes to exit.
- **`LG2SPIKE_DUMP=<folder>`** writes every library's full answers to files. AOT and JIT runs were compared with it.

The raw output is in [`results/`](results/), in the `gitreader-*` files.

## Where harness runs git, with GitReader

The invocation numbers are those of the [TASK-0097 inventory](README.md#where-harness-runs-git).

| # | git command | GitReader |
|---|---|---|
| 1, 2 | HEAD on **reftable** repositories | **wrong**: it opens the repository, then reading HEAD fails ("Nullable object must have a value") and status lists every file |
| 3 | `cat-file -e <rev>^{commit}` | `GetCommitAsync(hash)`: same answer |
| 4 | `ls-files --cached --others --exclude-standard` | `GetWorkingDirectoryStatusAsync().ProcessedPaths` + `GetUntrackedFilesAsync`. **Wrong**: `.git/info/exclude` is ignored (ai-harness-cs: 585 against git's 584). Slow: the status hashes every file |
| 5 | `status --porcelain=v1 -z -uall` | `GetWorkingDirectoryStatusAsync` + `GetUntrackedFilesAsync`. **Wrong with `core.autocrlf=true`**: it hashes work-tree bytes without the CRLF→LF conversion. ai-harness-cs gives 562 dirty paths against git's 11; the large repository 4,915 against 51. Correct on Linux |
| 6, 7 | `diff --name-status -M <base> --` + `ls-files --others` | *differently*: no diff API, so it is built from HEAD's tree, the status and the base tree. Inherits #5's error on Windows. Correct on Linux, where no renames occurred |
| 8, 9 | `ls-tree -r`, `hash-object` | tree walk: supported. Hash-only: *not verified* (no API found) |
| 10 | `check-ignore -v` | **unsupported**: no ignore check for one path. Its glob filter is only applied while listing untracked files |
| 11 | churn `log --since=180d --no-merges --name-status -M` | *differently*: own walk + own tree diff + **exact renames only**. Git's similarity renames would be harness's own code (ai-harness-cs: 1,251 entries against 1,250; large: 9,822 against 9,781) |
| 12 | `log -n 5 --no-merges -- <paths>` | *differently*: own walk comparing the paths' tree entries. Same answers on both repositories |
| 13 | `diff --name-only <from> <to>` | *differently*: own tree diff. Same answers |
| 14 | `diff -U0` hunk headers | **unsupported**: no line diff. Harness would need its own diff algorithm and function-name patterns |
| 15 | `show <commit>:<path>` | `OpenBlobAsync` after a tree lookup: same answer |
| 16 | `config --get core.hooksPath` | **unsupported**: no config API (only `RemoteUrls`) |

**Repository formats** (`formats`; same on Windows and Linux unless noted)

| repository | GitReader |
|---|---|
| plain, untracked cache, `extensions.worktreeConfig`, submodule, packed refs, commit-graph + multi-pack-index, `core.autocrlf=true` (repository's own files) | same as git |
| SHA-256 objects | opens, then **fails**: "Invalid hash string", "Broken Git index file" |
| reftable | opens, then **wrong**: HEAD fails, status lists every file |
| index v4 | **file list and status fail**: "Missing null terminator" |
| split index | **file list and status fail**: "An item with the same key has already been added" |
| sparse checkout (cone, *no* sparse index) | **file list and status fail**: "Missing null terminator". The index has extended flags (skip-worktree), which `git add -N` also sets |
| sparse index | **fails** as above |
| linked worktree | **wrong**: reports the *main* work tree's untracked file as its own |
| shared clone (alternates) | **wrong**: objects in the alternate store are not found, so all files are dirty and the commit history is missing |
| shallow clone | **fails** on the oldest commit's parent ("Nullable object must have a value"): `.git/shallow` is ignored. Windows only: the fresh checkout's CRLF files also show as dirty |
| partial clone (`blob:none`) | **fails** on missing objects ("Couldn't find tree object"); git fetches them on demand |

## Performance

All times are in ms: **git median → GitReader** warm median / fresh repository (open + read + dispose). "Warm" uses a repository already open and its tree/commit cache (in `GitReaderOps.cs`). "Fresh" is what a call costs without a long-lived cache. **Bold** marks a wrong answer.

### Windows

| command | ai-harness-cs | large |
|---|---:|---:|
| file list (#4) | 24 → **124** / **126** | 80 → 1,212 / 1,243 |
| `status` (#5) | 25 → **122** / **134** | 90 → **1,192** / **1,223** |
| `cat-file -e` (#3) | 17 → 0.0 / 0.4 | 15 → 0.0 / 24 |
| `diff <base>` + untracked (#6, #7) | 54 → **119** / **145** | 176 → **1,223** / **1,635** |
| churn `log` 180 days (#11) | 68 → 4.1 / 458 (first 352) | 95 → 46 / **24,799** (first **16,022**) |
| retrieval `log` (#12) | 17 → 0.3 / 16.5 | 41 → 0.5 / 582 |
| `diff --name-only` (#13) | 18 → 0.0 / 5.6 | 47 → 0.2 / 134 |
| `show` (#15) | 17 → 0.1 / 0.7 | 18 → 0.5 / 29 |

### Linux (WSL)

| command | ai-harness-cs | large |
|---|---:|---:|
| file list | 7.8 → 88 / 115 | 29 → 934 / 865 |
| `status` | 12 → 99 / 82 | 101 → 888 / 919 |
| `cat-file -e` | 3.9 → 0.0 / 2.4 | 4.1 → 0.0 / 50 |
| `diff <base>` + untracked | 12 → 94 / 169 | 107 → 730 / 1,224 |
| churn `log` | 12 → 5.5 / 1,476 (first 1,240) | 140 → 73 / 22,608 (first 22,510) |
| retrieval `log` | 3.7 → 0.4 / 104 | 6.6 → 0.6 / 366 |
| `diff --name-only` | 5.3 → 0.1 / 32 | 5.3 → 0.4 / 119 |
| `show` | 4.4 → 1.0 / 8.9 | 2.9 → 0.5 / 45 |

Opening a repository is cheap (0.4–2.5 ms). Reading the first object of a large repository's pack is not (24–50 ms). History walks load every tree they touch (16–25 s for 180 days on the large repository). They are fast only from a cache that harness would have to keep, and that cache costs memory: about 430 MB of private memory after one read of the large repository on Linux.

### Effect on whole operations

Priced from the tables above, as in TASK-0097:

| path | git | GitReader |
|---|---:|---:|
| incremental sync, ai-harness-cs, Windows (`cat-file` ×2, `diff`, untracked, `status`, file list) | ≈ 135 ms | ≈ 370 ms, and **wrong** (CRLF) |
| incremental sync, large repository, Windows | ≈ 380 ms | ≈ 3.6 s, and **wrong** |
| incremental sync, large repository, Linux | ≈ 245 ms | ≈ 2.6 s |
| ui/MCP coupling (churn + `status`), large repository, fresh | ≈ 185 ms Windows / 240 ms Linux | ≈ 26 s / 23.5 s |
| telemetry resolve per pending call (`diff --name-only`, up to 10 `diff -U0`, k `show`) | ≈ 17–47 ms per process on Windows | `diff -U0` unsupported, so git stays for it. The rest costs 6–134 ms when fresh |

## Native AOT

- **Builds.** A trimmed Native AOT build referencing GitReader publishes for **win-x64 and linux-x64 with no IL, trim or AOT warnings**.
- **Answers match JIT.** Every API the benchmark uses gives **the same answers as under JIT**. Every command's full answer, for both libraries, was dumped from an AOT run and a JIT run (`LG2SPIKE_DUMP`) and compared: 19 of 19 identical. There is no marshalling, so none of LibGit2Sharp's silent nulls or crashes.
- **Size.** It adds about **1.9 MB** per executable (probe: 2.87 MB against 0.94 MB for an empty app), with no native files.
- **arm64 and osx-arm64.** These were not run.
  - Being pure managed code, GitReader should behave the same there.
  - What could still differ: case-sensitive paths on Linux against case-insensitive ones on macOS and Windows; file sharing and locking (which differ on Windows, see below); and line endings, which follow the checkout rather than the platform.

## Risks

- **Correctness.** The answers above are wrong rather than failing loudly, in repositories harness meets today:
  - Windows checkouts with `core.autocrlf`;
  - `.git/info/exclude`;
  - linked worktrees, which harness supports;
  - `git add -N` and sparse checkouts;
  - shared clones;
  - shallow CI checkouts.
- **Shutdown and handles on Windows.** Every Windows process that read the large repository with GitReader was slow to exit:
  - **The benchmark process** printed its whole table, then sat in process teardown. It exited about **38 minutes** after printing the table, having used **over 30 minutes of CPU, almost all of it kernel time**. It could not be killed in the meantime ("There is no running instance of the task", yet still listed and holding its executable).
  - **A probe stopped after it timed out** took about 100 s to die. A second one stalled and could not be killed either.
  - **A single `status` read** returned with **886 handles still open after `Dispose`**, then took **27 s** to exit. That read itself took 64 s, but it ran while the first stuck process was spinning, so its timings are inflated.

  On Linux the same reads leave 8–68 handles and exit at once. The small repository is unaffected on both. The cause was not found: GitReader keeps a `FileStreamCache` of open pack streams and does not memory-map files. A long-running `harness ui` or MCP server would accumulate this, and a CLI run (`harness sync` from a git hook) could hang the hook.
- **Speed and memory.** Status and file lists hash every file; git uses the index's file timestamps instead. History needs a large in-memory cache to be fast.
- **Maintenance.**
  - **One main maintainer:** kekyo, 263 of about 275 commits.
  - **Popularity:** 106 stars, 9 forks.
  - **Releases:** 32 so far, the last two in June 2026, after an 11-month gap. Recent issues were answered within days to a few months.
  - **Feature lag:** worktrees arrived in March 2025, and `.gitignore` handling in working-directory enumeration in June 2025. Each new git feature would arrive late, or never.
- **Licence.** GitReader and GitReader.Core are Apache-2.0, which is compatible with harness's MIT licence. Releases would need the Apache-2.0 licence text (and a NOTICE file, if the project ships one) in `doc/third-party-notices`. There are no other runtime dependencies on net10.0.
- **Threading.** The API is async (`Task`-based) and has an `IConcurrentScope` option. Sharing one repository instance across requests was not tested, so a repository per request, or a lock, would be the safe choice.
- **What it would remove.** The git subprocess stdin hazard on the MCP server (mem_6e62c00914eb20f4) and the process timeouts. Harness already handles both.

## Follow-ups

None beyond TASK-0097's (still in the Inbox):

- fewer git starts per sync;
- batched telemetry git reads;
- profiling `harness sync`.

Combining git with GitReader for the single object reads would add a library, its Windows shutdown problem and a second code path, to save what batching saves anyway.
