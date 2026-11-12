## ai-harness-cs
156 commits; HEAD 022af290d695; base HEAD~20 5cab8ea57fa7; from HEAD~10 063f39288056
file src/Harness.Cli/Ui/UiReadService.Evidence.cs; retrieval paths src/Harness.Cli/Ui/UiReadService.Evidence.cs, src/Harness.Infrastructure/Architecture/AnalysisRepository.cs, src/Harness.Infrastructure/Architecture/ArchitectureReader.cs
git git version 2.56.0.windows.4; libgit2 0.32.0+libgit2-5853918.eaa698d078941fd5e3cc82b59b885cd35d8cc0f8 (x64 - Threads, Https, Ssh, NSec)
libgit2: native load + first open 1.5 ms
libgit2: open + HEAD on a fresh Repository, median 1.4 ms
GitReader: first open + HEAD 1.3 ms
GitReader: open + HEAD on a fresh repository, median 0.4 ms

| command | used by | git first | git median | libgit2 first | libgit2 median | libgit2 fresh repo | libgit2 same | GitReader first | GitReader median | GitReader fresh repo | GitReader same |
|---|---|---:|---:|---:|---:|---:|---|---:|---:|---:|---|
| `ls-files --cached --others --exclude-standard` | #4 file list (sync, every architecture read) | 92.1 | 24.3 | 19.3 | 10.4 | 17.6 | yes (584) | 119.1 | 123.5 | 125.6 | **no** git 584 / libgit2 585; git only: ; libgit2 only: .claude/scheduled_tasks.lock |
| `status --porcelain=v1 -z -uall` | #5 dirty paths (every sync, coupling) | 27.9 | 24.9 | 13.2 | 10.3 | 18.2 | yes (11) | 123.0 | 121.6 | 134.0 | **no** git 11 / libgit2 562; git only: ; libgit2 only: .claude/scheduled_tasks.lock; .editorconfig |
| `cat-file -e <base>^{commit}` | #3 commit exists (2x per incremental sync) | 17.3 | 16.6 | 0.3 | 0.0 | 1.0 | yes (1) | 0.2 | 0.0 | 0.4 | yes (1) |
| `diff --name-status -M <base> -- + ls-files --others` | #6+#7 changes since the last sync (incremental sync) | 60.5 | 54.0 | 24.8 | 20.3 | 29.7 | yes (90) | 138.2 | 119.0 | 145.0 | **no** git 90 / libgit2 580; git only: ; libgit2 only: ? .claude/scheduled_tasks.lock; M .editorconfig |
| `log --since=180d --no-merges --name-status -M` | #11 churn (every change_coupling call) | 69.6 | 67.6 | 89.4 | 35.9 | 84.5 | yes (1250) | 352.2 | 4.1 | 457.6 | **no** git 1250 / libgit2 1251; git only: R src/Harness.Infrastructure/Tasks/TaskStore.Milestones.cs -> src/Harness.Infras; libgit2 only: A src/Harness.Infrastructure/Tasks/TaskStore.Blueprints.cs; D src/Harness.Infrastructure/Tasks/TaskStore.Milestones.cs |
| `log -n 5 --no-merges -- <3 paths>` | #12 recent commits (get_context, cached per session) | 22.9 | 16.7 | 6.4 | 6.0 | 15.6 | yes (5) | 0.5 | 0.3 | 16.5 | yes (5) |
| `diff --name-only <from> <to>` | #13 telemetry resolve (per pending get_context) | 23.7 | 17.8 | 1.0 | 0.4 | 7.0 | yes (33) | 0.1 | 0.0 | 5.6 | yes (33) |
| `diff -U0 <from> <to> -- <file> (hunk headers)` | #14 changed functions (up to 10 per resolved call) | 26.7 | 17.2 | 1.4 | 0.7 | 3.3 | **no** git 16 / libgit2 19; git only: @@ -279,12 +361 @@ internal sealed partial class UiReadService; @@ -295,0 +367,23 @@ internal sealed partial class UiReadService; libgit2 only: @@ -279 +361 @@ internal sealed partial class UiReadService; @@ -280,0 +363,11 @@ internal sealed partial class UiReadService | - | - | - | no equivalent |
| `show <from>:<file>` | #15 file at a commit (telemetry resolve) | 18.7 | 16.5 | 0.5 | 0.2 | 1.6 | yes (1) | 0.2 | 0.1 | 0.7 | yes (1) |
| `config --get core.hooksPath` | #16 hooks folder (ui hooks endpoint) | 18.9 | 14.6 | 0.6 | 0.0 | 1.0 | yes (1) | - | - | - | no equivalent |
| `check-ignore .harness/knowledge.toml` | #10 is the shared knowledge file ignored (ui, when it exists) | 17.5 | 16.6 | 0.2 | 0.1 | 1.4 | yes (1) | - | - | - | no equivalent |
