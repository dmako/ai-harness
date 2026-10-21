using System.Text;
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
    public void ScopesNormalizeLikeGo(string input, string expected) => Assert.Equal(expected, MemoryScope.Parse(input).ToString());

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
    public void InvalidScopesAreRejected(string input) => Assert.Throws<ArgumentException>(() => MemoryScope.Parse(input));

    [Fact]
    public void ApplicabilityRespectsPathBoundariesAndCase()
    {
        var target = MemoryScope.Parse("path:internal/storage/db.cs");
        foreach (var scope in new[] { "project", "workspace:home", "user", "path:internal/storage", "path:internal/storage/db.cs" })
            Assert.True(MemoryScope.Parse(scope).AppliesTo(target));
        foreach (var scope in new[] { "path:internal/stor", "path:Internal/storage", "path:internal/git", "subsystem:auth", "branch:main" })
            Assert.False(MemoryScope.Parse(scope).AppliesTo(target));
        Assert.True(MemoryScope.Parse("branch:main").AppliesTo(MemoryScope.Parse("branch:main")));
        Assert.False(MemoryScope.Parse("branch:main").AppliesTo(MemoryScope.Parse("branch:Main")));
    }

    [Fact]
    public void NanosecondTimesSurviveOffsetsAndCompareWithoutTickTruncation()
    {
        var first = MemoryTimestamp.Parse("2026-10-21T01:02:03.123456789Z");
        var next = MemoryTimestamp.Parse("2026-10-21T03:02:03.123456790+02:00");
        Assert.True(first < next);
        Assert.Equal("2026-10-21T01:02:03.123456789Z", first.ToString());
        Assert.Equal("2026-10-21T01:02:03Z", first.ToRfc3339());
        Assert.Equal("2026-10-21T01:02:03.000000000Z", MemoryTimestamp.Parse("2026-10-21T01:02:03Z").ToString());
        Assert.Throws<FormatException>(() => MemoryTimestamp.Parse("2026-10-21T01:02:03.1234567890Z"));
    }

    [Fact]
    public void NormalizationHashesAndSimilarityMatchLegacyRules()
    {
        Assert.Equal("wrap errors with %w", MemoryText.Normalize("  Wrap\tERRORS\nwith %w.!;,  "));
        Assert.Equal(MemoryText.Hash("Wrap errors with %w."), MemoryText.Hash("  wrap ERRORS with %w "));
        Assert.Equal(2.0 / 3, MemoryText.Similarity("Use SQLite for storage", "Storage uses SQLite"));
        Assert.Equal(0, MemoryText.Similarity("Use SQLite for storage", "Deploy with Kubernetes"));
        Assert.Equal("\"DROP\" OR \"TABLE\" OR \"memories\"", MemoryText.FullTextQuery("\"; DROP TABLE memories; --"));
        Assert.Equal("", MemoryText.FullTextQuery("!!!"));
        Assert.Equal("open_question", MemoryText.ParseType(" OPEN_QUESTION "));
    }

    [Fact]
    public void ValidationBoundsUtf8RejectsInvalidEncodingAndNonfiniteConfidence()
    {
        var input = new MemoryInput { Type = "fact", Content = new string('ž', 2000) };
        Assert.Equal(4000, Encoding.UTF8.GetByteCount(MemoryText.Validate(input).Content));
        Assert.Throws<ArgumentException>(() => MemoryText.Validate(input with { Content = input.Content + "a" }));
        Assert.Throws<EncoderFallbackException>(() => MemoryText.Validate(input with { Content = "\ud800" }));
        foreach (var confidence in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.1, 1.1 })
            Assert.Throws<ArgumentException>(() => MemoryText.Validate(input with { Confidence = confidence }));
        Assert.Throws<ArgumentException>(() => MemoryText.Validate(input with { Scope = MemoryScope.User }));
        Assert.Throws<ArgumentException>(() => MemoryText.Validate(input with { Scope = MemoryScope.Parse("workspace:home") }));
    }

    [Fact]
    public void ValidationDefaultsAlwaysRecordSourceAndActor()
    {
        var input = new MemoryInput { Type = " FACT ", Content = "  CI uses GitHub Actions  ", Scope = new MemoryScope(""), Source = new MemorySource("", " ", " ") };
        var normalized = MemoryText.Validate(input);
        Assert.Equal("fact", normalized.Type);
        Assert.Equal("CI uses GitHub Actions", normalized.Content);
        Assert.Equal(MemoryScope.Project, normalized.Scope);
        Assert.Equal(new MemorySource("agent", "unknown", "unknown"), normalized.Source);
        Assert.Equal("codex", MemoryText.Validate(input with { Source = new MemorySource(By: "codex") }).Source.Ref);
        Assert.Throws<ArgumentException>(() => MemoryText.Validate(input with { Source = new MemorySource("rumour") }));
    }

    public static TheoryData<string, string> RedactionFixtures => new()
    {
        { "clone https://zaki:hunter2pass@gitlab.com/x.git please", "clone https://zaki:[redacted]@gitlab.com/x.git please" },
        { "curl -H 'Authorization: Bearer abcdefghijklmnop1234567890'", "curl -H 'Authorization: Bearer [redacted]'" },
        { "use key sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123 for the call", "use key [redacted] for the call" },
        { "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef0123 leaked", "token [redacted] leaked" },
        { "glpat-abcdefghijklmnopqrstu in the CI", "[redacted] in the CI" },
        { "aws AKIAIOSFODNN7EXAMPLE key", "aws [redacted] key" },
        { "DB_PASSWORD=s3cr3t! and API_KEY: 'abc def'", "DB_PASSWORD=[redacted] and API_KEY: [redacted]" },
        { "{\"client_secret\": \"xyz\"}", "{\"client_secret\": [redacted]}" },
        { "jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "jwt [redacted]" },
        { "-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----", "[redacted]" },
    };

    [Theory]
    [MemberData(nameof(RedactionFixtures))]
    public void GoRedactionFixturesRejectCredentialsWithoutEchoingThem(string input, string expected)
    {
        Assert.Equal(expected, CredentialRedactor.Redact(input));
        var error = Assert.Throws<ArgumentException>(() => MemoryText.Validate(new MemoryInput { Type = "fact", Content = input }));
        Assert.Contains("never stores credentials", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(input, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Fix OrderService.Confirm: stale Revision after PUT")]
    [InlineData("tokenizer splits task words; password reset page shows the wrong copy")]
    [InlineData("https://gitlab.com/matus.zakarovsky/ai-harness/-/merge_requests/new")]
    [InlineData("get_context budget=6000 path=internal/retrieval")]
    [InlineData("sk-learn style model")]
    public void OrdinaryGoRedactionFixturesRemainUsable(string input)
    {
        Assert.Equal(input, CredentialRedactor.Redact(input));
        Assert.Equal(input, MemoryText.Validate(new MemoryInput { Type = "fact", Content = input }).Content);
    }

    [Fact]
    public void ValidityWindowIncludesStartAndExcludesEnd()
    {
        var from = MemoryTimestamp.Parse("2026-10-21T00:00:00.000000001Z");
        var until = MemoryTimestamp.Parse("2026-10-21T00:00:00.000000002Z");
        var memory = new MemoryRecord("id", "fact", "content", MemoryScope.Project, new MemorySource(), null, "", "", from, until, from, from);
        Assert.True(memory.Current(from));
        Assert.False(memory.Current(until));
        Assert.False((memory with { SupersededBy = "replacement" }).Current(from));
    }

    [Fact]
    public void WiderFailuresArePartialButProjectFailuresAndCancellationPropagate()
    {
        var failed = new FailedStore(new IOException("unavailable"));
        var scope = MemoryScope.Parse("workspace:offline");
        var result = new MemoryLayers(null, [new MemoryLevel(scope, failed)]).Search(new MemoryQuery(), TestContext.Current.CancellationToken);
        Assert.Empty(result.Memories);
        Assert.Equal(scope, Assert.Single(result.Errors).Scope);
        Assert.Throws<IOException>(() => new MemoryLayers(failed, []).Search(new MemoryQuery(), TestContext.Current.CancellationToken));
        Assert.Throws<OperationCanceledException>(() => new MemoryLayers(null,
            [new MemoryLevel(scope, new FailedStore(new OperationCanceledException()))]).Search(new MemoryQuery(), TestContext.Current.CancellationToken));
    }

    private sealed class FailedStore(Exception error) : IMemoryStore
    {
        public MemoryRecord Get(string id, CancellationToken cancellationToken = default) => throw error;
        public IReadOnlyList<MemoryRecord> Search(MemoryQuery query, CancellationToken cancellationToken = default) => throw error;
        public IReadOnlyList<SimilarMemory> Similar(string type, string content, CancellationToken cancellationToken = default) => throw error;
    }
}
