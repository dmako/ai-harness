## large private repository (WSL clone)
17887 commits; HEAD a098e5c4de83; base HEAD~20 9fce97dd12a6; from HEAD~10 2716458952eb
file <redacted>; retrieval paths <redacted>
git git version 2.55.0; libgit2 0.32.0+libgit2-5853918.eaa698d078941fd5e3cc82b59b885cd35d8cc0f8 (x64 - Threads, Https, Ssh, NSec)
libgit2: native load + first open 0.7 ms
libgit2: open + HEAD on a fresh Repository, median 0.3 ms

| command | used by | git first | git median | libgit2 first | libgit2 median | libgit2 fresh repo | same answer |
|---|---|---:|---:|---:|---:|---:|---|
| `ls-files --cached --others --exclude-standard` | #4 file list (sync, every architecture read) | 60.5 | 39.2 | 98.1 | 51.5 | 59.9 | yes (5096) |
| `status --porcelain=v1 -z -uall` | #5 dirty paths (every sync, coupling) | 94.6 | 96.4 | 47.6 | 52.6 | 68.0 | yes (0) |
| `cat-file -e <base>^{commit}` | #3 commit exists (2x per incremental sync) | 5.5 | 3.7 | 0.2 | 0.0 | 0.5 | yes (1) |
| `diff --name-status -M <base> -- + ls-files --others` | #6+#7 changes since the last sync (incremental sync) | 105.7 | 108.3 | 126.5 | 117.9 | 124.1 | yes (179) |
| `log --since=180d --no-merges --name-status -M` | #11 churn (every change_coupling call) | 137.3 | 141.1 | 1671.6 | 1284.6 | 1938.4 | **no** git 9781 / libgit2 9773 (rename pairings differ; paths redacted) |
| `log -n 5 --no-merges -- <3 paths>` | #12 recent commits (get_context, cached per session) | 9.6 | 6.0 | 52.7 | 49.4 | 255.0 | yes (5) |
| `diff --name-only <from> <to>` | #13 telemetry resolve (per pending get_context) | 6.4 | 6.2 | 4.9 | 2.8 | 11.4 | yes (49) |
| `diff -U0 <from> <to> -- <file> (hunk headers)` | #14 changed functions (up to 10 per resolved call) | 9.9 | 5.0 | 1.0 | 0.1 | 5.3 | yes (1) |
| `show <from>:<file>` | #15 file at a commit (telemetry resolve) | 6.7 | 3.4 | 0.3 | 0.0 | 0.4 | yes (1) |
| `config --get core.hooksPath` | #16 hooks folder (ui hooks endpoint) | 5.1 | 3.6 | 0.7 | 0.1 | 0.5 | yes (1) |
| `check-ignore .harness/knowledge.toml` | #10 is the shared knowledge file ignored (ui, when it exists) | 8.9 | 6.2 | 0.1 | 0.0 | 0.3 | yes (1) |
