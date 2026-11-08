using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace MeaiSpike;

/// <summary>
/// Adding and combining providers in M.E.AI's terms, run under Native AOT. Each step prints PASS or FAIL.
/// <list type="number">
/// <item>fallback: a composite AIFunction asking providers in order until one answers (harness's combine "first" chain
/// and its miss handling).</item>
/// <item>union: two providers asked at once and their items merged (ProviderRegistry.CallAsync's fan-out).</item>
/// <item>llm: an IChatClient (scripted here, a real model later) given MCP tools through FunctionInvokingChatClient,
/// i.e. a model-backed provider that decides which tools to call.</item>
/// <item>embedding: an IEmbeddingGenerator (a hashing stand-in) reranking provider hits by cosine similarity.</item>
/// </list>
/// </summary>
internal static class Compose
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (command, commandArgs, label) = Bench.Server(args);
        await using var first = await Bench.Connect(command, commandArgs).ConfigureAwait(false);
        await using var second = await Bench.Connect(command, commandArgs).ConfigureAwait(false);
        var a = (await first.ListToolsAsync().ConfigureAwait(false)).ToDictionary(t => t.Name);
        var b = (await second.ListToolsAsync().ConfigureAwait(false)).ToDictionary(t => t.Name);
        Console.WriteLine("server: " + label);
        var failures = 0;
        var query = new Dictionary<string, object?> { ["pattern"] = "ExecuteAsync", ["limit"] = 20 };

        failures += await Step(
                "fallback: a missing answer from the first provider falls through to the second",
                async () =>
                {
                    var fallback = new FirstAnswer("code_text_search", [a["miss"], b["grep"]]);
                    var text = Bench.Text(await fallback.InvokeAsync(new(query)).ConfigureAwait(false));
                    return text.Contains("\"hits\"", StringComparison.Ordinal)
                        ? $"{text.Length} chars from grep"
                        : null;
                }
            )
            .ConfigureAwait(false);

        failures += await Step(
                "union: both providers asked at once, items merged",
                async () =>
                {
                    var answers = await Task.WhenAll(
                            new AIFunction[] { a["grep"], b["grep_b"] }.Select(f => f.InvokeAsync(new(query)).AsTask())
                        )
                        .ConfigureAwait(false);
                    var hits = answers.Sum(answer => Hits(Bench.Text(answer)).Count);
                    return hits > 0 ? $"{hits} items from {answers.Length} providers" : null;
                }
            )
            .ConfigureAwait(false);

        failures += await Step(
                "llm: FunctionInvokingChatClient lets a (scripted) model call an MCP tool",
                async () =>
                {
                    using var model = new ScriptedChatClient("grep", query);
                    using var client = new ChatClientBuilder(model).UseFunctionInvocation().Build();
                    var response = await client
                        .GetResponseAsync("where is ExecuteAsync used?", new ChatOptions { Tools = [a["grep"]] })
                        .ConfigureAwait(false);
                    return model.SawToolResult && response.Text.StartsWith("ranked", StringComparison.Ordinal)
                        ? response.Text
                        : null;
                }
            )
            .ConfigureAwait(false);

        failures += await Step(
                "embedding: IEmbeddingGenerator reranks provider hits",
                async () =>
                {
                    var hits = Hits(Bench.Text(await a["grep"].InvokeAsync(new(query)).ConfigureAwait(false)))
                        .Take(50)
                        .ToArray();
                    using var generator = new HashingEmbeddingGenerator();
                    var vectors = await generator
                        .GenerateAsync(new[] { "provider execute" }.Concat(hits))
                        .ConfigureAwait(false);
                    var target = vectors[0].Vector;
                    var best = hits.Select(
                            (hit, i) =>
                                (hit, score: TensorPrimitives.CosineSimilarity(target.Span, vectors[i + 1].Vector.Span))
                        )
                        .MaxBy(pair => pair.score);
                    return hits.Length > 0 ? $"{hits.Length} hits, best {best.score:F2}" : null;
                }
            )
            .ConfigureAwait(false);

        Console.WriteLine(failures is 0 ? "all steps passed" : failures + " step(s) failed");
        return failures is 0 ? 0 : 1;
    }

    private static async Task<int> Step(string name, Func<Task<string?>> step)
    {
        try
        {
            var detail = await step().ConfigureAwait(false);
            Console.WriteLine((detail is null ? "FAIL " : "PASS ") + name + (detail is null ? "" : " — " + detail));
            return detail is null ? 1 : 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + name + " — " + Bench.Describe(error));
            return 1;
        }
    }

    private static List<string> Hits(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document
            .RootElement.GetProperty("hits")
            .EnumerateArray()
            .Select(hit => hit.GetProperty("file").GetString() + ":" + hit.GetProperty("in").GetString())
            .ToList();
    }

    /// <summary>combine "first": the first provider whose answer is not an error or a miss.</summary>
    private sealed class FirstAnswer(string name, IReadOnlyList<AIFunction> providers) : AIFunction
    {
        public override string Name => name;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            List<string> misses = [];
            foreach (var provider in providers)
            {
                var answer = await provider.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
                // an MCP error answer comes back as the serialized CallToolResult with isError
                if (
                    answer is JsonElement { ValueKind: JsonValueKind.Object } e
                    && e.TryGetProperty("isError", out var x)
                    && x.ValueKind is JsonValueKind.True
                )
                {
                    misses.Add(provider.Name + ": " + Bench.Text(answer));
                    continue;
                }
                return answer;
            }
            throw new InvalidOperationException("no answer: " + string.Join("; ", misses));
        }
    }

    /// <summary>A model that first asks for one tool call, then answers from the tool's result.</summary>
    private sealed class ScriptedChatClient(string tool, Dictionary<string, object?> arguments) : IChatClient
    {
        public bool SawToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var result = messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .LastOrDefault();
            if (result is null)
            {
                return Task.FromResult(
                    new ChatResponse(
                        new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", tool, arguments)])
                    )
                );
            }
            SawToolResult = true;
            var text = Bench.Text(result.Result);
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, $"ranked {text.Length} chars"))
            );
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            foreach (
                var update in (
                    await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false)
                ).ToChatResponseUpdates()
            )
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>A stand-in embedding model: character trigrams hashed into 256 dimensions.</summary>
    private sealed class HashingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
            {
                var vector = new float[256];
                var text = value.ToLowerInvariant();
                for (var i = 0; i + 3 <= text.Length; i++)
                {
                    vector[(uint)string.GetHashCode(text.AsSpan(i, 3), StringComparison.Ordinal) % 256] += 1;
                }
                embeddings.Add(new Embedding<float>(vector));
            }
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
