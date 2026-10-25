using System.Text;
using AwesomeAssertions;
using Harness.Core.Memory;
using Xunit;

namespace Harness.Unit;

public sealed class MemoryTests
{
    [Theory]
    [InlineData("", "project")]
    [InlineData(" project ", "project")]
    [InlineData("user", "user")]
    [InlineData("path:internal/storage/", "path:internal/storage")]
    [InlineData("path:internal\\git", "path:internal/git")]
    [InlineData("path:./a/../b", "path:b")]
    [InlineData("subsystem:auth", "subsystem:auth")]
    [InlineData("branch:feature/x", "branch:feature/x")]
    [InlineData("workspace:home", "workspace:home")]
    public void ScopesNormalizeConsistently(string input, string expected) =>
        MemoryScope.Parse(input).ToString().Should().Be(expected);

    [Theory]
    [InlineData("path:")]
    [InlineData("path:/etc")]
    [InlineData("path:../x")]
    [InlineData("path:.")]
    [InlineData("path:a/../../b")]
    [InlineData("subsystem:")]
    [InlineData("team:core")]
    [InlineData("project:x")]
    [InlineData("internal/storage")]
    public void InvalidScopesAreRejected(string input) =>
        FluentActions.Invoking(() => MemoryScope.Parse(input)).Should().ThrowExactly<ArgumentException>();

    [Fact]
    public void ApplicabilityRespectsPathBoundariesAndCase()
    {
        var target = MemoryScope.Parse("path:internal/storage/db.cs");
        foreach (
            var scope in new[]
            {
                "project",
                "workspace:home",
                "user",
                "path:internal/storage",
                "path:internal/storage/db.cs",
            }
        )
        {
            MemoryScope.Parse(scope).AppliesTo(target).Should().BeTrue();
        }

        foreach (
            var scope in new[]
            {
                "path:internal/stor",
                "path:Internal/storage",
                "path:internal/git",
                "subsystem:auth",
                "branch:main",
            }
        )
        {
            MemoryScope.Parse(scope).AppliesTo(target).Should().BeFalse();
        }

        MemoryScope.Parse("branch:main").AppliesTo(MemoryScope.Parse("branch:main")).Should().BeTrue();
        MemoryScope.Parse("branch:main").AppliesTo(MemoryScope.Parse("branch:Main")).Should().BeFalse();
    }

    [Fact]
    public void NanosecondTimesSurviveOffsetsAndCompareWithoutTickTruncation()
    {
        var first = MemoryTimestamp.Parse("2026-10-21T01:02:03.123456789Z");
        var next = MemoryTimestamp.Parse("2026-10-21T03:02:03.123456790+02:00");
        (first < next).Should().BeTrue();
        first.ToString().Should().Be("2026-10-21T01:02:03.123456789Z");
        first.ToRfc3339().Should().Be("2026-10-21T01:02:03Z");
        MemoryTimestamp.Parse("2026-10-21T01:02:03Z").ToString().Should().Be("2026-10-21T01:02:03.000000000Z");
        FluentActions
            .Invoking(() => MemoryTimestamp.Parse("2026-10-21T01:02:03.1234567890Z"))
            .Should()
            .ThrowExactly<FormatException>();
    }

    [Fact]
    public void NormalizationHashesAndSimilarityMatchLegacyRules()
    {
        MemoryText.Normalize("  Wrap\tERRORS\nwith %w.!;,  ").Should().Be("wrap errors with %w");
        MemoryText.Hash("  wrap ERRORS with %w ").Should().Be(MemoryText.Hash("Wrap errors with %w."));
        MemoryText.Similarity("Use SQLite for storage", "Storage uses SQLite").Should().Be(2.0 / 3);
        MemoryText.Similarity("Use SQLite for storage", "Deploy with Kubernetes").Should().Be(0);
        MemoryText.FullTextQuery("\"; DROP TABLE memories; --").Should().Be("\"DROP\" OR \"TABLE\" OR \"memories\"");
        MemoryText.FullTextQuery("!!!").Should().Be("");
        MemoryText.ParseType(" OPEN_QUESTION ").Should().Be("open_question");
    }

    [Fact]
    public void ValidationBoundsUtf8RejectsInvalidEncodingAndNonfiniteConfidence()
    {
        var input = new MemoryInput { Type = "fact", Content = new string('ž', 2000) };
        Encoding.UTF8.GetByteCount(MemoryText.Validate(input).Content).Should().Be(4000);
        FluentActions
            .Invoking(() => MemoryText.Validate(input with { Content = input.Content + "a" }))
            .Should()
            .ThrowExactly<ArgumentException>();
        FluentActions
            .Invoking(() => MemoryText.Validate(input with { Content = "\ud800" }))
            .Should()
            .ThrowExactly<EncoderFallbackException>();
        foreach (var confidence in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.1, 1.1 })
        {
            FluentActions
                .Invoking(() => MemoryText.Validate(input with { Confidence = confidence }))
                .Should()
                .ThrowExactly<ArgumentException>();
        }

