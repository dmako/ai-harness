using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Integration.Sync;
using Harness.Integration.Web;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Providers;

/// <summary>A slow or stuck provider costs its own answer, never the whole request.</summary>
[Collection("Provider processes")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Providers transfer to the registry, disposed by its await using declaration."
)]
public sealed class ProviderDeadlineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheOverviewAnswersWithWhatArrivedBeforeTheDeadlineNamingWhatDidNot()
    {
        await using var registry = new ProviderRegistry(TimeSpan.FromSeconds(1));
        registry.Register(
            new ProviderRegistryTests.Fake(
                "fast",
                [Capabilities.Overview],
                execute: (_, _) =>
                    Task.FromResult(
                        new ProviderResult { Summary = new Dictionary<string, JsonElement> { ["files"] = Number(3) } }
                    )
            )
        );
        foreach (var name in new[] { "slow-a", "slow-b" })
        {
            registry.Register(
                new ProviderRegistryTests.Fake(
                    name,
                    [Capabilities.Overview],
                    execute: async (_, token) =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), token);
                        return new ProviderResult();
                    }
                )
            );
        }

        registry.Register(new Stuck("stuck"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var clock = Stopwatch.StartNew();

        var (structure, errors) = await registry.OverviewAsync(cancellationToken: deadline.Token);

        structure.Keys.Should().Equal("fast");
        errors.Should().HaveCount(3);
        errors.Should().ContainSingle(error => error.StartsWith("slow-a: timed out", StringComparison.Ordinal));
        errors.Should().ContainSingle(error => error.StartsWith("slow-b: timed out", StringComparison.Ordinal));
        errors.Should().ContainSingle(error => error.StartsWith("stuck:", StringComparison.Ordinal));
        clock
            .Elapsed.Should()
            .BeLessThan(TimeSpan.FromSeconds(6), "the calls run side by side, bounded by the deadline");
    }

    [Fact]
    public async Task HarnessUiShowsTheOverviewOfTheProvidersThatAnsweredWhenOneStalls()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            ".harness/config.toml",
            $$"""
            [providers.stalled]
            type = "mcp"
            command = "dotnet"
            args = [{{JsonSerializer.Serialize(ProviderFixture.Worker)}}, "stall-start", {{JsonSerializer.Serialize(
                fixture.Root
            )}}]
            timeout_ms = 1000

            [providers.stalled.capabilities."project.overview"]
            tool = "overview"
            """
        );
        await using var server = await UiFixture.StartAsync(fixture);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/projects/{UiWriteTests.ProjectId(fixture)}/architecture"
        );
        request.Headers.Add("X-Harness-UI", "1");
        var clock = Stopwatch.StartNew();

        using var response = await server.Client.SendAsync(request, Token);

        var body = await response.Content.ReadAsStringAsync(Token);
        response.IsSuccessStatusCode.Should().BeTrue(body);
        using var answer = JsonDocument.Parse(body);
        answer
            .RootElement.GetProperty("errors")
            .EnumerateArray()
            .Should()
            .ContainSingle(
                error => error.GetString()!.StartsWith("stalled (overview): ", StringComparison.Ordinal),
                body
            );
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the provider's own timeout bounds it");
    }

    private static JsonElement Number(int value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A provider whose discovery never finishes.</summary>
    private sealed class Stuck(string name)
        : ProviderRegistryTests.Fake(name, [Harness.Core.Providers.Capabilities.Overview]),
            IProviderDiscovery
    {
        public Task DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
