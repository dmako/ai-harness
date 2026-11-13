using System.Net;
using System.Text;
using AwesomeAssertions;
using Harness.Core.Architecture;
using Harness.Infrastructure.Architecture.PackageFeeds;
using Harness.Infrastructure.Configuration;
using Xunit;

namespace Harness.Unit.Architecture;

/// <summary>
/// The NuGet V3 resources harness reads (recorded from nuget.org), the cache on disk with its TTL, private feeds'
/// credentials and the doctor check; every request goes to a fake handler, never the network (TASK-0141).
/// </summary>
public sealed class PackageFeedTests : IDisposable
{
    private const string Source = "https://feed.test/v3/index.json";

    /// <summary>A service index as nuget.org's, cut to the resources harness reads and one it does not.</summary>
    private const string ServiceIndex = """
        {
          "version": "3.0.0",
          "resources": [
            {
              "@id": "https://feed.test/v3-flatcontainer/",
              "@type": "PackageBaseAddress/3.0.0",
              "comment": "Base URL of where NuGet packages are stored"
            },
            {
              "@id": "https://feed.test/v3/vulnerabilities/index.json",
              "@type": "VulnerabilityInfo/6.7.0",
              "comment": "The endpoint for discovering information about vulnerabilities of packages in this package source."
            },
            { "@id": "https://feed.test/query", "@type": ["SearchQueryService", "SearchQueryService/3.0.0-beta"] }
          ]
        }
        """;

    private const string Versions = """
        {"versions":["8.0.0","8.0.1","9.0.0-preview.7.24405.7","9.0.0","9.0.3","10.0.0-rc.1.25451.107"]}
        """;

    private const string VulnerabilityIndex = """
        [{"@name":"base","@id":"https://feed.test/v3-vulnerabilities/2026.11.11.06.01.49/vulnerability.base.json","@updated":"2026-11-11T06:01:49.7810051Z","comment":"The base data for vulnerability update periodically"},{"@name":"update","@id":"https://feed.test/v3-vulnerabilities/2026.11.11.06.01.49/2026.11.12.11.22.45/vulnerability.update.json","@updated":"2026-11-12T11:22:45.4704388Z","comment":"The patch data for the vulnerability. Contains all the vulnerabilities since base was last updated."}]
        """;

    private const string BasePage = """
        {"microsoft.data.odata":[{"url":"https://github.com/advisories/GHSA-mv2r-q4g5-j8q5","severity":2,"versions":"(, 5.8.4)"}],"microsoft.aspnetcore.all":[{"url":"https://github.com/advisories/GHSA-mv2r-q4g5-j8q5","severity":2,"versions":"[2.1.0, 2.1.13)"},{"url":"https://github.com/advisories/GHSA-3m2r-q8x3-xmf7","severity":1,"versions":"[2.0.0, 2.0.8)"}],"acme.json":[{"url":"https://github.com/advisories/GHSA-acme-json","severity":3,"versions":"[8.0.0, 8.0.2)"}]}
        """;

