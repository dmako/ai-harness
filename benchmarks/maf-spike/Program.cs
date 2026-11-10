// TASK-0112: can harness start, watch, cancel and resume Claude Code / Codex sessions per task through Microsoft
// Agent Framework? See README.md.
using System.Diagnostics;
using System.Text.Json;
using ClaudeCodeSdk;
using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.CodexSdk;
using OpenAI.CodexSdk.MAF;

var state = Path.Combine(AppContext.BaseDirectory, "state");
Directory.CreateDirectory(state);

return args switch
{
    ["run", var agent, var workdir, var task] => await Run(agent, workdir, task),
    ["cancel", var agent, var workdir, var task] => await Cancel(agent, workdir, task),
    ["resume", var agent, var workdir] => await Resume(agent, workdir),
    ["resume-cli", var workdir] => await ResumeCli(workdir),
    ["interrupt", var workdir] => await Interrupt(workdir),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine(
        """
        MafSpike run <claude|codex> <workdir> <task id>     start a session for a task, one turn; prints identity, usage, timing
        MafSpike cancel <claude|codex> <workdir> <task id>  a long streamed turn cancelled at its first output; are processes gone?
        MafSpike resume <claude|codex> <workdir>            a new process resumes the session saved by run; does it remember?
        MafSpike interrupt <workdir>                        Claude only, core SDK: interrupt a turn, then continue in the same process
        """
    );
    return 2;
}

// ---- run: start a session that harness can link to a task ------------------------------------------------------

async Task<int> Run(string kind, string workdir, string task)
{
    var claudeSession = Guid.NewGuid(); // harness chooses Claude's session id before starting it
    string? codexThread = null;
    var agent = Create(kind, workdir, task, claudeSession, id => codexThread = id);
    var session = await agent.CreateSessionAsync();
    var watch = Stopwatch.StartNew();
    var response = await agent.RunAsync("Reply with exactly the word READY and nothing else.", session);
    watch.Stop();
    var saved = await agent.SerializeSessionAsync(session);
    await File.WriteAllTextAsync(Path.Combine(state, kind + ".session.json"), saved.GetRawText());
    await File.WriteAllTextAsync(
        Path.Combine(state, kind + ".cli-session.txt"),
        kind is "claude" ? claudeSession.ToString() : codexThread ?? ""
    );
    Console.WriteLine($"agent {kind}, task {task}, wall {watch.Elapsed.TotalSeconds:F1} s");
    Console.WriteLine(
        "identity: "
            + (
                session switch
                {
                    ClaudeCodeAgentSession c =>
                        $"MAF session {c.SessionId}; CLI started with --session-id {claudeSession} (see the transcript file name)",
                    CodexAgentSession c => $"codex thread {c.ThreadId} (OnThreadStarted reported {codexThread})",
                    _ => session.GetType().Name,
                }
            )
    );
    Console.WriteLine("answer: " + response.Text.Trim());
    Report(response);
    Console.WriteLine("serialized session: " + saved.GetRawText());
    await Dispose(agent);
    return 0;
}

// ---- cancel: stop a running turn and check what is left running ---------------------------------------------------

async Task<int> Cancel(string kind, string workdir, string task)
{
    var before = AgentProcesses();
    var agent = Create(kind, workdir, task, Guid.NewGuid(), _ => { });
    var session = await agent.CreateSessionAsync();
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var watch = Stopwatch.StartNew();
    TimeSpan? first = null;
    TimeSpan? cancelled = null;
    var updates = 0;
    try
    {
        await foreach (
            var update in agent.RunStreamingAsync(
                "Count from 1 to 300, one number per line, then say DONE.",
                session,
                cancellationToken: cancel.Token
            )
        )
        {
            updates++;
            if (first is null && update.Text.Length > 0)
            {
                first = watch.Elapsed;
                Console.WriteLine(
                    $"first output after {first.Value.TotalSeconds:F1} s: {update.Text.Trim()[..Math.Min(20, update.Text.Trim().Length)]}…; live: {Describe(AgentProcesses().Except(before))}"
                );
                cancelled = watch.Elapsed;
                cancel.Cancel();
            }
        }
        Console.WriteLine("stream ended without cancellation after " + updates + " updates");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine(
            $"cancelled: RunStreamingAsync threw OperationCanceledException {(watch.Elapsed - cancelled!.Value).TotalMilliseconds:F0} ms after Cancel()"
        );
    }
    await Task.Delay(TimeSpan.FromSeconds(3));
    var left = AgentProcesses().Except(before).ToArray();
    Console.WriteLine($"3 s after cancel: {left.Length} agent process(es) still alive {Describe(left)}");
    await File.WriteAllTextAsync(
        Path.Combine(state, kind + ".cancelled.json"),
        (await agent.SerializeSessionAsync(session)).GetRawText()
    );
    await Dispose(agent);
    return 0;
}

