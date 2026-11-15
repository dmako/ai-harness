using System.IO.Compression;
using System.Net;
using System.Text;
using AwesomeAssertions;
using Harness.Integration.Sync;
using Harness.Web;
using Xunit;

namespace Harness.Integration.Web;

/// <summary>The installed sidecar bundle: root routing, encoding negotiation, cache policy and path guards.</summary>
[Collection("Provider processes")]
public sealed class UiBundleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MissingBundleReportsAnUnavailableUi()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var empty = Path.Combine(fixture.DirectoryRoot, "empty-bundle");
        Directory.CreateDirectory(empty);
        await using var server = await UiFixture.StartAsync(fixture, uiBundle: empty);
        using var response = await server.Client.GetAsync("/", Token);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Token)).Should().Contain("UI bundle missing");
    }

    [Fact]
    public async Task InstalledBundleServesBothBrotliAndIdentityAndCachesOnlyFingerprintedFiles()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var bundle = Path.Combine(fixture.DirectoryRoot, "bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "_framework"));
        await File.WriteAllTextAsync(Path.Combine(bundle, "index.html"), "<!DOCTYPE html><title>harness</title>", Token);
        await WriteBrotliAsync(Path.Combine(bundle, "main.js.br"), "export {};");
        await WriteBrotliAsync(Path.Combine(bundle, "_framework", "app.0123456789.wasm.br"), "wasm fixture");
        await File.WriteAllTextAsync(Path.Combine(bundle, "_framework", "dotnet.js"), "loader", Token);
        await File.WriteAllTextAsync(Path.Combine(bundle, "web.config"), "secret", Token);
        await using var server = await UiFixture.StartAsync(fixture, uiBundle: bundle);

        using (var root = await server.Client.GetAsync("/", Token))
        {
            root.StatusCode.Should().Be(HttpStatusCode.OK);
            (await root.Content.ReadAsStringAsync(Token)).Should().Contain("<title>harness</title>");
            root.Headers.CacheControl!.NoCache.Should().BeTrue();
            root.Headers.GetValues("Content-Security-Policy").Single().Should().Be(UiBundleAssets.ContentSecurityPolicy);
        }
        using (var identity = await server.Client.GetAsync("/main.js", Token))
        {
            identity.StatusCode.Should().Be(HttpStatusCode.OK);
            identity.Content.Headers.ContentEncoding.Should().BeEmpty();
            (await identity.Content.ReadAsStringAsync(Token)).Should().Be("export {};");
            identity.Headers.CacheControl!.NoCache.Should().BeTrue();
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/main.js"))
        {
            request.Headers.AcceptEncoding.ParseAdd("br;q=0, *;q=1");
            using var refused = await server.Client.SendAsync(request, Token);
            refused.Content.Headers.ContentEncoding.Should().BeEmpty();
            (await refused.Content.ReadAsStringAsync(Token)).Should().Be("export {};");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/_framework/app.0123456789.wasm"))
        {
            request.Headers.AcceptEncoding.ParseAdd("br");
            using var response = await server.Client.SendAsync(request, Token);
            response.Content.Headers.ContentEncoding.Should().Equal("br");
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/wasm");
            response.Headers.CacheControl!.Extensions.Should().Contain(e => e.Name == "immutable");
            response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromDays(365));
            var tag = response.Headers.ETag!;
            using var cached = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
            cached.Headers.AcceptEncoding.ParseAdd("br");
            cached.Headers.IfNoneMatch.Add(tag);
            using var notModified = await server.Client.SendAsync(cached, Token);
            notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        }
        using (var loader = await server.Client.GetAsync("/_framework/dotnet.js", Token))
        {
            loader.Headers.CacheControl!.NoCache.Should().BeTrue();
        }
        foreach (var path in new[] { "/next/", "/app.js", "/style.css", "/web.config", "/%2e%2e/outside.js" })
        {
            using var response = await server.Client.GetAsync(path, Token);
            response.IsSuccessStatusCode.Should().BeFalse(path);
        }
    }

    private static async Task WriteBrotliAsync(string file, string text)
    {
        await using var output = File.Create(file);
        await using var compressor = new BrotliStream(output, CompressionLevel.Optimal);
        await compressor.WriteAsync(Encoding.UTF8.GetBytes(text), Token);
    }
}
