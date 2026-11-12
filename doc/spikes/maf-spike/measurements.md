# MAF sessions spike: how it was measured (TASK-0112)

**Recommendation: no.** Don't use MAF or the ClaudeCodeSdk.MAF / CodexSdk.MAF bridges. Build on harness's hooks,
on the CLIs' transcripts, and on harness's own runner over the CLIs' stdio protocols. The findings and the reasoning
are in [README.md](README.md). This page describes the spike's program and its runs; the raw output is in
[`results/`](results/).

The spike's code (`benchmarks/maf-spike`) was removed from the repository in TASK-0119. To rerun it, check out commit
`a3e435a`, the last one that has it. Since TASK-0118 the projects targeted net11.0 (SDK 11.0.100) and still built and
Native AOT-published on win-x64. The measurements here and in `results/` were taken on .NET 10 (SDK 10.0.403) and were
not repeated.

`MafSpike` was a standalone project. It was not in `harness.slnx`, and empty `Directory.*` files kept the
repository's build rules, analyzers and central package versions out. It was published as trimmed Native AOT, like
harness. It referenced ClaudeCodeSdk.MAF 10.4.4 and CodexSdk.MAF 10.0.3, which bring Microsoft.Agents.AI 1.21.0.

**It starts real agent sessions**, which cost tokens.
- They run read-only: Claude in `plan` mode, Codex in a `read-only` sandbox with approval `never`.
- They run in the folder you pass. Use a scratch folder with one small file.
- Claude asks for `haiku`. Codex uses your configured model at low reasoning effort.

## Commands

| Command | What it does |
|---|---|
| `run <claude\|codex> <workdir> <task id>` | Starts a session for a task and runs one turn ("reply READY"). Passes `HARNESS_TASK_ID` in the environment. For Claude, it chooses the CLI session id up front (`SessionId` → `--session-id`). Prints identity (MAF session vs CLI session or thread), the answer, `UsageDetails`, what the raw messages are, and wall time. Saves the serialized MAF session and the CLI id under `state/`. |
| `resume <claude\|codex> <workdir>` | A new process deserializes the saved MAF session and asks what it answered before. Prints PASS or FAIL. |
| `resume-cli <workdir>` | Claude only: a new process resumes the CLI's own session id (`Resume` → `--resume`) instead. |
| `cancel <claude\|codex> <workdir> <task id>` | Streams a long turn and cancels at the first output. Prints how long cancellation took and which `claude`/`codex`/`node` processes are still alive 3 s later. |
| `interrupt <workdir>` | Claude only, ClaudeCodeSdk's core `ClaudeSdkClient`: `InterruptAsync` during a turn, then another turn in the same process (a "pause"). |

`size/` held two size probes:
- `Hello`: an empty program, as a baseline.
- `ClaudeCore`: one Claude turn through ClaudeCodeSdk's core client, without MAF. It prints the `ResultMessage`'s
  cost and session id, which the MAF layer drops.

```
# Windows: needs vswhere on PATH (the Visual Studio Installer folder)
dotnet publish -c Release -r win-x64 -o <out>
<out>/MafSpike run claude <workdir> TASK-SAMPLE
<out>/MafSpike resume claude <workdir>
<out>/MafSpike resume-cli <workdir>
<out>/MafSpike cancel claude <workdir> TASK-SAMPLE
<out>/MafSpike interrupt <workdir>
# Codex needs reflection-based System.Text.Json, so only a non-AOT build runs it:
dotnet build -c Release -p:PublishAot=false -p:JsonSerializerIsReflectionEnabledByDefault=true -o <jit>
<jit>/MafSpike run codex <workdir> TASK-SAMPLE
<jit>/MafSpike resume codex <workdir>
<jit>/MafSpike cancel codex <workdir> TASK-SAMPLE
dotnet publish size/ClaudeCore/ClaudeCore.csproj -c Release -r win-x64 -o <size-out>   # likewise size/Hello
```

**Machine**
- Windows 11 (10.0.26300).
- .NET SDK 10.0.403, ILCompiler 10.0.14.
- Claude Code 2.1.317, codex-cli 0.165.0.

## Results

| File | What it holds |
|---|---|
| [results/jit-win-x64.txt](results/jit-win-x64.txt) | Every scenario under JIT. Includes the failed MAF-session resume for Claude, the `--resume` pass, the interrupt failure, and Codex's run, resume and cancel (with reflection JSON on), with rollout timestamps. |
| [results/native-claude-codex-win-x64.txt](results/native-claude-codex-win-x64.txt) | The Native AOT executable: Claude run, MAF resume (FAIL), `--resume` (PASS) and cancel; Codex failing at its first event. |
| [results/publish-win-x64.txt](results/publish-win-x64.txt) | The 22 trim/AOT warnings of the publish (11 IL2026 + 11 IL3050), all in CodexSdk and CodexSdk.MAF. |
| [results/size-win-x64.txt](results/size-win-x64.txt) | Executable sizes of `Hello`, `ClaudeCore` and `MafSpike`, and `ClaudeCore`'s native run. |
| [results/packages.txt](results/packages.txt) | The resolved package graph, including transitive packages. |
