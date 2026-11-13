using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Agents;
using Harness.Core.Providers;

namespace Harness.Infrastructure.Agents;

/// <summary>
/// Asks an agent client's CLI for its models without starting a session or a turn (TASK-0142): Claude Code in print
/// mode with stream-json input gets the initialize control request (--no-session-persistence, so nothing is saved),
/// `codex app-server` gets initialize, initialized and model/list. The process runs in the given folder and is killed,
/// with its whole tree, once the answer is read or the time is up.
/// </summary>
public static class AgentModelProbe
{
    /// <summary>The longest a probe may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The client's report. Throws <see cref="InvalidOperationException"/> when the client answered with an error or
    /// not at all, <see cref="TimeoutException"/> when it took too long; a CLI that cannot be started throws what
    /// starting it threw.
    /// </summary>
    public static async Task<AgentModelReport> ProbeAsync(
        IProviderProcessFactory processes,
        string client,
        string executable,
        IReadOnlyList<string> prefix,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken token
    )
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(prefix);
        var codex = client is AgentClients.Codex;
        List<string> arguments = codex
            ? [.. prefix, "app-server"]
            :
            [
                .. prefix,
                "-p",
                "--input-format",
                "stream-json",
                "--output-format",
                "stream-json",
                "--verbose",
                "--no-session-persistence",
            ];
        var process = processes.Start(
            new ProviderProcessOptions(executable, arguments) { WorkingDirectory = workingDirectory }
        );
        await using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(timeout);
            // a pipe read does not always stop when cancelled: killing the tree closes it
            using var kill = limit.Token.Register(process.KillTree);
            var error = new StringBuilder();
            var drain = DrainAsync(process.Error, error);
            try
            {
                using var reader = new StreamReader(process.Output, new UTF8Encoding(false));
                var conversation = new Conversation(process, reader);
                return codex ? await CodexAsync(conversation) : await ClaudeAsync(conversation);
            }
            catch (InvalidOperationException exception) when (!limit.IsCancellationRequested)
            {
                await drain
                    .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                string tail;
                lock (error)
                {
                    tail = error.ToString().Trim().ReplaceLineEndings(" ");
                }
                if (tail.Length is 0)
                {
                    throw;
                }
                var shown = tail.Length <= 300 ? tail : tail[^300..];
                throw new InvalidOperationException(exception.Message + ": " + shown, exception);
            }
            catch (Exception exception) when (limit.IsCancellationRequested && exception is not TimeoutException)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"{client} did not tell its models within {timeout.TotalSeconds:0} s",
                    exception
                );
            }
            finally
            {
                process.KillTree();
                await drain
                    .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private static async Task<AgentModelReport> ClaudeAsync(Conversation conversation)
    {
        await conversation.WriteAsync(AgentModelProtocol.ClaudeInitialize());
        while (await conversation.ReadAsync() is { } line)
        {
            if (
                AgentModelCatalogs.Text(line["type"]) is "control_response"
                && AgentModelCatalogs.Text(line["response"]?["request_id"]) is AgentModelProtocol.ClaudeInitializeId
            )
            {
                return AgentModelProtocol.Claude(line)
                    ?? throw new InvalidOperationException(
                        "claude-code: " + AgentModelCatalogs.Text(line["response"]?["error"])
                    );
            }
        }
        throw new InvalidOperationException("claude-code ended without answering initialize");
    }

    private static async Task<AgentModelReport> CodexAsync(Conversation conversation)
    {
        var initialized = await conversation.CallAsync(
            1,
            "initialize",
            new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "harness-models", ["version"] = "1.0.0" },
            }
        );
        await conversation.WriteAsync(new() { ["jsonrpc"] = "2.0", ["method"] = "initialized" });
        List<AgentModel> models = [];
        var cursor = "";
        for (var page = 0; page < AgentModelProtocol.CodexPages; page++)
        {
            var (found, next) = AgentModelProtocol.Codex(
                await conversation.CallAsync(2 + page, "model/list", AgentModelProtocol.CodexListParams(cursor))
            );
            models.AddRange(found);
            if (next.Length is 0)
            {
                break;
            }
            cursor = next;
        }
        return new(models) { Version = AgentModelProtocol.CodexVersion(initialized) };
    }

    private static async Task DrainAsync(Stream stream, StringBuilder error)
    {
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            var buffer = new char[1024];
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                lock (error)
                {
                    error.Append(buffer, 0, read);
                    if (error.Length > 4096)
                    {
                        error.Remove(0, error.Length - 4096);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The process is gone.
        }
    }

    /// <summary>JSON lines both ways with the client's process.</summary>
    private sealed class Conversation(IProviderProcess process, StreamReader reader)
    {
        public async Task WriteAsync(JsonObject message)
        {
            await process.Input.WriteAsync(new UTF8Encoding(false).GetBytes(message.ToJsonString() + "\n"));
            await process.Input.FlushAsync();
        }

        /// <summary>The next JSON object line, or null once the output ends.</summary>
        public async Task<JsonObject?> ReadAsync()
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (!line.TrimStart().StartsWith('{', StringComparison.Ordinal))
                {
                    continue;
                }
                try
                {
                    if (JsonNode.Parse(line) is JsonObject json)
                    {
                        return json;
                    }
                }
                catch (JsonException)
                {
                    // Not a protocol line.
                }
            }
            return null;
        }

        /// <summary>A JSON-RPC call: its result, skipping notifications; an error answer throws.</summary>
        public async Task<JsonObject> CallAsync(long id, string method, JsonObject parameters)
        {
            await WriteAsync(
                new()
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["method"] = method,
                    ["params"] = parameters,
                }
            );
            while (await ReadAsync() is { } line)
            {
                if (
                    line["method"] is not null
                    || line["id"] is not JsonValue value
                    || !value.TryGetValue<long>(out var answered)
                    || answered != id
                )
                {
                    continue;
                }
                if (line["error"] is JsonObject error)
                {
                    throw new InvalidOperationException(
                        "codex " + method + ": " + AgentModelCatalogs.Text(error["message"])
                    );
                }
                return line["result"] as JsonObject ?? [];
            }
            throw new InvalidOperationException("codex ended without answering " + method);
        }
    }
}
