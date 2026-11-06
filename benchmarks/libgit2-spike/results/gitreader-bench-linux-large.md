## large private repository (WSL clone)
17887 commits; HEAD a098e5c4de83; base HEAD~20 9fce97dd12a6; from HEAD~10 2716458952eb
file <redacted>; retrieval paths <redacted>
git git version 2.55.0; libgit2 0.32.0+libgit2-5853918.eaa698d078941fd5e3cc82b59b885cd35d8cc0f8 (x64 - Threads, Https, Ssh, NSec)
libgit2: native load + first open 0.3 ms
libgit2: open + HEAD on a fresh Repository, median 0.4 ms
GitReader: first open + HEAD 2.5 ms
GitReader: open + HEAD on a fresh repository, median 1.1 ms

| command | used by | git first | git median | libgit2 first | libgit2 median | libgit2 fresh repo | libgit2 same | GitReader first | GitReader median | GitReader fresh repo | GitReader same |
|---|---|---:|---:|---:|---:|---:|---|---:|---:|---:|---|
| `ls-files --cached --others --exclude-standard` | #4 file list (sync, every architecture read) | 36.6 | 29.2 | 61.1 | 50.4 | 64.2 | yes (5096) | 1062.3 | 933.6 | 864.5 | yes (5096) |
| `status --porcelain=v1 -z -uall` | #5 dirty paths (every sync, coupling) | 100.2 | 100.8 | 55.0 | 47.2 | 60.6 | yes (0) | 914.9 | 888.4 | 918.5 | yes (0) |
| `cat-file -e <base>^{commit}` | #3 commit exists (2x per incremental sync) | 6.1 | 4.1 | 0.3 | 0.0 | 0.1 | yes (1) | 0.3 | 0.0 | 49.5 | yes (1) |
| `diff --name-status -M <base> -- + ls-files --others` | #6+#7 changes since the last sync (incremental sync) | 121.5 | 106.5 | 107.5 | 100.5 | 112.6 | yes (179) | 1304.5 | 730.4 | 1224.0 | yes (179) |
| `log --since=180d --no-merges --name-status -M` | #11 churn (every change_coupling call) | 133.2 | 139.8 | 1666.4 | 1230.8 | 1663.0 | **no** git 9781 / library 9773 (paths redacted) | 22510.4 | 72.7 | 22607.7 | **no** git 9781 / library 9822 (paths redacted) |
| `log -n 5 --no-merges -- <3 paths>` | #12 recent commits (get_context, cached per session) | 6.7 | 6.6 | 44.1 | 36.3 | 169.3 | yes (5) | 5.3 | 0.6 | 366.4 | yes (5) |
| `diff --name-only <from> <to>` | #13 telemetry resolve (per pending get_context) | 7.6 | 5.3 | 4.2 | 1.9 | 8.4 | yes (49) | 4.1 | 0.4 | 118.9 | yes (49) |
| `diff -U0 <from> <to> -- <file> (hunk headers)` | #14 changed functions (up to 10 per resolved call) | 3.6 | 3.3 | 0.5 | 0.1 | 4.4 | yes (1) | — | — | — | no equivalent |
| `show <from>:<file>` | #15 file at a commit (telemetry resolve) | 3.5 | 2.9 | 0.2 | 0.0 | 0.2 | yes (1) | 0.6 | 0.5 | 44.9 | yes (1) |
| `config --get core.hooksPath` | #16 hooks folder (ui hooks endpoint) | 3.7 | 2.3 | 0.3 | 0.0 | 0.2 | yes (1) | — | — | — | no equivalent |
| `check-ignore .harness/knowledge.toml` | #10 is the shared knowledge file ignored (ui, when it exists) | 5.1 | 6.2 | 0.1 | 0.0 | 0.3 | yes (1) | — | — | — | no equivalent |
