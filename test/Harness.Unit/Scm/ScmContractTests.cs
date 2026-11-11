using System.Net;
using System.Security.Authentication;
using AwesomeAssertions;
using Harness.Scm;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class ScmContractTests
{
    private const string Token = "glpat-supersecret-0123456789";

    [Fact]
    public void ConnectionsNeverShowTheirTokenAsText()
    {
        ScmEndpoint.TryNormalizeUrl("https://gitlab.com", out var url, out _);
        var connection = new ScmConnection(new ScmEndpoint("gitlab", ScmKind.Gitlab, url), Token);

        connection.ToString().Should().NotContain(Token).And.Contain("****6789");
        connection.ApiRoot.AbsoluteUri.Should().Be("https://gitlab.com/");
        ScmTokens.Mask("short").Should().Be("****");
        ScmTokens.Mask("").Should().BeEmpty();
    }

    [Fact]
    public void RedactionRemovesTheTokenFromServerText() =>
        ScmErrors
            .Redact($"bad header PRIVATE-TOKEN: {Token} sent", Token)
            .Should()
            .Be("bad header PRIVATE-TOKEN: *** sent");

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ScmErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ScmErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, ScmErrorKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, ScmErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ScmErrorKind.Invalid)]
    [InlineData(HttpStatusCode.BadRequest, ScmErrorKind.Invalid)]
    [InlineData(HttpStatusCode.BadGateway, ScmErrorKind.Server)]
    public void HttpStatusesMapToErrorKinds(HttpStatusCode status, ScmErrorKind kind) =>
        ScmErrors.FromStatus(status).Should().Be(kind);

    [Fact]
    public void TransportFailuresAreTlsOrUnreachable()
    {
        var server = new Uri("https://gitlab.example.com/");
        var tls = new HttpRequestException(
            HttpRequestError.SecureConnectionError,
            "The SSL connection could not be established",
            new AuthenticationException("bad certificate")
        );

        ScmErrors.FromTransport(server, tls).Kind.Should().Be(ScmErrorKind.Tls);
        ScmErrors
            .FromTransport(server, new HttpRequestException("No such host"))
            .Kind.Should()
            .Be(ScmErrorKind.Unreachable);
        ScmErrors.FromTransport(server, new TaskCanceledException()).Message.Should().Contain("did not answer in time");
    }

    [Fact]
    public void EveryErrorKindHasANameAndAHint()
    {
        foreach (var kind in Enum.GetValues<ScmErrorKind>())
        {
            ScmErrors.Name(kind).Should().NotBeNullOrWhiteSpace();
            ScmErrors.Hint(kind).Should().NotBeNullOrWhiteSpace();
        }

        var failed = ScmConnectionTest.Failed(new ScmException(ScmErrorKind.Unauthorized, "401"));
        failed.Ok.Should().BeFalse();
        failed.Hint.Should().Contain("harness scm set");
    }

    [Theory]
    [InlineData("gitlab", ScmKind.Gitlab, "gitlab")]
    [InlineData(" GitLab ", ScmKind.Gitlab, "gitlab")]
    [InlineData("forgejo", ScmKind.Forgejo, "forgejo")]
    public void KindsParseCaseInsensitively(string text, ScmKind kind, string name)
    {
        ScmKinds.TryParse(text, out var parsed).Should().BeTrue();
        parsed.Should().Be(kind);
        ScmKinds.Name(kind).Should().Be(name);
    }

    [Fact]
    public void UnknownKindsAndEnumNamesAreRejected()
    {
        ScmKinds.TryParse("github", out _).Should().BeFalse();
        ScmNames.TryParseState("open", out var state).Should().BeTrue();
        state.Should().Be(MergeRequestState.Opened);
        ScmNames.TryParseState("draft", out _).Should().BeFalse();
        ScmNames.TryParseVisibility("internal", out var visibility).Should().BeTrue();
        ScmNames.Name(visibility).Should().Be("internal");
        ScmNames.TryParseLineType("old", out var line).Should().BeTrue();
        ScmNames.Name(line).Should().Be("old");
    }

    [Fact]
    public void TheRegistryConnectsThroughTheProviderOfTheConnectionsKind()
    {
        var gitlab = new FakeProvider(ScmKind.Gitlab);
        var registry = new ScmProviderRegistry([gitlab]);
        ScmEndpoint.TryNormalizeUrl("https://gitlab.com", out var url, out _);
        var connection = new ScmConnection(new ScmEndpoint("g", ScmKind.Gitlab, url), Token);

        registry.Connect(connection).Should().BeNull();
        gitlab.Connected.Should().Be(connection);
        registry.Kinds.Should().Equal(ScmKind.Gitlab);
        FluentActions
            .Invoking(() => registry.For(ScmKind.Forgejo))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*forgejo*");
        FluentActions
            .Invoking(() => new ScmProviderRegistry([gitlab, new FakeProvider(ScmKind.Gitlab)]))
            .Should()
            .Throw<ArgumentException>();
    }

    private sealed class FakeProvider(ScmKind kind) : IScmProvider
    {
        public ScmConnection? Connected { get; private set; }

        public ScmKind Kind => kind;

        public IScmClient Connect(ScmConnection connection)
        {
            Connected = connection;
            return null!;
        }
    }
}