        FluentActions
            .Invoking(() => MemoryText.Validate(input with { Scope = MemoryScope.User }))
            .Should()
            .ThrowExactly<ArgumentException>();
        FluentActions
            .Invoking(() => MemoryText.Validate(input with { Scope = MemoryScope.Parse("workspace:home") }))
            .Should()
            .ThrowExactly<ArgumentException>();
    }

    [Fact]
    public void ValidationDefaultsAlwaysRecordSourceAndActor()
    {
        var input = new MemoryInput
        {
            Type = " FACT ",
            Content = "  CI uses GitHub Actions  ",
            Scope = new MemoryScope(""),
            Source = new MemorySource("", " ", " "),
        };
        var normalized = MemoryText.Validate(input);
        normalized.Type.Should().Be("fact");
        normalized.Content.Should().Be("CI uses GitHub Actions");
        normalized.Scope.Should().Be(MemoryScope.Project);
        normalized.Source.Should().Be(new MemorySource("agent", "unknown", "unknown"));
        MemoryText.Validate(input with { Source = new MemorySource(By: "codex") }).Source.Ref.Should().Be("codex");
        FluentActions
            .Invoking(() => MemoryText.Validate(input with { Source = new MemorySource("rumour") }))
            .Should()
            .ThrowExactly<ArgumentException>();
    }

    public static TheoryData<string, string> RedactionFixtures =>
        new()
        {
            {
                "clone https://zaki:hunter2pass@gitlab.com/x.git please",
                "clone https://zaki:[redacted]@gitlab.com/x.git please"
            },
            {
                "curl -H 'Authorization: Bearer abcdefghijklmnop1234567890'",
                "curl -H 'Authorization: Bearer [redacted]'"
            },
            { "use key sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123 for the call", "use key [redacted] for the call" },
            { "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef0123 leaked", "token [redacted] leaked" },
            { "glpat-abcdefghijklmnopqrstu in the CI", "[redacted] in the CI" },
            { "aws AKIAIOSFODNN7EXAMPLE key", "aws [redacted] key" },
            { "DB_PASSWORD=s3cr3t! and API_KEY: 'abc def'", "DB_PASSWORD=[redacted] and API_KEY: [redacted]" },
            { """{"client_secret": "xyz"}""", """{"client_secret": [redacted]}""" },
            {
                "jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U",
                "jwt [redacted]"
            },
            { "-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----", "[redacted]" },
        };

    [Theory]
    [MemberData(nameof(RedactionFixtures))]
    public void RedactionRejectsCredentialsWithoutEchoingThem(string input, string expected)
    {
        CredentialRedactor.Redact(input).Should().Be(expected);
        var error = FluentActions
            .Invoking(() => MemoryText.Validate(new MemoryInput { Type = "fact", Content = input }))
            .Should()
            .ThrowExactly<ArgumentException>()
            .Which;
        error.Message.Should().Contain("never stores credentials");
        error.Message.Should().NotContain(input);
    }

    [Theory]
    [InlineData("Fix OrderService.Confirm: stale Revision after PUT")]
    [InlineData("tokenizer splits task words; password reset page shows the wrong copy")]
    [InlineData("https://gitlab.com/matus.zakarovsky/ai-harness/-/merge_requests/new")]
    [InlineData("get_context budget=6000 path=internal/retrieval")]
    [InlineData("sk-learn style model")]
    public void OrdinaryNonCredentialValuesRemainUsable(string input)
    {
        CredentialRedactor.Redact(input).Should().Be(input);
        MemoryText.Validate(new MemoryInput { Type = "fact", Content = input }).Content.Should().Be(input);
    }

    [Fact]
    public void ValidityWindowIncludesStartAndExcludesEnd()
    {
        var from = MemoryTimestamp.Parse("2026-10-21T00:00:00.000000001Z");
        var until = MemoryTimestamp.Parse("2026-10-21T00:00:00.000000002Z");
        var memory = new MemoryRecord(
            "id",
            "fact",
            "content",
            MemoryScope.Project,
            new MemorySource(),
            null,
            "",
            "",
            from,
            until,
            from,
            from
        );
        memory.Current(from).Should().BeTrue();
        memory.Current(until).Should().BeFalse();
        (memory with { SupersededBy = "replacement" }).Current(from).Should().BeFalse();
    }

    [Fact]
    public void WiderFailuresArePartialButProjectFailuresAndCancellationPropagate()
    {
        var failed = new FailedStore(new IOException("unavailable"));
        var scope = MemoryScope.Parse("workspace:offline");
        var result = new MemoryLayers(null, [new MemoryLevel(scope, failed)]).Search(
            new MemoryQuery(),
            TestContext.Current.CancellationToken
        );
        result.Memories.Should().BeEmpty();
        result.Errors.Should().ContainSingle().Which.Scope.Should().Be(scope);
        FluentActions
            .Invoking(() =>
                new MemoryLayers(failed, []).Search(new MemoryQuery(), TestContext.Current.CancellationToken)
            )
            .Should()
            .ThrowExactly<IOException>();
        FluentActions
            .Invoking(() =>
                new MemoryLayers(
                    null,
                    [new MemoryLevel(scope, new FailedStore(new OperationCanceledException()))]
                ).Search(new MemoryQuery(), TestContext.Current.CancellationToken)
            )
            .Should()
            .ThrowExactly<OperationCanceledException>();
    }

    private sealed class FailedStore(Exception error) : IMemoryStore
    {
        public MemoryRecord Get(string id, CancellationToken cancellationToken = default) => throw error;

        public IReadOnlyList<MemoryRecord> Search(MemoryQuery query, CancellationToken cancellationToken = default) =>
            throw error;

        public IReadOnlyList<SimilarMemory> Similar(
            string type,
            string content,
            CancellationToken cancellationToken = default
        ) => throw error;
    }
}
