## large private repository
17887 commits; HEAD a098e5c4de83; base HEAD~20 9fce97dd12a6; from HEAD~10 2716458952eb
file <redacted>; retrieval paths <redacted>
git git version 2.56.0.windows.4; libgit2 0.32.0+libgit2-5853918.eaa698d078941fd5e3cc82b59b885cd35d8cc0f8 (x64 - Threads, Https, Ssh, NSec)
libgit2: native load + first open 2.2 ms
libgit2: open + HEAD on a fresh Repository, median 2.5 ms

| command | used by | git first | git median | libgit2 first | libgit2 median | libgit2 fresh repo | same answer |
|---|---|---:|---:|---:|---:|---:|---|
| `ls-files --cached --others --exclude-standard` | #4 file list (sync, every architecture read) | 238.3 | 141.5 | 163.5 | 126.0 | 158.2 | yes (5147) |
| `status --porcelain=v1 -z -uall` | #5 dirty paths (every sync, coupling) | 315.3 | 153.3 | 267.7 | 228.2 | 287.3 | yes (51) |
| `cat-file -e <base>^{commit}` | #3 commit exists (2x per incremental sync) | 36.0 | 34.9 | 0.4 | 0.0 | 3.5 | yes (1) |
| `diff --name-status -M <base> -- + ls-files --others` | #6+#7 changes since the last sync (incremental sync) | 458.1 | 423.7 | 523.1 | 435.2 | 420.5 | yes (230) |
| `log --since=180d --no-merges --name-status -M` | #11 churn (every change_coupling call) | 236.2 | 182.4 | 4248.4 | 3878.3 | 4177.6 | **no** git 9781 / libgit2 9773 (rename pairings differ; paths redacted) |
| `log -n 5 --no-merges -- <3 paths>` | #12 recent commits (get_context, cached per session) | 40.3 | 37.3 | 55.7 | 54.6 | 247.9 | yes (5) |
| `diff --name-only <from> <to>` | #13 telemetry resolve (per pending get_context) | 34.3 | 32.1 | 5.5 | 4.4 | 16.9 | yes (49) |
| `diff -U0 <from> <to> -- <file> (hunk headers)` | #14 changed functions (up to 10 per resolved call) | 33.5 | 30.0 | 1.7 | 0.5 | 14.1 | yes (1) |
| `show <from>:<file>` | #15 file at a commit (telemetry resolve) | 32.9 | 30.7 | 0.3 | 0.0 | 3.5 | yes (1) |
| `config --get core.hooksPath` | #16 hooks folder (ui hooks endpoint) | 26.8 | 24.2 | 1.0 | 0.1 | 2.0 | yes (1) |
| `check-ignore .harness/knowledge.toml` | #10 is the shared knowledge file ignored (ui, when it exists) | 30.3 | 32.0 | 0.6 | 0.1 | 2.9 | yes (1) |
