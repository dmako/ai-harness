## ai-harness-cs (WSL clone)
155 commits; HEAD 83de74d459c3; base HEAD~20 b7a98cc45c39; from HEAD~10 b2b33e8752cf
file src/Harness.Cli/Ui/UiReadService.Evidence.cs; retrieval paths src/Harness.Cli/Ui/UiReadService.Evidence.cs, src/Harness.Infrastructure/Architecture/AnalysisRepository.cs, src/Harness.Infrastructure/Architecture/ArchitectureReader.cs
git git version 2.55.0; libgit2 0.32.0+libgit2-5853918.eaa698d078941fd5e3cc82b59b885cd35d8cc0f8 (x64 - Threads, Https, Ssh, NSec)
libgit2: native load + first open 0.3 ms
libgit2: open + HEAD on a fresh Repository, median 0.2 ms

| command | used by | git first | git median | libgit2 first | libgit2 median | libgit2 fresh repo | same answer |
|---|---|---:|---:|---:|---:|---:|---|
| `ls-files --cached --others --exclude-standard` | #4 file list (sync, every architecture read) | 68.4 | 6.1 | 35.6 | 8.9 | 10.1 | yes (563) |
| `status --porcelain=v1 -z -uall` | #5 dirty paths (every sync, coupling) | 29.9 | 11.4 | 12.8 | 8.5 | 10.8 | yes (0) |
| `cat-file -e <base>^{commit}` | #3 commit exists (2x per incremental sync) | 4.5 | 3.6 | 0.2 | 0.0 | 0.2 | yes (1) |
| `diff --name-status -M <base> -- + ls-files --others` | #6+#7 changes since the last sync (incremental sync) | 19.4 | 14.4 | 21.1 | 17.6 | 19.9 | yes (70) |
| `log --since=180d --no-merges --name-status -M` | #11 churn (every change_coupling call) | 22.5 | 23.5 | 36.7 | 30.6 | 29.1 | yes (1238) |
| `log -n 5 --no-merges -- <3 paths>` | #12 recent commits (get_context, cached per session) | 5.5 | 4.1 | 2.1 | 1.5 | 3.3 | yes (5) |
| `diff --name-only <from> <to>` | #13 telemetry resolve (per pending get_context) | 4.5 | 3.6 | 0.7 | 0.1 | 0.9 | yes (21) |
| `diff -U0 <from> <to> -- <file> (hunk headers)` | #14 changed functions (up to 10 per resolved call) | 5.3 | 4.4 | 0.9 | 0.4 | 1.2 | yes (19) |
| `show <from>:<file>` | #15 file at a commit (telemetry resolve) | 4.1 | 3.6 | 0.3 | 0.1 | 0.3 | yes (1) |
| `config --get core.hooksPath` | #16 hooks folder (ui hooks endpoint) | 4.1 | 4.8 | 0.6 | 0.0 | 0.2 | yes (1) |
| `check-ignore .harness/knowledge.toml` | #10 is the shared knowledge file ignored (ui, when it exists) | 4.1 | 3.5 | 0.3 | 0.0 | 0.4 | yes (1) |