    private const string UpdatePage = """
        {"acme.http":[{"url":"https://github.com/advisories/GHSA-acme-http","severity":0,"versions":"(, 2.0.0)"}]}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "harness-feeds-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFeed _feed = new();
    private readonly HttpClient _http;
    private DateTimeOffset _now = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);

    public PackageFeedTests()
    {
        Directory.CreateDirectory(_root);
        _http = new(_feed, false);
        _feed.Answer(Source, ServiceIndex);
        _feed.Answer("https://feed.test/v3-flatcontainer/acme.json/index.json", Versions);
        _feed.Answer("https://feed.test/v3-flatcontainer/acme.http/index.json", """{"versions":["1.0.0","2.0.0"]}""");
        _feed.Answer("https://feed.test/v3/vulnerabilities/index.json", VulnerabilityIndex);
        _feed.Answer("https://feed.test/v3-vulnerabilities/2026.11.11.06.01.49/vulnerability.base.json", BasePage);
        _feed.Answer(
            "https://feed.test/v3-vulnerabilities/2026.11.11.06.01.49/2026.11.12.11.22.45/vulnerability.update.json",
            UpdatePage
        );
    }

    public void Dispose()
    {
        _http.Dispose();
        _feed.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TheServiceIndexGivesTheFlatContainerAndTheVulnerabilityIndex()
    {
        var service = NuGetFeed.ParseService(ServiceIndex);

        service.BaseAddress.Should().Be("https://feed.test/v3-flatcontainer/");
        service.Vulnerabilities.Should().Be("https://feed.test/v3/vulnerabilities/index.json");
        NuGetFeed
            .ParseService("""{"resources":[{"@id":"https://x/flat/","@type":"PackageBaseAddress/3.0.0"}]}""")
            .Vulnerabilities.Should()
            .BeEmpty("a feed without VulnerabilityInfo has versions only");
        var invalid = () => NuGetFeed.ParseService("""{"resources":[]}""");
        invalid.Should().Throw<PackageSourceException>().Which.Kind.Should().Be("invalid");
    }

    [Fact]
    public void TheFlatContainerAndTheVulnerabilityResourcesAreParsed()
    {
        NuGetFeed.ParseVersions(Versions).Should().HaveCount(6).And.Contain("9.0.3");
        NuGetFeed
            .ParseVulnerabilityIndex(VulnerabilityIndex)
            .Select(page => (page.Name, page.Updated))
            .Should()
            .Equal(("base", "2026-11-11T06:01:49.7810051Z"), ("update", "2026-11-12T11:22:45.4704388Z"));
        var page = NuGetFeed.ParseVulnerabilityPage(BasePage);
        page["Microsoft.AspNetCore.All"]
            .Should()
            .Equal(
                new PackageAdvisory(2, "https://github.com/advisories/GHSA-mv2r-q4g5-j8q5", "[2.1.0, 2.1.13)"),
                new PackageAdvisory(1, "https://github.com/advisories/GHSA-3m2r-q8x3-xmf7", "[2.0.0, 2.0.8)")
            );
    }

    [Fact]
    public async Task ALookupMarksNewerStableVersionsAndVulnerabilitiesFromEveryPage()
    {
        var lookup = await Metadata()
            .LookupAsync(
                Settings(),
                [("Acme.Json", "8.0.1"), ("Acme.Http", "1.0.0")],
                PackageLookupMode.Network,
                Token
            );

        lookup.Problems.Should().BeEmpty();
        var json = lookup.Packages["acme.json@8.0.1"];
        json.Latest.Should().Be("9.0.3", "pre-releases are not offered");
        json.Vulnerabilities.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new PackageVulnerability(
                    "critical",
                    "https://github.com/advisories/GHSA-acme-json",
                    "[8.0.0, 8.0.2)",
                    "9.0.0"
                )
            );
        lookup
            .Packages["acme.http@1.0.0"]
            .Vulnerabilities.Single()
            .Severity.Should()
            .Be("low", "the update page counts too");
    }

    [Fact]
    public async Task UnresolvedAndFloatingVersionsAreNotLookedUp()
    {
        var lookup = await Metadata()
            .LookupAsync(
                Settings(),
                [("Acme.Json", "$(JsonVersion)"), ("Acme.Http", "1.*")],
                PackageLookupMode.Network,
                Token
            );

        lookup.Packages.Should().BeEmpty();
        _feed.Requests.Should().BeEmpty("nothing needs a source");
    }

    [Fact]
    public async Task VersionListsAreCachedForADayAndTheVulnerabilityPagesUntilTheirIndexChanges()
    {
        var metadata = Metadata();
        await metadata.LookupAsync(Settings(), [("Acme.Json", "8.0.1")], PackageLookupMode.Network, Token);
        _feed.Requests.Should().HaveCount(5, "service index, version list, vulnerability index and two pages");

        _feed.Requests.Clear();
        _now += TimeSpan.FromHours(23);
        await metadata.LookupAsync(Settings(), [("Acme.Json", "8.0.1")], PackageLookupMode.Network, Token);
        _feed.Requests.Should().BeEmpty("all of it is younger than 24 h");

        _now += TimeSpan.FromHours(2);
        _feed.Answer(
            "https://feed.test/v3/vulnerabilities/index.json",
            VulnerabilityIndex.Replace("2026-11-12T11:22:45.4704388Z", "2026-11-13T09:00:00Z", StringComparison.Ordinal)
        );
        var lookup = await metadata.LookupAsync(Settings(), [("Acme.Json", "8.0.1")], PackageLookupMode.Network, Token);
        _feed
            .Requests.Should()
            .BeEquivalentTo(
                [
                    Source,
                    "https://feed.test/v3-flatcontainer/acme.json/index.json",
                    "https://feed.test/v3/vulnerabilities/index.json",
                    "https://feed.test/v3-vulnerabilities/2026.11.11.06.01.49/2026.11.12.11.22.45/vulnerability.update.json",
                ],
                "after 24 h everything is read again, but only the changed page is downloaded"
            );
        lookup.Packages["acme.json@8.0.1"].Latest.Should().Be("9.0.3");
    }

    [Fact]
    public async Task ACacheOnlyLookupNeverAsksTheSourceAndUsesWhatIsAtMostAWeekOld()
    {
        var metadata = Metadata();
        (await metadata.LookupAsync(Settings(), [("Acme.Json", "8.0.1")], PackageLookupMode.CacheOnly, Token))
            .Packages.Should()
            .BeEmpty("nothing is cached yet");
        _feed.Requests.Should().BeEmpty();

        await metadata.LookupAsync(Settings(), [("Acme.Json", "8.0.1")], PackageLookupMode.Network, Token);
        _feed.Requests.Clear();
        _now += TimeSpan.FromDays(3);
        (await metadata.LookupAsync(Settings(), [("Acme.Json", "8.0.1")], PackageLookupMode.CacheOnly, Token))
            .Packages["acme.json@8.0.1"]
            .Vulnerabilities.Should()
            .ContainSingle();
        _now += TimeSpan.FromDays(5);
        (await metadata.LookupAsync(Settings(), [("Acme.Json", "8.0.1")], PackageLookupMode.CacheOnly, Token))
            .Packages.Should()
            .BeEmpty("a cache older than a week is not used");
        _feed.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ASourceThatCannotBeReadIsReportedAndLeavesNoMarks()
    {
        _feed.Answer(Source, "", HttpStatusCode.Unauthorized);
        var lookup = await Metadata()
            .LookupAsync(Settings(), [("Acme.Json", "8.0.1")], PackageLookupMode.Network, Token);

        lookup.Packages.Should().BeEmpty();
        lookup.Problems.Should().ContainSingle().Which.Kind.Should().Be("unauthorized");

        _feed.Fail(Source);
        (await Metadata().LookupAsync(Settings(), [("Acme.Json", "8.0.1")], PackageLookupMode.Network, Token))
            .Problems.Single()
            .Kind.Should()
            .Be("unreachable");
    }

    [Fact]
    public async Task APrivateFeedIsReadWithTheCredentialsOfTheNuGetConfiguration()
    {
        Environment.SetEnvironmentVariable("HARNESS_TEST_FEED_TOKEN", "s3cret");
        try
        {
            var settings = Settings(
                """
                <packageSourceCredentials>
                  <My_x0020_Feed>
                    <add key="Username" value="build" />
                    <add key="ClearTextPassword" value="%HARNESS_TEST_FEED_TOKEN%" />
                  </My_x0020_Feed>
                </packageSourceCredentials>
                """,
                key: "My Feed"
            );
            settings.Credentials["My Feed"].Should().Be(new NuGetCredential("build", "s3cret", ""));

            await Metadata().LookupAsync(settings, [("Acme.Json", "8.0.1")], PackageLookupMode.Network, Token);

            _feed
                .Authorizations.Should()
                .AllBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("build:s3cret")))
                .And.HaveCount(5);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HARNESS_TEST_FEED_TOKEN", null);
        }
    }

    [Fact]
    public void ACredentialThatCannotBeUsedSaysWhy()
    {
        var settings = Settings(
            """
            <packageSourceCredentials>
              <feed>
                <add key="Username" value="build" />
                <add key="ClearTextPassword" value="%HARNESS_TEST_UNSET_TOKEN%" />
              </feed>
            </packageSourceCredentials>
            """
        );

        settings.Credentials["feed"].Problem.Should().Contain("environment variable");
    }

    [Fact]
    public async Task DoctorReportsAnUnreadableSourceWithTheFix()
    {
        _feed.Answer(Source, "", HttpStatusCode.Forbidden);
        var settings = Settings();
        var problems = await Metadata().CheckSourcesAsync(settings, Token);

        var check = PackageSourcesCheck.Health(["feed"], problems, settings);

        check.Name.Should().Be("package-sources");
        check.Level.Should().Be("warn");
        check.Detail.Should().Contain("feed (" + Source + "): unauthorized");
        check.Fix.Should().Contain("packageSourceCredentials").And.Contain("dotnet nuget update source feed");

        _feed.Fail(Source);
        var unreachable = PackageSourcesCheck.Health(
            ["feed"],
            await Metadata().CheckSourcesAsync(settings, Token),
            settings
        );
        unreachable.Fix.Should().Contain("dotnet nuget disable source feed");

        _feed.Answer(Source, ServiceIndex);
        PackageSourcesCheck
            .Health(["feed"], await Metadata().CheckSourcesAsync(settings, Token), settings)
            .Level.Should()
            .Be("ok");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PackageMetadata Metadata() =>
        new(new PackageMetadataCache(Path.Combine(_root, "cache")), _http, () => _now);

    /// <summary>The repository's nuget.config: only the fake feed, under <paramref name="key"/>.</summary>
    private NuGetSettings Settings(string extra = "", string key = "feed")
    {
        File.WriteAllText(
            Path.Combine(_root, "nuget.config"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="{key}" value="{Source}" />
              </packageSources>
              {extra}
            </configuration>
            """
        );
        return NuGetSettings.Load(_root, "");
    }

    /// <summary>Answers recorded bodies by URL and records what was asked, with the Authorization header.</summary>
    private sealed class FakeFeed : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(
            StringComparer.Ordinal
        );
        private readonly HashSet<string> _failing = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];
        public List<string> Authorizations { get; } = [];

        public void Answer(string url, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _failing.Remove(url);
            _answers[url] = (status, body);
        }

        public void Fail(string url) => _failing.Add(url);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests)
            {
                Requests.Add(url);
                Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
            }

            if (_failing.Contains(url))
            {
                throw new HttpRequestException("No such host is known. (feed.test:443)");
            }

            var (status, body) = _answers.TryGetValue(url, out var answer) ? answer : (HttpStatusCode.NotFound, "");
            return Task.FromResult(
                new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
            );
        }
    }
}