// ---- resume: a new process continues a saved session -------------------------------------------------------------

async Task<int> Resume(string kind, string workdir)
{
    var file = Path.Combine(state, kind + ".session.json");
    using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(file));
    var agent = Create(kind, workdir, "", null, _ => { });
    var session = await agent.DeserializeSessionAsync(saved.RootElement);
    var watch = Stopwatch.StartNew();
    var response = await agent.RunAsync(
        "What single word did you reply with in your previous answer? Reply with that word only.",
        session
    );
    Console.WriteLine($"agent {kind}: resumed {saved.RootElement.GetRawText()} in {watch.Elapsed.TotalSeconds:F1} s");
    Console.WriteLine(
        "answer: "
            + response.Text.Trim()
            + (
                response.Text.Contains("READY", StringComparison.Ordinal)
                    ? "  (remembers: PASS)"
                    : "  (remembers: FAIL)"
            )
    );
    Report(response);
    await Dispose(agent);
    return 0;
}

// ---- resume-cli: Claude, a new process resumes the CLI's own session id (--resume), bypassing the MAF session -----

async Task<int> ResumeCli(string workdir)
{
    var id = (await File.ReadAllTextAsync(Path.Combine(state, "claude.cli-session.txt"))).Trim();
    var agent = new ClaudeCodeAIAgent(
        new ClaudeCodeAIAgentOptions
        {
            Model = "haiku",
            WorkingDirectory = workdir,
            PermissionMode = PermissionMode.plan,
            MaxTurns = 3,
            Resume = id,
        }
    );
    var session = await agent.CreateSessionAsync();
    var watch = Stopwatch.StartNew();
    var response = await agent.RunAsync(
        "What single word did you reply with in your previous answer? Reply with that word only.",
        session
    );
    Console.WriteLine($"agent claude: --resume {id} in {watch.Elapsed.TotalSeconds:F1} s");
    Console.WriteLine(
        "answer: "
            + response.Text.Trim()
            + (
                response.Text.Contains("READY", StringComparison.Ordinal)
                    ? "  (remembers: PASS)"
                    : "  (remembers: FAIL)"
            )
    );
    Report(response);
    await Dispose(agent);
    return 0;
}

// ---- interrupt: Claude's core SDK keeps the process and session while stopping a turn ("pause") -------------------

async Task<int> Interrupt(string workdir)
{
    await using var client = new ClaudeSdkClient(
        new ClaudeCodeOptions
        {
            Model = "haiku",
            WorkingDirectory = workdir,
            PermissionMode = PermissionMode.plan,
            MaxTurns = 3,
        }
    );
    var watch = Stopwatch.StartNew();
    await client.ConnectAsync();
    Console.WriteLine($"connected in {watch.Elapsed.TotalSeconds:F1} s");
    await client.QueryAsync("Count from 1 to 300, one number per line, then say DONE.");
    var interrupted = false;
    await foreach (var message in client.ReceiveResponseAsync())
    {
        switch (message)
        {
            case AssistantMessage when !interrupted:
                Console.WriteLine($"first assistant message after {watch.Elapsed.TotalSeconds:F1} s; interrupting");
                await client.InterruptAsync();
                interrupted = true;
                break;
            case ResultMessage result:
                Console.WriteLine(
                    $"turn ended: subtype {result.Subtype}, error {result.IsError}, session {result.SessionId}, cost ${result.TotalCostUsd:F4}"
                );
                break;
        }
    }
    await client.QueryAsync("Reply with exactly the word CONTINUED.");
    await foreach (var message in client.ReceiveResponseAsync())
    {
        if (message is ResultMessage result)
        {
            Console.WriteLine(
                $"same process, next turn: \"{result.Result?.Trim()}\" in {result.DurationMs} ms, {result.NumTurns} turn(s), cost ${result.TotalCostUsd:F4}, session {result.SessionId}"
            );
            Console.WriteLine(
                $"usage: in {result.Usage?.InputTokens}, out {result.Usage?.OutputTokens}, cache read {result.Usage?.CacheReadInputTokens}, cache write {result.Usage?.CacheCreationInputTokens}"
            );
        }
    }
    return 0;
}

