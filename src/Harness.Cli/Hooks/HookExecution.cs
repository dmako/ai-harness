using Harness.Core.Hooks;
using Harness.Infrastructure.Hooks;
using Harness.Infrastructure.Projects;

namespace Harness.Cli.Hooks;

internal static class HookExecution
{
    internal const string SessionContext = """
        This repository uses harness (MCP server "harness"; tools mcp__harness__*, load them with ToolSearch if deferred).
        - Start every task with get_context (the task in words, naming the symbols you know), then search_project (kind impact before changing a symbol), package_api and decisions_for, before Grep, Glob or greps through Bash.
        - When you delegate to a subagent, say in its prompt: "This repository uses harness: start with get_context and search with search_project before Grep or Bash greps."
        - If a harness answer is empty or fails, say so and fall back for that question; keep using harness for the next one.
        - AGENTS.md (or CLAUDE.md) holds the rest of the harness guidance: goals, decisions, tasks and memory.
        """;

    // This entry point precedes command parsing and host construction: malformed hook arguments,
    // closed input, cancellation and all best-effort failures must still produce exit zero.
    public static async Task<int> RunAsync(
        string[] args,
        Stream input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var operation = args[1];
        var client = "claude-code";
        var i = 2;
        while (i < args.Length)
        {
            if (args[i] is "--client" or "-client" && i + 1 < args.Length)
            {
                client = args[++i];
            }
            else if (
                args[i].StartsWith("--client=", StringComparison.Ordinal)
                || args[i].StartsWith("-client=", StringComparison.Ordinal)
            )
            {
                client = args[i][(args[i].IndexOf('=', StringComparison.Ordinal) + 1)..];
            }
            else
            {
                return 0;
            }
            i++;
        }
        try
        {
            // WaitAsync also bounds platform stdin implementations that do not cancel pending reads.
            var bytes = await ReadAsync(input, deadline.Token).WaitAsync(deadline.Token);
            var call = HookPayload.Parse(bytes);
            var ingestion = new HookIngestion(new ProjectLifecycle());
            if (operation is "session")
            {
                var fallback = HookPayload.NativePath(Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") ?? ".");
                var directory = call.Cwd.Length > 0 ? call.Cwd : fallback;
                await ingestion.LocateAsync(directory, deadline.Token);
                await output.WriteLineAsync(SessionContext);
            }
            else
            {
                await ingestion.RecordAsync(call, client, deadline.Token);
                await DebugAsync(error, "recorded");
            }
        }
        catch (Exception exception)
        {
            // Never print external payloads, filenames or exception text in hook diagnostics.
            await DebugAsync(
                error,
                exception is OperationCanceledException
                    ? "dropped: time budget exceeded"
                    : "dropped: " + exception.GetType().Name
            );
        }
        return 0;
    }

    private static Task DebugAsync(TextWriter error, string message) =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HARNESS_HOOK_DEBUG"))
            ? Task.CompletedTask
            : error.WriteLineAsync("harness hook event: " + message);

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read is 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > HookPayload.MaximumBytes)
            {
                throw new InvalidDataException("hook payload exceeds 1 MiB");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }
}
