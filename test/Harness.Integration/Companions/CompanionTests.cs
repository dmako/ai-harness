using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Collect;
using Harness.Core.Reports;
using Harness.Core.Telemetry;
using Harness.Infrastructure.Telemetry;
using Harness.Integration.Sync;
using Harness.Integration.Web;
using Harness.Share;
using Harness.Web;
using Xunit;

namespace Harness.Integration.Companions;

public sealed class CompanionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CliPreviewDoesNotPersistOrSendAndDisabledAutomaticSendNeverExports()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var app = CollectApplication.Create(
            Path.Combine(fixture.DirectoryRoot, "reports"),
            "127.0.0.1:0",
            token: "tok"
        );
        await app.StartAsync(Token);
        Task<(int Code, string Output, string Error)> Cli(params string[] args) =>
            fixture.ProcessAsync("dotnet", [UiFixture.CliPath, "telemetry", .. args]);
        (await Cli()).Output.Should().Contain("export: off");
        var preview = await Cli("preview");
        preview.Code.Should().Be(0, preview.Error);
        JsonNode.Parse(preview.Output)!["projects"]!.AsArray().Should().HaveCount(1);
        var statePath = Path.Combine(fixture.Global, "telemetry.json");
        File.Exists(statePath).Should().BeFalse();
        (await Cli("send")).Code.Should().Be(2);
        (await Cli("send", "--auto")).Code.Should().Be(0);
        (await Cli("enable")).Code.Should().Be(2);
        (await Cli("enable", "--endpoint", "ftp://invalid")).Code.Should().Be(1);
        (await Cli("enable", "--endpoint", UiHost.Address(app) + "v1/report", "--token", "tok")).Code.Should().Be(0);
        var exported = await Cli("send", "--auto");
        exported.Code.Should().Be(0, exported.Error);
        exported.Output.Should().BeEmpty();
        (await File.ReadAllLinesAsync(Path.Combine(fixture.Global, "telemetry.log"), Token)).Should().HaveCount(1);
        (await Cli("send", "--auto")).Code.Should().Be(0);
        (await File.ReadAllLinesAsync(Path.Combine(fixture.Global, "telemetry.log"), Token)).Should().HaveCount(1);
        (await Cli("disable")).Code.Should().Be(0);
        (await Cli("send", "--auto")).Code.Should().Be(0);
        (await Cli("send")).Code.Should().Be(2);
        (await File.ReadAllLinesAsync(Path.Combine(fixture.Global, "telemetry.log"), Token)).Should().HaveCount(1);
        (await Cli("enable", "--endpoint", "http://127.0.0.1:1/v1/report")).Code.Should().Be(0);
        (await Cli("send")).Code.Should().Be(1);
        (await Cli("send", "--auto")).Code.Should().Be(0);
        await app.StopAsync(Token);
    }

    [Fact]
    public async Task AggregateExportRoundTripsThroughCollectorWithAuthenticationAndDailySuccessInterval()
    {
        using var temp = new TemporaryDirectory();
        var exporter = new DeveloperExport(Path.Combine(temp.Path, "sender"));
        var state = exporter.Load();
        state.Enabled.Should().BeFalse();
        state.Due(DateTimeOffset.UtcNow).Should().BeFalse();
        await using var app = CollectApplication.Create(
            Path.Combine(temp.Path, "reports"),
            "127.0.0.1:0",
            token: "test-token"
        );
        await app.StartAsync(Token);
        DeveloperExport.Enable(state, UiHost.Address(app) + "v1/report", "test-token");
        var now = DateTimeOffset.UtcNow;
        var report = new LocalReport
        {
            Id = "prj_0123456789abcdef",
            Root = "/Users/someone/secret-project",
            Context = new()
            {
                Calls = 4,
                Hits = 3,
                Changed = 10,
                Misses = [new() { Path = "source-secret.cs" }],
            },
            Findings =
            [
                new(
                    "warn",
                    "context",
                    "provider failed: https://bot:pw@host/x and C:\\repo\\a.cs",
                    "harness context stats --project secret-project"
                ),
            ],
            Metrics = new() { Tools = [new() { Tool = "get_context", Calls = 4 }] },
            Knowledge = new() { Current = 7 },
        };
        var payload = DeveloperExport.Build(
            state,
            [report],
            new() { ["version"] = "test", ["platform"] = "test" },
            new Dictionary<string, string> { ["ripwire"] = "ripwire 1.2" },
            now
        );
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var body = Encoding.UTF8.GetString(bytes);
        foreach (
            var forbidden in new[]
            {
                report.Id,
                "secret-project",
                "/Users",
                "source-secret.cs",
                "bot:pw",
                "C:\\repo",
                "harness context stats",
                "top_misses",
                "query",
                "root",
                "action",
                "task",
            }
        )
        {
            body.Should().NotContain(forbidden);
        }
        var again = DeveloperExport.Build(state, [report], new(), new Dictionary<string, string>(), now);
        again["projects"]![0]!["id"]!.ToString().Should().Be(payload["projects"]![0]!["id"]!.ToString());
        await exporter.SendAsync(state, bytes, now, Token);
        state.LastSent.Should().Be(now);
        state.Due(now.AddHours(1)).Should().BeFalse();
        state.Due(now.AddDays(1)).Should().BeTrue();
        await exporter.SaveAsync(state, Token);
        exporter.Load().Token.Should().Be("test-token");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(exporter.StatePath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        using var client = new HttpClient { BaseAddress = new Uri(UiHost.Address(app)) };
        (await client.GetStringAsync("/machines/" + state.Machine + ".json", Token)).Should().Be(body);
        (await client.GetStringAsync("/", Token)).Should().Contain(state.Machine).And.Contain("--brand-teal");
        Directory.GetFiles(Path.Combine(temp.Path, "reports", state.Machine)).Should().HaveCount(2);
        state.Token = "wrong";
        var send = () => exporter.SendAsync(state, bytes, now.AddMinutes(1), Token);
        await send.Should().ThrowAsync<HttpRequestException>();
        state.LastSent.Should().Be(now);
        (await File.ReadAllLinesAsync(exporter.LogPath, Token)).Should().HaveCount(2);
        (await File.ReadAllTextAsync(exporter.LogPath, Token)).Should().NotContain("test-token").And.NotContain(body);
        state.Enabled = false;
        await send.Should().ThrowAsync<InvalidOperationException>();
        (await File.ReadAllLinesAsync(exporter.LogPath, Token)).Should().HaveCount(2);
        await app.StopAsync(Token);
    }

    [Theory]
    [InlineData("hello", 400)]
    [InlineData("""{"schema":9,"machine":"m_0123456789abcdef"}""", 400)]
    [InlineData("""{"schema":1,"machine":"../../etc"}""", 400)]
    [InlineData("""{"schema":"1","machine":"m_0123456789abcdef"}""", 400)]
    public async Task CollectorRejectsInvalidReports(string body, int status)
    {
        using var temp = new TemporaryDirectory();
        await using var app = CollectApplication.Create(temp.Path, "127.0.0.1:0", token: "tok");
        await app.StartAsync(Token);
        using var client = new HttpClient { BaseAddress = new Uri(UiHost.Address(app)) };
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var unauthenticated = await client.PostAsync("/v1/report", content, Token);
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "tok");
        using var invalid = await client.PostAsync("/v1/report", content, Token);
        ((int)invalid.StatusCode).Should().Be(status);
        using var hugeContent = new StringContent(new string('x', CollectApplication.MaxBody + 1));
        using var huge = await client.PostAsync("/v1/report", hugeContent, Token);
        huge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).Should().BeEmpty();
        await app.StopAsync(Token);
    }

    [Theory]
    [InlineData("project prj_edda8bc866761dd9b196edcc6d73d1a7 is not readable", "project [id] is not readable")]
    [InlineData("context hits 26% (31/130 over 25 answers)", "context hits 26% (31/130 over 25 answers)")]
    [InlineData("provider failed: https://bot:pw@host/x and C:\\repo\\a.cs", "provider failed: [path] and [path]")]
    [InlineData("token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef0123 in internal/cli/x.cs", "token [redacted] in [path]")]
    public void RedactionScenarios(string input, string expected) => DeveloperExport.Clean(input).Should().Be(expected);

    [Fact]
    public async Task SharingServesOnlyStartupAllowlistReadOnlyWithChecksumsRangesAndPlatformHints()
    {
        using var temp = new TemporaryDirectory();
        var releaseDir = Path.Combine(temp.Path, "v0.1.0");
        Directory.CreateDirectory(releaseDir);
        foreach (
            var (name, body) in new Dictionary<string, string>
            {
                ["harness-v0.1.0-linux-amd64.tar.gz"] = "linux bytes",
                ["harness-v0.1.0-darwin-arm64.tar.gz"] = "mac bytes",
                ["harness-v0.1.0-windows-amd64.zip"] = "win bytes",
                ["SHA256SUMS"] = "abc123  harness-v0.1.0-windows-amd64.zip\n",
                ["RELEASE_NOTES.md"] = "# notes",
                ["secret.txt"] = "private",
            }
        )
        {
            await File.WriteAllTextAsync(Path.Combine(releaseDir, name), body, Token);
        }
        var release = ShareApplication.Load(releaseDir);
        release.Builds.Should().HaveCount(3);
        release.Extra.Should().HaveCount(2);
        release.Builds.Single(b => b.OS is "windows").SHA256.Should().Be("abc123");
        release.Builds.Single(b => b.OS is "linux").SHA256.Should().HaveLength(64);
        ShareApplication.Latest(temp.Path).Should().Be(releaseDir);
        await using var app = ShareApplication.Create(release, "127.0.0.1:0");
        await app.StartAsync(Token);
        using var client = new HttpClient { BaseAddress = new Uri(UiHost.Address(app)) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0)");
        (await client.GetStringAsync("/", Token))
            .Should()
            .Contain("Download for macOS · Apple silicon")
            .And.Contain("Intel Mac")
            .And.Contain("sha256 abc123")
            .And.Contain("chmod +x")
            .And.Contain("Release notes");
        var download = "/download/harness-v0.1.0-darwin-arm64.tar.gz";
        using var response = await client.GetAsync(download, Token);
        (await response.Content.ReadAsStringAsync(Token)).Should().Be("mac bytes");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        using var range = new HttpRequestMessage(HttpMethod.Get, download);
        range.Headers.Range = new RangeHeaderValue(0, 2);
        using var partial = await client.SendAsync(range, Token);
        partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await partial.Content.ReadAsStringAsync(Token)).Should().Be("mac");
        foreach (
            var path in new[]
            {
                "/secret.txt",
                "/download/secret.txt",
                "/download/..%2fsecret.txt",
                "/download/",
                "/harness-v0.1.0-linux-amd64.tar.gz",
                "/download/HARNESS-v0.1.0-linux-amd64.tar.gz",
            }
        )
        {
            using var rejected = await client.GetAsync(path, Token);
            rejected.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            using var request = new HttpRequestMessage(method, download);
            using var rejected = await client.SendAsync(request, Token);
            rejected.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "harness-v0.1.0-linux-arm64.tar.gz"), "late", Token);
        using var late = await client.GetAsync("/download/harness-v0.1.0-linux-arm64.tar.gz", Token);
        late.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await app.StopAsync(Token);
    }

    [Fact]
    public void WiderExposureRequiresExplicitLanOption()
    {
        var create = () => CompanionHost.Create("0.0.0.0:0", false, 8765);
        create.Should().Throw<ArgumentException>().WithMessage("*--lan*");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "harness-companion-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }
}
