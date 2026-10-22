using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Unit.Retrieval;

public sealed class GoContractTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Theory]
    [InlineData("")]
    [InlineData("fix TimetableOps.Copy and order_template")]
    [InlineData("copy order templates with validation rules")]
    [InlineData("Improve the Parser but ignore API and HAR-0143 and e.g. examples")]
    [InlineData("test OrderService and OrderService.Copy")]
    [InlineData("Fix FooBar foo_bar Alpha.Beta Gamma:Delta and MAX_SIZE")]
    [InlineData("order templates")]
    [InlineData("tests in test_order, specs and testing")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Fixture providers transfer ownership to the disposed registry."
    )]
    [SuppressMessage(
        "Security",
        "S4036:Searching OS commands in PATH is security-sensitive",
        Justification = "The explicitly configured frozen test oracle executes without a shell."
    )]
    public async Task NormalizationGatheringMergeAndRankingMatchFrozenGo(string text)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_RETRIEVAL_ORACLE");
        if (string.IsNullOrEmpty(oracle))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_RETRIEVAL_ORACLE using Retrieval/build_oracle.py.");
        }

        var token = TestContext.Current.CancellationToken;
        var items = new[]
        {
            Capabilities.SymbolSearch,
            Capabilities.TextSearch,
            Capabilities.FileSearch,
            Capabilities.Callers,
            Capabilities.References,
            Capabilities.Impact,
            Capabilities.Flow,
            Capabilities.Context,
            "custom.capability",
        }
            .Select(
                (capability, i) =>
                    new ItemInput(
                        new(capability, "graph", Evidence: i % 2 is 0 ? "exact" : null),
                        i,
                        new()
                        {
                            Name = "Type.Copy",
                            QualifiedName = "NS.Type.Copy",
                            Path = i % 3 is 0 ? "" : "src/A.cs",
                            StartLine = 10,
                            EndLine = 20,
                            Snippet = i % 2 is 0 ? "é😀" : null,
                            Provider = "graph",
                            Tool = "search",
                            Depth = i % 4,
                        }
                    )
            )
            .ToArray();
        Candidate[] candidates =
        [
            RetrievalTests.Code("span", "definition", 10, 20) with
            {
                Scores = new() { ["rank"] = 0.125, ["name"] = 1 },
                Evidence = "inferred",
            },
            RetrievalTests.Code("span", "definition", 10, 20) with
            {
                Text = "known",
                Size = 5,
                Provenance = [new("second", "def", Capabilities.SymbolSearch, 4)],
                Scores = new() { ["rank"] = 0.5 },
            },
            RetrievalTests.Code("line", "match", 15, 15),
            RetrievalTests.Code("own", "caller", 16, 17),
            RetrievalTests.Code("doc", "definition") with
            {
                Path = "doc/A.MD",
                Scores = new()
                {
                    ["rank"] = 0.3,
                    ["loose"] = 1,
                    ["test"] = 1,
                },
                Evidence = "inferred",
            },
            RetrievalTests.Code("file", "file") with
            {
                Path = "data/A.json",
                Scores = new() { ["phrase"] = 1, ["rank"] = 1 },
            },
            RetrievalTests.Code("deep", "impact") with
            {
                Depth = 3,
            },
            new()
            {
                Id = "memory:m",
                Source = "memory",
                Role = "decision",
                Scores = new() { ["scope"] = 0.8 },
                Evidence = "exact",
            },
            new()
            {
                Id = "git:c:a",
                Source = "git",
                Role = "change",
                Scores = new() { ["recency"] = 0.5 },
                Evidence = "exact",
            },
            RetrievalTests.Code("\ue000", "unknown"),
            RetrievalTests.Code("😀", "unknown"),
        ];
        var answers = new Dictionary<string, IReadOnlyList<ProviderItem>>(StringComparer.Ordinal)
        {
            [Capabilities.SymbolSearch + "|" + text] =
            [
                new()
                {
                    Name = "Loose",
                    Path = "doc/README.md",
                    StartLine = 2,
                },
            ],
            [Capabilities.SymbolSearch + "|TimetableOps.Copy"] =
            [
                new()
                {
                    Name = "Copy",
                    QualifiedName = "TimetableOps.Copy",
                    Path = "src/TimetableOps.cs",
                    StartLine = 40,
                },
                new()
                {
                    Name = "Copy",
                    QualifiedName = "Other.Copy",
                    Path = "src/Other.cs",
                    StartLine = 3,
                },
            ],
            [Capabilities.SymbolSearch + "|orderTemplate"] = [new() { Name = "orderTemplate", Path = "src/order.cs" }],
            [Capabilities.SymbolSearch + "|OrderService"] =
            [
                new()
                {
                    Name = "OrderService",
                    Path = "test/ServiceTests.cs",
                    StartLine = 1,
                },
            ],
            [Capabilities.Callers + "|TimetableOps.Copy"] =
            [
                new()
                {
                    Name = "Caller",
                    Path = "src/Caller.cs",
                    StartLine = 9,
                },
            ],
            [Capabilities.Impact + "|TimetableOps.Copy"] =
            [
                new()
                {
                    Name = "Far",
                    Path = "src/Far.cs",
                    Depth = 3,
                },
            ],
            [Capabilities.FileSearch + "|template"] = [new() { Path = "src/template.json" }],
            [Capabilities.SymbolSearch + "|OrderTemplate"] = Enumerable
                .Range(0, 4)
                .Select(i => new ProviderItem { Name = "OrderTemplate", Path = $"src/{i}.cs" })
                .ToArray(),
        };
        var providers = new[]
        {
            new
            {
                ID = "graph",
                Answers = answers,
                Failure = "",
                Excluded = false,
            },
            new
            {
                ID = "offline",
                Answers = new Dictionary<string, IReadOnlyList<ProviderItem>>(StringComparer.Ordinal),
                Failure = "offline",
                Excluded = false,
            },
            new
            {
                ID = "excluded",
                Answers = answers,
                Failure = "",
                Excluded = true,
            },
        };
        await using var registry = new ProviderRegistry();
        foreach (var provider in providers)
        {
            registry.Register(
                new GatheringTests.Fake(
                    provider.ID,
                    request =>
                        provider.Failure.Length > 0
                            ? throw new IOException(provider.Failure)
                            : provider.Answers.GetValueOrDefault(request.Capability + "|" + request.Query) ?? []
                )
            );
            if (provider.Excluded)
            {
                registry.ExcludeFromContext(provider.ID);
            }
        }
        var normalized = items.Select(item => Candidate.FromItem(item.Info, item.Rank, item.Item)).ToArray();
        var merged = RetrievalMerger.Merge(candidates.Concat(normalized));
        var gathered = await RetrievalGatherer.GatherAsync(new(registry), new(text), token);
        var actual = JsonSerializer.SerializeToNode(
            new
            {
                normalized = normalized.Select(Wire),
                merged = merged.Candidates.Select(Wire),
                folded = merged.Folded.Select(fold => new
                {
                    candidate = Wire(fold.Candidate),
                    fold.Into,
                    fold.Reason,
                }),
                ranked = RetrievalRanker
                    .Rank(merged.Candidates)
                    .Select(rank => new
                    {
                        candidate = Wire(rank.Candidate),
                        rank.Tier,
                        rank.Score,
                        rank.Why,
                    }),
                gathered = new
                {
                    candidates = gathered.Candidates.Select(Wire),
                    gathered.Symbols,
                    gathered.Phrases,
                    gathered.Errors,
                    gathered.Notes,
                    calls = gathered.Calls.Select(call => new
                    {
                        call.Provider,
                        call.Capability,
                        call.Query,
                        call.Items,
                        call.Bytes,
                        call.Error,
                    }),
                },
            },
            Json
        );
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
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.StandardInput.WriteAsync(
                JsonSerializer
                    .Serialize(
                        new
                        {
                            text,
                            candidates,
                            items,
                            providers,
                        },
                        Json
                    )
                    .AsMemory(),
                token
            );
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
            process.ExitCode.Should().Be(0, await stderr);
            var expected = JsonNode.Parse(await stdout);
            JsonNode.DeepEquals(actual, expected).Should().BeTrue("C# {0} must equal frozen Go {1}", actual, expected);
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

    private sealed record ItemInput(CapabilityInfo Info, int Rank, ProviderItem Item);

    private static object Wire(Candidate candidate) =>
        new
        {
            candidate.Id,
            candidate.Source,
            candidate.Role,
            candidate.Name,
            candidate.QualifiedName,
            candidate.Path,
            candidate.StartLine,
            candidate.EndLine,
            candidate.Text,
            candidate.Size,
            candidate.SizeEstimated,
            candidate.Depth,
            candidate.Scores,
            candidate.Evidence,
            candidate.Freshness,
            when = candidate.When?.ToRfc3339Nano() ?? "0001-01-01T00:00:00Z",
            candidate.Provenance,
            candidate.MemoryScope,
            candidate.Overrides,
        };
}
