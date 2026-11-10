// TASK-0111 size probes: one MCP tool call, compiled three ways (see README.md).
//   McpOnly        harness today: McpClient.CallToolAsync
//   McpAIFunction  the same call through McpClientTool as an M.E.AI AIFunction (Abstractions only)
//   McpMeai        plus Microsoft.Extensions.AI: FunctionInvokingChatClient and an IEmbeddingGenerator
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

await using var client = await McpClient.CreateAsync(
    new StdioClientTransport(new() { Command = args[0], Arguments = args[1..] }),
    new McpClientOptions { ProtocolVersion = "2025-11-25" } // as harness's McpProcessClient
);
var arguments = new Dictionary<string, object?> { ["pattern"] = "x", ["limit"] = 20 };
#if MCP_ONLY
var result = await client.CallToolAsync("grep", arguments);
Console.WriteLine(string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)).Length);
#else
var tool = (await client.ListToolsAsync()).First(t => t.Name == "grep");
var answer = await tool.InvokeAsync(new AIFunctionArguments(arguments));
Console.WriteLine(answer is TextContent text ? text.Text.Length : -1);
#endif
#if MEAI
using var chat = new ChatClientBuilder(new OneCall()).UseFunctionInvocation().Build();
Console.WriteLine((await chat.GetResponseAsync("go", new ChatOptions { Tools = [tool] })).Text);
using var embeddings = new Fixed();
Console.WriteLine((await embeddings.GenerateAsync(["a", "b"])).Count);

sealed class OneCall : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> m,
        ChatOptions? o = null,
        CancellationToken c = default
    ) =>
        Task.FromResult(
            m.Any(x => x.Contents.OfType<FunctionResultContent>().Any())
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
                : new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("1", "grep", new Dictionary<string, object?> { ["pattern"] = "x" })]
                    )
                )
        );

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> m,
        ChatOptions? o = null,
        CancellationToken c = default
    ) => throw new NotSupportedException();

    public object? GetService(Type t, object? k = null) => null;

    public void Dispose() { }
}

sealed class Fixed : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> v,
        EmbeddingGenerationOptions? o = null,
        CancellationToken c = default
    ) => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(v.Select(_ => new Embedding<float>(new float[4]))));

    public object? GetService(Type t, object? k = null) => null;

    public void Dispose() { }
}
#endif
