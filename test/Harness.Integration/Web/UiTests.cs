using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Workspaces;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Harness.Web;
using Xunit;

namespace Harness.Integration.Web;

[Collection("Provider processes")]
public sealed class UiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task DefaultPortHelpAndExplicitAddressStartTheEmbeddedUi()
    {
        UiHost.DefaultAddress.Should().Be("127.0.0.1:17477");
        using var fixture = await SyncFixture.CreateAsync();
        var help = await fixture.ProcessAsync("dotnet", [UiFixture.CliPath, "ui", "--help"]);
        help.Code.Should().Be(0, help.Error);
        help.Output.Should().Contain("127.0.0.1:17477").And.Contain("--no-open");
        await using var server = await UiFixture.StartAsync(fixture, address: null);
        server.Client.BaseAddress!.Port.Should().Be(17477);
        var response = await server.Client.GetAsync("/", Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Token)).Should().Contain("app.js").And.Contain("style.css");
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Which.Should().Be("default-src 'self'; frame-ancestors 'none'");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        foreach (var asset in new[] { "app.js", "style.css", "brand.css", "logo.svg", "favicon.png", "apple-touch-icon.png" })
        {
            using var content = await server.Client.GetAsync(asset, Token);
            content.StatusCode.Should().Be(HttpStatusCode.OK, asset);
            content.Content.Headers.ContentLength.Should().BeGreaterThan(100);
        }
        var docs = await ReadAsync(server.Client, "/api/docs");
        docs["docs"]!.AsArray().Should().HaveCount(2);
        docs["docs"]![0]!["Name"]!.GetValue<string>().Should().Be("guide");
        var guide = await ReadAsync(server.Client, "/api/docs/guide");
        guide["body"]!.GetValue<string>().Should().NotContain(":7477");
        var all = string.Join('\n', UiAssets.Documents.Select(d => System.Text.Encoding.UTF8.GetString(UiAssets.Read("Documentation/" + d.File))));
        all.Should().Contain(":17477").And.NotContain(":7477");
    }
    [Theory]
    [InlineData("0.0.0.0:17477")]
    [InlineData("example.com:17477")]
    [InlineData("127.0.0.1:99999")]
    [InlineData("localhost:17477/path")]
    public void NonLoopbackAndMalformedListenerAddressesAreRejected(string address) =>
        FluentActions.Invoking(() => UiHost.ParseAddress(address)).Should().Throw<ArgumentException>();
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("localhost")]
    [InlineData("[::1]")]
    public void LoopbackHostsAreRecognized(string host) => UiHost.IsLoopback(host).Should().BeTrue();
    [Fact]
    public async Task HostAndTraversalChecksRunBeforeServingAssetsOrApiContent()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var server = await UiFixture.StartAsync(fixture);
        foreach (var path in new[] { "/", "/app.js", "/api/machine", "/api/docs" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = "evil.example";
            using var response = await server.Client.SendAsync(request, Token);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await response.Content.ReadAsStringAsync(Token)).Should().Contain("only loopback hosts");
        }
        foreach (var path in new[] { "/%2e%2e/config.toml", "/%252e%252e/config.toml", "/..%5cconfig.toml", "/Documentation/guide.md", "/api/unknown" })
        {
            using var response = await server.Client.GetAsync(path, Token);
            response.IsSuccessStatusCode.Should().BeFalse(path);
        }
        var projects = await ReadAsync(server.Client, "/api/projects");
        var id = projects["projects"]![0]!["id"]!.GetValue<string>();
        foreach (var at in new[] { "../global/config.toml:1", "/etc/passwd:1", "C:/Windows/win.ini:1" })
        {
            using var response = await server.Client.GetAsync($"/api/projects/{id}/snippet?at={Uri.EscapeDataString(at)}", Token);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        foreach (var endpoint in new[] { "architecture", "coupling", "health?probe=1" })
        {
            using var response = await server.Client.GetAsync($"/api/projects/{id}/{endpoint}", Token);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }
    [Fact]
    public async Task ReadRoutesAreIndependentlyAvailableAndNeverLaunchConfiguredProviders()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write("example.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        fixture.Write("a.cs", "public sealed class Example { }\n");
        fixture.Write(".harness/config.toml", "[providers.must-not-start]\ntype = 'harness'\ncommand = 'missing-provider-ui-sentinel'\n");
        await fixture.CommitAsync("web fixture");
        var workspaceStore = new WorkspaceStore(fixture.Lifecycle.GlobalConfigurationPath, fixture.Lifecycle.Registry);
        var registered = fixture.Lifecycle.Registry.Load()["projects"]![0]!["id"]!.GetValue<string>();
        await workspaceStore.CreateAsync("web", Token);
        await workspaceStore.JoinAsync("web", [registered], Token);
        await using var server = await UiFixture.StartAsync(fixture);
        var projects = await ReadAsync(server.Client, "/api/projects");
        var id = projects["projects"]![0]!["id"]!.GetValue<string>();
        foreach (var endpoint in new[] { "summary", "health", "metrics", "context", "report", "providers", "hooks", "memories", "proposals", "tasks", "milestones", "tasks/TASK-9999/events", "tasks-next-id", "task-proposals", "arch-proposals", "applies", "stack", "structure", "snippet?at=a.cs:1" })
        {
            await ReadAsync(server.Client, $"/api/projects/{id}/{endpoint}");
        }
        foreach (var endpoint in new[] { "dependencies", "issues", "duplicates", "stack", "system", "findings", "adoption?library=repository", "snippet?at=repository:%20a.cs:1" })
        {
            await ReadAsync(server.Client, "/api/workspaces/web/" + endpoint);
        }
        foreach (var path in new[] { "/api/workspaces", "/api/machine", "/api/activity", "/api/stacks", "/api/knowledge?level=user", "/api/search?q=repo", "/api/workspaces/discover?folder=" + Uri.EscapeDataString(fixture.DirectoryRoot) })
        {
            await ReadAsync(server.Client, path);
        }
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => server.Client.GetAsync("/api/projects", Token)));
        foreach (var response in responses)
        { response.StatusCode.Should().Be(HttpStatusCode.OK); response.Dispose(); }
        using var invalidDays = await server.Client.GetAsync($"/api/projects/{id}/metrics?days=0", Token);
        invalidDays.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
    internal static async Task<JsonNode> ReadAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Token);
        var body = await response.Content.ReadAsStringAsync(Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK, path + ": " + body);
        return JsonNode.Parse(body)!;
    }
    [Fact]
    public async Task OnlyAnExplicitTrustedProbeStartsTheProviderProcess()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var marker = Path.Combine(fixture.DirectoryRoot, "provider-started");
        fixture.Write(".harness/config.toml", $"[providers.lazy]\ntype = 'harness'\ncommand = 'dotnet'\ntimeout_ms = 2000\nargs = [{JsonSerializer.Serialize(ProviderFixture.Worker)}, 'descendant', {JsonSerializer.Serialize(marker)}]\n");
        await using var server = await UiFixture.StartAsync(fixture);
        var projects = await ReadAsync(server.Client, "/api/projects");
        var id = projects["projects"]![0]!["id"]!.GetValue<string>();
        var providers = await ReadAsync(server.Client, $"/api/projects/{id}/providers");
        providers["providers"]![0]!["name"]!.GetValue<string>().Should().Be("lazy");
        await ReadAsync(server.Client, $"/api/projects/{id}/health");
        File.Exists(marker).Should().BeFalse();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{id}/health?probe=1");
        request.Headers.Add("X-Harness-UI", "1");
        using var response = await server.Client.SendAsync(request, Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var health = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        health["probed"]!.GetValue<bool>().Should().BeTrue();
        health["healthy"]!.GetValue<bool>().Should().BeFalse();
        File.Exists(marker).Should().BeTrue();
    }
    [Fact]
    public async Task ExistingReadStateIsNotCreatedOrMigratedAndMachineMetadataExcludesTokens()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Sql("DELETE FROM schema_migrations WHERE version=19");
        await File.WriteAllTextAsync(Path.Combine(fixture.Global, "telemetry.json"), """{"enabled":true,"endpoint":"https://telemetry.invalid","token":"must-stay-private","projects":{}}""", Token);
        await using var server = await UiFixture.StartAsync(fixture);
        var projects = await ReadAsync(server.Client, "/api/projects");
        var id = projects["projects"]![0]!["id"]!.GetValue<string>();
        projects["projects"]![0]!["live"]!["state"]!.GetValue<string>().Should().Be("schema_outdated");
        using var tasks = await server.Client.GetAsync($"/api/projects/{id}/tasks", Token);
        tasks.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var machine = await ReadAsync(server.Client, "/api/machine");
        machine.ToJsonString().Should().NotContain("must-stay-private").And.NotContain("\"token\"");
        await ReadAsync(server.Client, "/api/knowledge?level=user");
        File.Exists(Path.Combine(fixture.Global, "knowledge", "user.db")).Should().BeFalse();
        using var db = HarnessDatabase.OpenReadOnly(fixture.DatabasePath);
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_migrations";
        Convert.ToInt32(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture).Should().Be(18);
    }
    [Fact]
    public async Task FindingsReuseTheRequestsArchitectureReadWithoutReopeningFiles()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write("example.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        await fixture.CommitAsync("cached architecture");
        var id = fixture.Lifecycle.Registry.Load()["projects"]![0]!["id"]!.GetValue<string>();
        var member = new Harness.Core.Workspaces.WorkspaceMember("repository", id, fixture.Root, fixture.DatabasePath);
        var reader = new ArchitectureReader([member]);
        var read = await reader.ProjectAsync(member, null, true, Token);
        var repo = await reader.RepositoryAsync(member, Token);
        var reads = repo.PhysicalReads;
        using var database = fixture.Database();
        var findings = await new FindingSets(fixture.Root, id, database, fixture.Lifecycle.GlobalConfigurationPath, fixture.Lifecycle.Registry, read).ReadAsync("project", Token);
        findings.Groups.Should().ContainSingle();
        repo.PhysicalReads.Should().Be(reads);
        repo.Read("example.csproj").Should().Contain("net10.0");
        repo.PhysicalReads.Should().Be(reads);
    }
}
