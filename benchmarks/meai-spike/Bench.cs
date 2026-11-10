using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MeaiSpike;

/// <summary>
/// One provider process, one SDK session, the same tool call three ways, alternating order on every round:
/// <list type="bullet">
/// <item><c>current</c>: what harness does today (McpProcessClient.CallAsync + McpMapping.Text/Body): a per-call
/// deadline, <c>McpClient.CallToolAsync</c>, the text blocks joined and parsed when they are JSON.</item>
/// <item><c>aifunction</c>: the same call through <c>McpClientTool</c> as an M.E.AI <c>AIFunction</c>
/// (<c>InvokeAsync(AIFunctionArguments)</c>), its <c>object?</c> answer turned back into text and parsed.</item>
/// <item><c>middleware</c>: the AIFunction wrapped in a <c>DelegatingAIFunction</c> that applies the deadline, as an
/// M.E.AI-style provider pipeline would.</item>
/// </list>
/// Latency is wall time per call; allocations are <c>GC.GetTotalAllocatedBytes(true)</c> deltas, so they include the
/// SDK's transport threads (reading and parsing the response), which is the honest per-call cost.
/// </summary>
internal static class Bench
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(string[] args)
    {
        // bench <rounds> <tool> <arguments json> fake <bytes> | -- <command> [args...]
        var rounds = int.Parse(args[0], CultureInfo.InvariantCulture);
        var toolName = args[1];
        var arguments = Arguments(args[2]);
        var (command, commandArgs, label) = Server(args[3..]);

        var started = Stopwatch.GetTimestamp();
        await using var client = await Connect(command, commandArgs).ConfigureAwait(false);
        var connect = Stopwatch.GetElapsedTime(started);
        started = Stopwatch.GetTimestamp();
        var listed = await client.ListToolsAsync().ConfigureAwait(false);
        var list = Stopwatch.GetElapsedTime(started);
        var tool =
            listed.FirstOrDefault(candidate => candidate.Name == toolName)
            ?? throw new InvalidOperationException("no tool " + toolName);
        var middleware = new DeadlineFunction(tool, Deadline);

        var methods = new (string Name, Func<Task<int>> Call)[]
        {
            ("current", () => Current(client, toolName, arguments)),
            ("aifunction", () => ViaFunction(tool, arguments, deadline: true)),
            ("middleware", () => ViaFunction(middleware, arguments, deadline: false)),
        };
        var answer = await tool.InvokeAsync(new AIFunctionArguments(arguments)).ConfigureAwait(false);
        Console.WriteLine(
            $"server: {label} ({client.ServerInfo?.Name} {client.ServerInfo?.Version}); tool {toolName} {args[2]}"
        );
        Console.WriteLine(
            $"connect {connect.TotalMilliseconds:F1} ms, tools/list {list.TotalMilliseconds:F1} ms ({listed.Count} tools); "
                + $"AIFunction answer type: {answer?.GetType().FullName ?? "null"}"
        );

        var warmup = Math.Max(5, rounds / 10);
        var latencies = methods.ToDictionary(method => method.Name, _ => new List<double>());
        var allocations = methods.ToDictionary(method => method.Name, _ => 0L);
        var sizes = methods.ToDictionary(method => method.Name, _ => 0);
        for (var round = 0; round < warmup + rounds; round++)
        {
            for (var i = 0; i < methods.Length; i++)
            {
                var (name, call) = methods[(round + i) % methods.Length];
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var start = Stopwatch.GetTimestamp();
                var size = await call().ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(start);
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                if (round < warmup)
                {
                    continue;
                }
                latencies[name].Add(elapsed.TotalMicroseconds);
                allocations[name] += allocated;
                sizes[name] = size;
            }
        }

        Console.WriteLine($"rounds {rounds} (after {warmup} warm-up), {Environment.ProcessorCount} CPUs");
        Console.WriteLine("method      | median us |   p90 us |  mean us | alloc B/call | answer chars");
        foreach (var (name, _) in methods)
        {
            var sorted = latencies[name].Order().ToArray();
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name, -11} | {sorted[sorted.Length / 2], 9:F0} | {sorted[(int)(sorted.Length * 0.9)], 8:F0} | {sorted.Average(), 8:F0} | {allocations[name] / rounds, 12:N0} | {sizes[name], 12:N0}"
                )
            );
        }
        return 0;
    }

    internal static async Task<McpClient> Connect(string command, IList<string> args) =>
        await McpClient
            .CreateAsync(
                new StdioClientTransport(
                    new StdioClientTransportOptions
                    {
                        Command = command,
                        Arguments = args,
                        WorkingDirectory = Environment.CurrentDirectory,
                        Name = Path.GetFileNameWithoutExtension(command),
                    }
                ),
                new McpClientOptions
                {
                    ClientInfo = new() { Name = "meai-spike", Version = "0.1.0" },
                    ProtocolVersion = "2025-11-25",
                }
            )
            .ConfigureAwait(false);

    internal static (string Command, IList<string> Args, string Label) Server(string[] args) =>
        args switch
        {
            ["fake", var bytes] => (
                Environment.ProcessPath!,
                ["serve", bytes],
                "fake MCP server, " + bytes + " B answer"
            ),
            ["--", var command, .. var rest] => (command, rest, Path.GetFileName(command)),
            _ => throw new ArgumentException("server: fake <bytes> | -- <command> [args...]"),
        };

    internal static Dictionary<string, object?> Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    /// <summary>harness today: McpProcessClient.CallAsync's deadline, then McpMapping.Text and McpMapping.Body.</summary>
    private static async Task<int> Current(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        var result = await client
            .CallToolAsync(tool, arguments, cancellationToken: deadline.Token)
            .AsTask()
            .WaitAsync(deadline.Token)
            .ConfigureAwait(false);
        return Map(string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
    }

    private static async Task<int> ViaFunction(
        AIFunction function,
        Dictionary<string, object?> arguments,
        bool deadline
    )
    {
        using var source = deadline ? new CancellationTokenSource(Deadline) : null;
        var answer = await function
            .InvokeAsync(new AIFunctionArguments(arguments), source?.Token ?? default)
            .ConfigureAwait(false);
        return Map(Text(answer));
    }

    /// <summary>What an AIFunction returns for an MCP tool, back to the text harness maps.</summary>
    internal static string Text(object? answer) =>
        answer switch
        {
            TextContent text => text.Text,
            IEnumerable<AIContent> contents => string.Concat(contents.OfType<TextContent>().Select(c => c.Text)),
            JsonElement { ValueKind: JsonValueKind.Object } element when element.TryGetProperty("content", out var c) =>
                string.Concat(
                    c.EnumerateArray()
                        .Where(block => block.TryGetProperty("text", out _))
                        .Select(block => block.GetProperty("text").GetString())
                ),
            JsonElement element => element.GetRawText(),
            null => "",
            _ => answer.ToString() ?? "",
        };

    /// <summary>McpMapping.Body for JSON answers; text answers go to a regex in harness and are only measured.</summary>
    private static int Map(string text)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length > 0 && trimmed[0] is '{' or '[')
        {
            using var document = JsonDocument.Parse(text);
            _ = document.RootElement.Clone();
        }
        return text.Length;
    }

    /// <summary>A provider-pipeline step in M.E.AI's style: a per-call deadline around any AIFunction.</summary>
    internal sealed class DeadlineFunction(AIFunction inner, TimeSpan deadline) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            source.CancelAfter(deadline);
            try
            {
                return await base.InvokeCoreAsync(arguments, source.Token)
                    .AsTask()
                    .WaitAsync(source.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(Name + ": timed out", error);
            }
        }
    }

    internal static string Describe(Exception error)
    {
        var text = new StringBuilder(error.GetType().Name).Append(": ").Append(error.Message);
        return text.ToString();
    }
}