// ---- helpers -----------------------------------------------------------------------------------------------------

static AIAgent Create(string kind, string workdir, string task, Guid? claudeSession, Action<string> threadStarted) =>
    kind switch
    {
        "claude" => new ClaudeCodeAIAgent(
            new ClaudeCodeAIAgentOptions
            {
                Model = "haiku",
                WorkingDirectory = workdir,
                PermissionMode = PermissionMode.plan, // read-only: the spike's tasks change nothing
                MaxTurns = 3,
                SessionId = claudeSession,
                // what a harness-started session would carry so hooks and MCP calls can name their task
                EnvironmentVariables = new() { ["HARNESS_TASK_ID"] = task },
            }
        ),
        "codex" => new CodexAIAgent(
            new CodexAIAgentOptions
            {
                CodexOptions = new() { Env = Environment(task) },
                ThreadOptions = new()
                {
                    WorkingDirectory = workdir,
                    SandboxMode = SandboxMode.ReadOnly,
                    ApprovalPolicy = ApprovalMode.Never,
                    SkipGitRepoCheck = true,
                    ModelReasoningEffort = ModelReasoningEffort.Low,
                },
                OnThreadStartedAsync = (id, _) =>
                {
                    threadStarted(id);
                    return ValueTask.CompletedTask;
                },
            }
        ),
        _ => throw new ArgumentException("agent: claude | codex"),
    };

// CodexOptions.Env replaces the environment, so keep the user's and add the task.
static Dictionary<string, string> Environment(string task)
{
    var env = System
        .Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
    env["HARNESS_TASK_ID"] = task;
    return env;
}

static void Report(AgentResponse response)
{
    var u = response.Usage;
    Console.WriteLine(
        u is null
            ? "usage: none reported"
            : $"usage: in {u.InputTokenCount}, out {u.OutputTokenCount}, total {u.TotalTokenCount}, cached in {u.CachedInputTokenCount}, reasoning {u.ReasoningTokenCount}, additional {(u.AdditionalCounts is null ? "-" : string.Join(", ", u.AdditionalCounts.Select(p => p.Key + "=" + p.Value)))}"
    );
    Console.WriteLine(
        $"raw: {response.RawRepresentation?.GetType().FullName ?? "null"}; properties: {(response.AdditionalProperties is null ? "-" : string.Join(", ", response.AdditionalProperties.Select(p => p.Key + "=" + p.Value)))}"
    );
    foreach (var message in response.Messages)
    {
        var raw = message.RawRepresentation;
        if (raw is ResultMessage result)
        {
            Console.WriteLine(
                $"claude result: cost ${result.TotalCostUsd:F4}, duration {result.DurationMs} ms (api {result.DurationApiMs} ms), turns {result.NumTurns}"
            );
        }
        else if (raw is not null)
        {
            Console.WriteLine("message raw: " + raw.GetType().FullName);
        }
    }
}

static async Task Dispose(AIAgent agent)
{
    switch (agent)
    {
        case IAsyncDisposable a:
            await a.DisposeAsync();
            break;
        case IDisposable d:
            d.Dispose();
            break;
    }
}

static HashSet<(int Id, string Name)> AgentProcesses() =>
    Process
        .GetProcesses()
        .Where(p => p.ProcessName is "claude" or "codex" or "node")
        .Select(p => (p.Id, p.ProcessName))
        .ToHashSet();

static string Describe(IEnumerable<(int Id, string Name)> processes) =>
    "[" + string.Join(", ", processes.Select(p => p.Name + ":" + p.Id)) + "]";
