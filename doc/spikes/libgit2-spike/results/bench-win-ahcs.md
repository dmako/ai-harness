## ai-harness-cs
155 commits; HEAD 83de74d459c3; base HEAD~20 b7a98cc45c39; from HEAD~10 b2b33e8752cf
file src/Harness.Cli/Ui/UiReadService.Evidence.cs; retrieval paths src/Harness.Cli/Ui/UiReadService.Evidence.cs, src/Harness.Infrastructure/Architecture/AnalysisRepository.cs, src/Harness.Infrastructure/Architecture/ArchitectureReader.cs
git git version 2.56.0.windows.4; libgit2 0.32.0+libgit2-5853918.eaa698d078941fd5e3cc82b59b885cd35d8cc0f8 (x64 - Threads, Https, Ssh, NSec)
libgit2: native load + first open 2.1 ms
libgit2: open + HEAD on a fresh Repository, median 2.7 ms

| command | used by | git first | git median | libgit2 first | libgit2 median | libgit2 fresh repo | same answer |
|---|---|---:|---:|---:|---:|---:|---|
| `ls-files --cached --others --exclude-standard` | #4 file list (sync, every architecture read) | 41.5 | 38.1 | 30.5 | 18.5 | 31.7 | yes (576) |
| `status --porcelain=v1 -z -uall` | #5 dirty paths (every sync, coupling) | 42.3 | 40.4 | 17.3 | 17.3 | 27.9 | yes (13) |
| `cat-file -e <base>^{commit}` | #3 commit exists (2x per incremental sync) | 30.1 | 27.4 | 0.7 | 0.0 | 1.8 | yes (1) |
| `diff --name-status -M <base> -- + ls-files --others` | #6+#7 changes since the last sync (incremental sync) | 97.9 | 89.9 | 36.0 | 34.3 | 50.1 | yes (83) |
| `log --since=180d --no-merges --name-status -M` | #11 churn (every change_coupling call) | 118.5 | 111.1 | 214.5 | 97.7 | 205.9 | yes (1238) |
| `log -n 5 --no-merges -- <3 paths>` | #12 recent commits (get_context, cached per session) | 41.4 | 32.4 | 16.5 | 16.2 | 36.5 | yes (5) |
| `diff --name-only <from> <to>` | #13 telemetry resolve (per pending get_context) | 32.9 | 30.2 | 1.5 | 0.8 | 20.3 | yes (21) |
| `diff -U0 <from> <to> -- <file> (hunk headers)` | #14 changed functions (up to 10 per resolved call) | 34.8 | 30.1 | 2.8 | 1.6 | 8.9 | **no** git 16 / libgit2 19; git only: @@ -279,12 +361 @@ internal sealed partial class UiReadService; @@ -295,0 +367,23 @@ internal sealed partial class UiReadService; libgit2 only: @@ -279 +361 @@ internal sealed partial class UiReadService; @@ -280,0 +363,11 @@ internal sealed partial class UiReadService |
| `show <from>:<file>` | #15 file at a commit (telemetry resolve) | 35.4 | 32.8 | 2.4 | 0.6 | 6.2 | yes (1) |
| `config --get core.hooksPath` | #16 hooks folder (ui hooks endpoint) | 27.9 | 28.6 | 1.5 | 0.1 | 2.6 | yes (1) |
| `check-ignore .harness/knowledge.toml` | #10 is the shared knowledge file ignored (ui, when it exists) | 27.4 | 27.3 | 0.4 | 0.1 | 3.9 | yes (1) |
