using System.Diagnostics.CodeAnalysis;
using System.Text;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Xunit;

namespace Harness.Unit.Retrieval;

public sealed class RetrievalTests
{
    [Theory]
    [InlineData("fix TimetableOps.Copy and OrderService", "TimetableOps.Copy,OrderService")]
    [InlineData("change order_template then HTTP and API and HAR-0143", "order_template")]
    [InlineData("Fix the Parser today. Ignore the sentence.", "Parser")]
    public void SymbolsPreferCodeAndExcludeProseNoise(string text, string expected) =>
        TaskParsing.Symbols(text).Should().Equal(expected.Split(','));

    [Fact]
    public void PhrasesAndNamingVariantsFollowSourceRules()
    {
        TaskParsing
            .Phrases("copy order templates with validation rules")
            .Should()
            .Equal("CopyOrder", "OrderTemplate", "ValidationRule");
        TaskParsing.NamingVariants("order_template").Should().Equal("orderTemplate", "OrderTemplate");
        TaskParsing.NamingVariants("MAX_SIZE").Should().BeEmpty();
        TaskParsing.ContentWords("Change order templates using order rules").Should().Be("order templates rules");
    }

    [Theory]
    [InlineData("src/Latest.cs", false)]
    [InlineData("src/FooTests.cs", true)]
    [InlineData("test/Foo.cs", true)]
    [InlineData("src/foo.spec.ts", true)]
    [InlineData("src/test_foo.py", true)]
    public void RecognizesTestFiles(string path, bool expected) => TaskParsing.IsTestFile(path).Should().Be(expected);

    [Fact]
    public void NormalizesIdentityPositionEvidenceAndUtf8Sizes()
    {
        var item = new ProviderItem
        {
            Name = "Type.Copy",
            Path = "src/A.cs",
            StartLine = 4,
            EndLine = 7,
            Provider = "graph",
            Tool = "symbols",
            Score = 0.99,
        };
        var definition = Candidate.FromItem(new(Capabilities.SymbolSearch, "graph"), 7, item);
        definition.Id.Should().Be("code:src/A.cs:4");
        definition.Size.Should().Be(180);
        definition.SizeEstimated.Should().BeTrue();
        definition.Scores["rank"].Should().Be(0.125);
        definition.Freshness.Should().Be("live");
        Candidate.FromItem(new(Capabilities.Callers, "graph"), 0, item).Id.Should().Be("code:src/A.cs#Copy");
        Candidate.FromItem(new(Capabilities.TextSearch, "graph"), 0, item with { Snippet = "é😀" }).Size.Should().Be(6);
    }

    [Fact]
    public void MergeCombinesReportsAndFoldsOnlyContainedSingleLinesWithoutMutatingInputs()
    {
        var definition = Code("a", "definition", 10, 20) with { Size = 45, SizeEstimated = true };
        var repeated = definition with
        {
            Text = "body",
            Size = 4,
            SizeEstimated = false,
            Evidence = "exact",
            Provenance = [new("second", "s", Capabilities.SymbolSearch)],
            Scores = new() { ["name"] = 1 },
        };
        var line = Code("line", "match", 12, 12) with
        {
            Evidence = "exact",
            Provenance = [new("rg", "search", Capabilities.TextSearch)],
        };
        var ownSpan = Code("span", "caller", 13, 14);
        var merged = RetrievalMerger.Merge([definition, repeated, line, ownSpan]);
        merged.Candidates.Select(candidate => candidate.Id).Should().Equal("a", "span");
        var first = merged.Candidates[0];
        first.Text.Should().Be("body");
        first.Size.Should().Be(4);
        first.Sources.Should().Be(3);
        first.Evidence.Should().Be("exact");
        merged.Folded.Should().HaveCount(2);
        definition.Scores.Should().NotContainKey("name");
        definition.Provenance.Should().ContainSingle();
        definition.Text.Should().BeEmpty();
    }

