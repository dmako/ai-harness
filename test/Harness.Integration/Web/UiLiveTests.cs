using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Infrastructure.Memory;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Web;

[Collection("Provider processes")]
public sealed class UiLiveTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StreamDetectsIndependentDatabaseAndRegistryWritesAndReconnects()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var server = await UiFixture.StartAsync(fixture);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        using var response = await server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Token));
        (await FrameAsync(reader)).Should().Contain("retry: 3000");
        // The shared watcher establishes its read-only baseline off the request thread.
        await Task.Delay(TimeSpan.FromSeconds(2), Token);
        using (var database = fixture.Database())
        {
            new MemoryStore(database).Put(new MemoryInput { Type = "goal", Content = "Stream notices an independent SQLite writer" }, Token);
        }
        var changed = await ChangeAsync(reader);
        var id = UiWriteTests.ProjectId(fixture);
        changed["projects"]!.AsArray().Select(p => p!.GetValue<string>()).Should().Contain(id);
        changed["kinds"]![id]!.AsArray().Select(p => p!.GetValue<string>()).Should().Contain("knowledge");
        changed.ToJsonString().Should().NotContain("Stream notices");
        await fixture.Lifecycle.Registry.RenameAsync(id, "changed-outside-server", null, Token);
        (await ChangeAsync(reader))["registry"]!.GetValue<bool>().Should().BeTrue();
        await UiWriteTests.PostAsync(server.Client, "/api/workspaces", """{"name":"live"}""");
        (await ChangeAsync(reader))["workspaces"]!.GetValue<bool>().Should().BeTrue();
        response.Dispose();
        using var again = await server.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, Token);
        using var againReader = new StreamReader(await again.Content.ReadAsStreamAsync(Token));
        (await FrameAsync(againReader)).Should().Contain("retry: 3000");
        (await UiTests.ReadAsync(server.Client, "/api/projects"))["projects"]![0]!["name"]!.GetValue<string>().Should().Be("changed-outside-server");
    }

    [Fact]
    public async Task EventStreamsAreLimitedAndForeignHostsAreRejected()
    {
        using var fixture = await SyncFixture.CreateAsync();
        await using var server = await UiFixture.StartAsync(fixture);
        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        foreign.Headers.Host = "foreign.example";
        using var rejected = await server.Client.SendAsync(foreign, Token);
        rejected.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        List<HttpResponseMessage> streams = [];
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var stream = await server.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, Token);
                streams.Add(stream);
                stream.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            using var excess = await server.Client.GetAsync("/api/events", Token);
            excess.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            JsonNode.Parse(await excess.Content.ReadAsStringAsync(Token))!["error"]!.GetValue<string>().Should().Contain("too many");
        }
        finally
        {
            foreach (var stream in streams)
            { stream.Dispose(); }
        }
    }

    internal static async Task<string> FrameAsync(StreamReader reader)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        List<string> lines = [];
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Length is 0)
            { return string.Join('\n', lines); }
            lines.Add(line);
        }
        throw new IOException("Event stream closed before a complete frame");
    }
    private static async Task<JsonNode> ChangeAsync(StreamReader reader)
    {
        var frame = await FrameAsync(reader);
        frame.Should().StartWith("event: change\ndata: ");
        return JsonNode.Parse(frame["event: change\ndata: ".Length..])!;
    }
}
