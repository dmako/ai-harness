using System.Net;
using AwesomeAssertions;
using Harness.Integration.Sync;
using Harness.Web;
using Xunit;

namespace Harness.Integration.Web;

/// <summary>The Avalonia UI's bundle at /next/ (TASK-0144): opt-in folder, content types, policy and path guard.</summary>
[Collection("Provider processes")]
public sealed class UiNextTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NextServesTheBundleFolderWithItsContentTypesAndAWebAssemblyPolicy()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var bundle = Path.Combine(fixture.DirectoryRoot, "next-bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "_framework"));
        await File.WriteAllTextAsync(Path.Combine(bundle, "index.html"), "<!DOCTYPE html><title>next</title>", Token);
        await File.WriteAllTextAsync(Path.Combine(bundle, "main.js"), "export {};", Token);
        await File.WriteAllBytesAsync(Path.Combine(bundle, "main.js.br"), [0x0b, 0x04, 0x80, 0x65, 0x78, 0x03], Token);
        await File.WriteAllBytesAsync(Path.Combine(bundle, "_framework", "dotnet.native.wasm"), [0, 0x61, 0x73, 0x6d], Token);
        await File.WriteAllBytesAsync(Path.Combine(bundle, "_framework", "icudt.dat"), [1, 2, 3], Token);
        await File.WriteAllTextAsync(Path.Combine(bundle, "web.config"), "<configuration />", Token);
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryRoot, "outside.js"), "secret", Token);

        await using var server = await UiFixture.StartAsync(
            fixture,
            environment: new Dictionary<string, string> { [UiNextAssets.FolderVariable] = bundle }
        );
        // plain http on loopback: no certificates to check
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true };
        using var noRedirect = new HttpClient(handler) { BaseAddress = server.Client.BaseAddress };
        using (var redirect = await noRedirect.GetAsync("/next", Token))
        {
            redirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
            redirect.Headers.Location!.ToString().Should().Be("/next/");
        }
        foreach (
            var (path, type) in new[]
            {
                ("/next/", "text/html; charset=utf-8"),
                ("/next/index.html", "text/html; charset=utf-8"),
                ("/next/main.js", "text/javascript; charset=utf-8"),
                ("/next/_framework/dotnet.native.wasm", "application/wasm"),
                ("/next/_framework/icudt.dat", "application/octet-stream"),
            }
        )
        {
            using var response = await server.Client.GetAsync(path, Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            response.Content.Headers.ContentType!.ToString().Should().Be(type, path);
            response.Headers.GetValues("Content-Security-Policy").Single().Should().Be(UiNextAssets.ContentSecurityPolicy);
            response.Headers.CacheControl!.NoCache.Should().BeTrue(path);
        }
        UiNextAssets.ContentSecurityPolicy.Should().Contain("'wasm-unsafe-eval'").And.NotContain("'unsafe-eval'");

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/next/main.js"))
        {
            request.Headers.AcceptEncoding.ParseAdd("br");
            using var compressed = await noRedirect.SendAsync(request, Token);
            compressed.Content.Headers.ContentEncoding.Should().Equal("br");
            (await compressed.Content.ReadAsByteArrayAsync(Token)).Length.Should().Be(6);
        }
        foreach (var path in new[] { "/next/web.config", "/next/missing.js", "/next/%2e%2e/outside.js", "/next/../outside.js" })
        {
            using var response = await server.Client.GetAsync(path, Token);
            response.IsSuccessStatusCode.Should().BeFalse(path);
        }
        // the current UI is untouched
        using var current = await server.Client.GetAsync("/", Token);
        (await current.Content.ReadAsStringAsync(Token)).Should().Contain("app.js");
    }

    [Fact]
    public async Task WithoutTheFolderThereIsNoNext()
    {
        using var fixture = await SyncFixture.CreateAsync();
        // empty, as an inherited value from the developer's shell would otherwise leak in
        await using var server = await UiFixture.StartAsync(
            fixture,
            environment: new Dictionary<string, string> { [UiNextAssets.FolderVariable] = "" }
        );
        using var response = await server.Client.GetAsync("/next/", Token);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