    [Fact]
    [SuppressMessage(
        "Security",
        "CA5394:Do not use insecure randomness",
        Justification = "Seeded permutation verifies deterministic ranking; no security use."
    )]
    public void RankingKeepsNamedDefinitionsFirstAndUsesUtf8StableTies()
    {
        var named = Code("named", "definition") with
        {
            Evidence = "inferred",
            Scores = new() { ["name"] = 1, ["rank"] = 0.125 },
        };
        Candidate[] input =
        [
            Code("\ue000", "definition"),
            Code("😀", "definition"),
            named,
            Code("caller", "caller"),
            Code("deep", "impact") with
            {
                Depth = 3,
            },
        ];
        var expected = RetrievalRanker.Rank(input).Select(item => item.Candidate.Id).ToArray();
        expected.Should().Equal("named", "\ue000", "😀", "caller", "deep");
        var random = new Random(17);
        for (var i = 0; i < 50; i++)
        {
            random.Shuffle(input);
            RetrievalRanker.Rank(input).Select(item => item.Candidate.Id).Should().Equal(expected);
        }
        RetrievalRanker.Score(named).Explain().Should().Contain("named in the task (tier 0)");
    }

    [Fact]
    public void RankingExplainsPhraseFilePromotionAndDiscounts()
    {
        var file = Code("file", "file") with
        {
            Path = "doc/OrderTemplate.json",
            Scores = new() { ["rank"] = 1, ["phrase"] = 1 },
        };
        RetrievalRanker.Score(file).Score.Should().Be(1.5);
        var loose = Code("loose", "definition") with
        {
            Path = "doc/guide.md",
            Scores = new()
            {
                ["rank"] = 1,
                ["loose"] = 1,
                ["test"] = 1,
            },
            Evidence = "inferred",
        };
        RetrievalRanker.Score(loose).Score.Should().BeApproximately(0.144, 0.000001);
        var corroborated = Code("many", "definition") with
        {
            Provenance = Enumerable
                .Range(0, 8)
                .Select(i => new Provenance(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "", ""))
                .ToArray(),
        };
        RetrievalRanker.Score(corroborated).Score.Should().Be(1.3);
    }

    [Fact]
    public async Task FileCacheSharesConcurrentReadsAndErrorsWithinOneRequest()
    {
        var loads = 0;
        var cache = new FileReadCache(
            async (path, token) =>
            {
                Interlocked.Increment(ref loads);
                await Task.Delay(10, token);
                return Encoding.UTF8.GetBytes("first\r\né😀\nlast");
            }
        );
        var reads = await Task.WhenAll(
            Enumerable
                .Range(0, 20)
                .Select(_ => cache.ReadBodyAsync("a.cs", 2, 99, TestContext.Current.CancellationToken))
        );
        reads.Should().OnlyContain(body => body == "é😀\nlast");
        loads.Should().Be(1);
        (await cache.ReadBodyAsync("a.cs", 1, 1, TestContext.Current.CancellationToken)).Should().Be("first\r");
        await FluentActions.Awaiting(() => cache.ReadBodyAsync("a.cs", 0, 1)).Should().ThrowAsync<IOException>();
        var failures = 0;
        var broken = new FileReadCache(
            (_, _) =>
            {
                failures++;
                throw new IOException("missing");
            }
        );
        for (var i = 0; i < 2; i++)
        {
            await FluentActions
                .Awaiting(() => broken.ReadBodyAsync("missing", 1, 1))
                .Should()
                .ThrowAsync<IOException>();
        }

        failures.Should().Be(1);
    }

    [Fact]
    public async Task FileCacheRejectsInvalidUtf8AndRequestByteOverflow()
    {
        var invalid = new FileReadCache((_, _) => Task.FromResult(new byte[] { 0xff }));
        await FluentActions
            .Awaiting(() => invalid.ReadBodyAsync("a", 1, 1))
            .Should()
            .ThrowAsync<DecoderFallbackException>();
        var bounded = new FileReadCache((_, _) => Task.FromResult("abcd"u8.ToArray()), maximumBytes: 6);
        await bounded.ReadBodyAsync("a", 1, 1, TestContext.Current.CancellationToken);
        await FluentActions
            .Awaiting(() => bounded.ReadBodyAsync("b", 1, 1))
            .Should()
            .ThrowAsync<InvalidDataException>();
    }

    internal static Candidate Code(string id, string role, int start = 0, int end = 0) =>
        new()
        {
            Id = id,
            Source = "code",
            Role = role,
            Name = id,
            Path = "src/A.cs",
            StartLine = start,
            EndLine = end,
            Evidence = "exact",
            Provenance = [new("first", "symbols", Capabilities.SymbolSearch)],
        };
}
