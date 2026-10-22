using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Harness.Mcp.Context;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Unit.Retrieval;

public sealed class ContextBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(1000)]
    [InlineData(2000)]
    [InlineData(4000)]
    public async Task PackingMatchesOriginalGoBudgetFixtures(int bytes)
    {
        var statement = "A durable statement already visible in the definition body, never repeated";
        var candidates = new List<Candidate>
        {
            Code("Small", "a.cs", 1, 10, true),
            Code("Other", "b.cs", 1, 10),
            Code("Huge", "big.cs", 1, 300),
            Code("Named", "named.cs", 1, 300, true),
            new()
            {
                Id = "memory:1",
                Source = "memory",
                Role = "decision",
                Text = statement,
                Evidence = "exact",
                Scores = new() { ["scope"] = 1 },
            },
        };
        for (var i = 0; i < 50; i++)
        {
            candidates.Add(Code("LongSymbol" + i, "some/deeply/nested/path/File.cs", i + 1, i + 1));
        }

        var bodies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a.cs"] = statement + "\n" + Lines(8, "x"),
            ["b.cs"] = Lines(10, "x"),
            ["big.cs"] = Lines(300, "x"),
            ["named.cs"] = Lines(300, "é😀\"\\\t"),
        };
        var budget = new ContextBudget(bytes);
        var packed = await ContextPacker.PackAsync(
            RetrievalRanker.Rank(RetrievalMerger.Merge(candidates).Candidates),
            budget,
            (path, _, _, _) =>
                bodies.TryGetValue(path, out var body) ? Task.FromResult(body) : throw new IOException("missing"),
            Token
        );
        var actual = JsonSerializer.SerializeToNode(
            new
            {
                units = packed.Units.Select(unit => new
                {
                    id = unit.Ranked.Candidate.Id,
                    tier = unit.Ranked.Tier,
                    score = unit.Ranked.Score,
                    why = unit.Ranked.Why,
                    body = unit.Body,
                    clipped = unit.Clipped,
                    reference = unit.Reference,
                    cost = unit.Cost,
                    bodyNote = unit.BodyNote,
                }),
                rejected = packed.Rejected.Select(item => item.Candidate.Id),
                used = packed.Used,
                omitted = packed.Omitted,
                overrun = packed.Overrun,
                note = packed.Note,
                budget = new
                {
                    bytes = packed.Budget.Bytes,
                    softShare = packed.Budget.SoftShare,
                    maxBody = packed.Budget.MaxBody,
                },
            }
        );
        var expected = await OracleAsync(
            new
            {
                mode = "pack",
                candidates,
                budget,
                bodies,
            }
        );
        JsonNode.DeepEquals(actual, expected).Should().BeTrue("C# {0} equals Go {1}", actual, expected);
        packed.Used.Should().BeLessThanOrEqualTo(packed.Budget.Bytes);
        packed.Units.Sum(unit => unit.Cost).Should().Be(packed.Used);
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(2000, false)]
    [InlineData(5000, false)]
    [InlineData(12000, false)]
    [InlineData(12000, true)]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Registry owns the fixture provider."
    )]
    public async Task RenderedUnicodeAndEscapingMatchOriginalHandler(int budget, bool debug)
    {
        var items = Enumerable
            .Range(0, 40)
            .Select(i => new ProviderItem
            {
                Name = "Unit" + i,
                Path = $"Area{i % 3}/Définition😀{i / 2}.cs",
                StartLine = 1 + i % 2 * 20,
                Snippet = string.Concat(Enumerable.Repeat("é😀 <tag>& \"quoted\"\tline\n\u2028\u2029\\\b\f\r", 8)),
                Provider = "fixture",
                Tool = "search",
            })
            .Take(debug ? 2 : 40)
            .ToArray();
        const string Task = "change Unit3 and TimetableOps.Copy";
        await using var registry = new ProviderRegistry();
        registry.Register(new FixtureProvider(items));
        var gathered = await RetrievalGatherer.GatherAsync(new(registry), new(Task), Token);
        var context = new ContextProject(new(), Notes: ["no project memory (run harness init to record decisions)"]);
        var answer = await ContextRenderer.RenderAsync(new(Task, budget, Debug: debug), gathered, context, Token);
        var actual = JsonNode.Parse(answer.Json)!;
        var expected = await OracleAsync(
            new
            {
                mode = "render",
                items,
                bodies = new Dictionary<string, string>(StringComparer.Ordinal),
                request = new
                {
                    task = Task,
                    budget,
                    debug,
                },
            }
        );
        AssertBudget(answer, budget);
        // Go measures before adding debug, and real provider latency is nondeterministic.
        if (debug)
        {
            actual.AsObject().Remove("used_bytes");
            expected!.AsObject().Remove("used_bytes");
            foreach (var value in new[] { actual, expected })
            {
                foreach (var call in value["debug"]!["calls"]!.AsArray())
                {
                    call!.AsObject().Remove("latency_ms");
                }
            }
        }
        JsonNode.DeepEquals(actual, expected).Should().BeTrue("C# {0} equals Go {1}", actual, expected);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(2000)]
    [InlineData(8000)]
    public async Task DebugAndOversizedDiagnosticsAreInsideTheActualBudget(int budget)
    {
        var candidates = Enumerable
            .Range(0, 100)
            .Select(i =>
                Code("Unit" + i, "src/quoted\"😀.cs", i + 1, i + 1) with
                {
                    Text = Lines(10, "é😀\\\""),
                    Size = 700,
                }
            )
            .ToArray();
        var calls = Enumerable
            .Range(0, 60)
            .Select(i => new RetrievalCall("provider", Capabilities.SymbolSearch, "Unit" + i, 100, 0, 1000))
            .ToArray();
        var gathered = new Gathered(candidates, [], [], [new string('x', 10000)], [], calls);
        var answer = await ContextRenderer.RenderAsync(
            new("fix Unit3", budget, Debug: true),
            gathered,
            cancellationToken: Token
        );
        AssertBudget(answer, budget);
        var output = JsonNode.Parse(answer.Json)!;
        output["debug"].Should().NotBeNull();
        output["note"]!.GetValue<string>().Should().Contain("truncated");
        output["omitted"]!.GetValue<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public void TokensDefaultsAndGoStringEncodingAreExplicit()
    {
        ContextRenderer.BudgetBytes(new("x")).Should().Be(8000);
        ContextRenderer.BudgetBytes(new("x", 9000, 500)).Should().Be(2000);
        ContextRenderer.BudgetBytes(new("x", 120)).Should().Be(1000);
        ContextBudget.FromTokens(1000).Bytes.Should().Be(4000);
        var text = ContextJson.Serialize(
            new Harness.Core.Serialization.JsonObjectMap { ["text"] = "é😀 <>&\u2028\u2029\"\\\n" }
        );
        text.Should().Contain("é😀").And.Contain("\\u003c\\u003e\\u0026\\u2028\\u2029").And.NotContain("\\uD83D");
        ContextJson.Size(new Harness.Core.Serialization.JsonObjectMap { ["text"] = "😀" }).Should().Be(15);
    }

    [Fact]
    public async Task OversizedTaskAndCancellationFailExplicitly()
    {
        var empty = new Gathered([], [], [], [], [], []);
        await FluentActions
            .Awaiting(() =>
                ContextRenderer.RenderAsync(new(new string('é', 2000), 1000), empty, cancellationToken: Token)
            )
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*raise the budget*");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await FluentActions
            .Awaiting(() =>
                ContextPacker.PackAsync(
                    [RetrievalRanker.Score(Code("A", "a.cs", 1, 2))],
                    new(),
                    cancellationToken: cancellation.Token
                )
            )
            .Should()
            .ThrowAsync<OperationCanceledException>();
    }

    private static void AssertBudget(ContextAnswer answer, int budget)
    {
        var output = JsonNode.Parse(answer.Json)!;
        var size = Encoding.UTF8.GetByteCount(answer.Json);
        output["used_bytes"]!.GetValue<int>().Should().Be(size);
        size.Should().BeLessThanOrEqualTo(budget);
    }

    private static Candidate Code(string name, string path, int start, int end, bool named = false)
    {
        var candidate = Candidate.FromItem(
            new(Capabilities.SymbolSearch, "p", Evidence: "exact"),
            0,
            new()
            {
                Name = name,
                Path = path,
                StartLine = start,
                EndLine = end,
                Provider = "p",
            }
        );
        if (named)
        {
            candidate.Scores["name"] = 1;
        }

        return candidate;
    }

    private static string Lines(int count, string text) =>
        string.Concat(Enumerable.Repeat(string.Concat(Enumerable.Repeat(text, 39)) + "\n", count));

    [SuppressMessage(
        "Security",
        "S4036:Searching OS commands in PATH is security-sensitive",
        Justification = "Explicitly configured frozen test oracle; no command shell."
    )]
    private static async Task<JsonNode?> OracleAsync(object input)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_CONTEXT_ORACLE");
        if (string.IsNullOrEmpty(oracle))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_CONTEXT_ORACLE using Context/build_oracle.py.");
        }

        using var process = new Process
        {
            StartInfo = new(oracle)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.Start();
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(Token);
            var stderr = process.StandardError.ReadToEndAsync(Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input).AsMemory(), Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
            process.ExitCode.Should().Be(0, await stderr);
            return JsonNode.Parse(await stdout);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private sealed class FixtureProvider(IReadOnlyList<ProviderItem> items) : IProvider
    {
        public string Name => "fixture";

        public IReadOnlyList<CapabilityInfo> Capabilities() => [new(Core.Providers.Capabilities.SymbolSearch, Name)];

        public Task<ProviderResult> ExecuteAsync(
            ProviderRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ProviderResult { Items = items });

        public ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProviderHealth("ready"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
