## large private repository
17887 commits; HEAD a098e5c4de83; base HEAD~20 9fce97dd12a6; from HEAD~10 2716458952eb
file <redacted>; retrieval paths <redacted>
git git version 2.56.0.windows.4; libgit2 0.32.0+libgit2-5853918.eaa698d078941fd5e3cc82b59b885cd35d8cc0f8 (x64 - Threads, Https, Ssh, NSec)
libgit2: native load + first open 1.5 ms
libgit2: open + HEAD on a fresh Repository, median 1.6 ms
GitReader: first open + HEAD 2.7 ms
GitReader: open + HEAD on a fresh repository, median 2.2 ms

| command | used by | git first | git median | libgit2 first | libgit2 median | libgit2 fresh repo | libgit2 same | GitReader first | GitReader median | GitReader fresh repo | GitReader same |
|---|---|---:|---:|---:|---:|---:|---|---:|---:|---:|---|
| `ls-files --cached --others --exclude-standard` | #4 file list (sync, every architecture read) | 81.8 | 79.6 | 95.0 | 79.1 | 93.2 | yes (5147) | 3124.5 | 1211.9 | 1243.0 | yes (5147) |
| `status --porcelain=v1 -z -uall` | #5 dirty paths (every sync, coupling) | 124.9 | 90.4 | 71.8 | 70.0 | 84.5 | yes (51) | 1200.2 | 1192.0 | 1223.0 | **no** git 51 / library 4915 (paths redacted) |
| `cat-file -e <base>^{commit}` | #3 commit exists (2x per incremental sync) | 21.2 | 15.0 | 0.2 | 0.0 | 1.0 | yes (1) | 0.3 | 0.0 | 24.0 | yes (1) |
| `diff --name-status -M <base> -- + ls-files --others` | #6+#7 changes since the last sync (incremental sync) | 181.8 | 175.6 | 154.0 | 146.3 | 168.1 | yes (230) | 1513.4 | 1222.8 | 1635.4 | **no** git 230 / library 4929 (paths redacted) |
| `log --since=180d --no-merges --name-status -M` | #11 churn (every change_coupling call) | 95.7 | 94.8 | 1622.4 | 1446.0 | 1608.1 | **no** git 9781 / library 9773 (paths redacted) | 16022.4 | 46.2 | 24799.0 | **no** git 9781 / library 9822 (paths redacted) |
| `log -n 5 --no-merges -- <3 paths>` | #12 recent commits (get_context, cached per session) | 44.4 | 41.3 | 24.4 | 22.0 | 114.0 | yes (5) | 2.9 | 0.5 | 582.1 | yes (5) |
| `diff --name-only <from> <to>` | #13 telemetry resolve (per pending get_context) | 49.6 | 47.2 | 4.0 | 2.0 | 9.8 | yes (49) | 3.4 | 0.2 | 134.3 | yes (49) |
| `diff -U0 <from> <to> -- <file> (hunk headers)` | #14 changed functions (up to 10 per resolved call) | 22.7 | 20.4 | 0.8 | 0.4 | 5.8 | yes (1) | - | - | - | no equivalent |
| `show <from>:<file>` | #15 file at a commit (telemetry resolve) | 20.5 | 18.3 | 0.1 | 0.0 | 1.3 | yes (1) | 0.8 | 0.5 | 28.9 | yes (1) |
| `config --get core.hooksPath` | #16 hooks folder (ui hooks endpoint) | 84.2 | 16.7 | 0.6 | 0.0 | 0.9 | yes (1) | - | - | - | no equivalent |
| `check-ignore .harness/knowledge.toml` | #10 is the shared knowledge file ignored (ui, when it exists) | 19.3 | 19.8 | 0.2 | 0.1 | 1.5 | yes (1) | - | - | - | no equivalent |
