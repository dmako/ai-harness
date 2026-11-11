## ai-harness-cs (WSL clone)
156 commits; HEAD 022af290d695; base HEAD~20 5cab8ea57fa7; from HEAD~10 063f39288056
file src/Harness.Cli/Ui/UiReadService.Evidence.cs; retrieval paths src/Harness.Cli/Ui/UiReadService.Evidence.cs, src/Harness.Infrastructure/Architecture/AnalysisRepository.cs, src/Harness.Infrastructure/Architecture/ArchitectureReader.cs
git git version 2.55.0; libgit2 0.32.0+libgit2-5853918.eaa698d078941fd5e3cc82b59b885cd35d8cc0f8 (x64 - Threads, Https, Ssh, NSec)
libgit2: native load + first open 0.7 ms
libgit2: open + HEAD on a fresh Repository, median 0.3 ms
GitReader: first open + HEAD 2.0 ms
GitReader: open + HEAD on a fresh repository, median 1.2 ms

| command | used by | git first | git median | libgit2 first | libgit2 median | libgit2 fresh repo | libgit2 same | GitReader first | GitReader median | GitReader fresh repo | GitReader same |
|---|---|---:|---:|---:|---:|---:|---|---:|---:|---:|---|
| `ls-files --cached --others --exclude-standard` | #4 file list (sync, every architecture read) | 95.4 | 7.8 | 18.1 | 8.6 | 10.4 | yes (575) | 150.2 | 88.3 | 115.4 | yes (575) |
| `status --porcelain=v1 -z -uall` | #5 dirty paths (every sync, coupling) | 18.5 | 12.2 | 6.2 | 7.5 | 10.3 | yes (0) | 123.1 | 99.3 | 82.0 | yes (0) |
| `cat-file -e <base>^{commit}` | #3 commit exists (2x per incremental sync) | 6.0 | 3.9 | 0.2 | 0.0 | 0.2 | yes (1) | 0.6 | 0.0 | 2.4 | yes (1) |
| `diff --name-status -M <base> -- + ls-files --others` | #6+#7 changes since the last sync (incremental sync) | 14.2 | 11.7 | 12.9 | 11.4 | 12.6 | yes (81) | 227.2 | 94.4 | 168.7 | yes (81) |
| `log --since=180d --no-merges --name-status -M` | #11 churn (every change_coupling call) | 13.9 | 11.8 | 24.2 | 23.2 | 29.8 | yes (1250) | 1240.3 | 5.5 | 1475.5 | **no** git 1250 / library 1251; git only: R src/Harness.Infrastructure/Tasks/TaskStore.Milestones.cs -> src/Harness.Infras…; library only: A src/Harness.Infrastructure/Tasks/TaskStore.Blueprints.cs; D src/Harness.Infrastructure/Tasks/TaskStore.Milestones.cs |
| `log -n 5 --no-merges -- <3 paths>` | #12 recent commits (get_context, cached per session) | 4.7 | 3.7 | 3.3 | 1.4 | 2.5 | yes (5) | 0.5 | 0.4 | 103.9 | yes (5) |
| `diff --name-only <from> <to>` | #13 telemetry resolve (per pending get_context) | 6.7 | 5.3 | 0.5 | 0.1 | 0.8 | yes (33) | 0.2 | 0.1 | 32.2 | yes (33) |
| `diff -U0 <from> <to> -- <file> (hunk headers)` | #14 changed functions (up to 10 per resolved call) | 6.0 | 5.5 | 1.0 | 0.5 | 1.9 | yes (19) | — | — | — | no equivalent |
| `show <from>:<file>` | #15 file at a commit (telemetry resolve) | 5.6 | 4.4 | 0.3 | 0.1 | 0.7 | yes (1) | 1.3 | 1.0 | 8.9 | yes (1) |
| `config --get core.hooksPath` | #16 hooks folder (ui hooks endpoint) | 4.8 | 4.3 | 0.4 | 0.0 | 0.1 | yes (1) | — | — | — | no equivalent |
| `check-ignore .harness/knowledge.toml` | #10 is the shared knowledge file ignored (ui, when it exists) | 4.6 | 4.9 | 0.6 | 0.0 | 0.5 | yes (1) | — | — | — | no equivalent |
