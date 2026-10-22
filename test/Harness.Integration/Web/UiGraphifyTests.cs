using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Integration.Providers;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Web;

[Collection("Provider processes")]
public sealed class UiGraphifyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InvalidSessionExecutableRetainsTheJsonErrorEnvelope()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var executable = Path.Combine(fixture.DirectoryRoot, "invalid-graphify.exe");
        await File.WriteAllTextAsync(executable, "This is not an executable", Token);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        await File.WriteAllTextAsync(
            fixture.Lifecycle.GlobalConfigurationPath,
            "[providers.graphify-csharp]\nargs = ['--graphify', '" + executable.Replace('\\', '/') + "']\n",
            Token
        );
        await using var server = await UiFixture.StartAsync(fixture);
        using var request = UiWriteTests.Request(
            server.Client,
            "/api/graphify/sessions/start",
            new JsonObject { ["project"] = UiWriteTests.ProjectId(fixture) }.ToJsonString()
        );
        using var response = await server.Client.SendAsync(request, Token);
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!["error"]!
            .GetValue<string>()
            .Should()
            .NotBeEmpty();
    }

    [Fact]
    public async Task SessionControlsStartReuseRestartAndStopOnlyTheSelectedSession()
    {
        using var fixture = await SyncFixture.CreateAsync();
        using var graphify = new GraphifyFixture();
        fixture.Write("App.slnx", "<Solution />");
        var executable = Path.ChangeExtension(ProviderFixture.Worker, ".exe");
        if (!OperatingSystem.IsWindows())
        {
            executable = Path.Combine(fixture.DirectoryRoot, "graphify-fixture");
            await File.WriteAllTextAsync(
                executable,
                "#!/bin/sh\nexec dotnet '"
                    + ProviderFixture.Worker.Replace("'", "'\\''", StringComparison.Ordinal)
                    + "' \"$@\"\n",
                Token
            );
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        await File.WriteAllTextAsync(
            fixture.Lifecycle.GlobalConfigurationPath,
            "[providers.graphify-csharp]\nargs = ['--graphify', '" + executable.Replace('\\', '/') + "']\n",
            Token
        );
        await using var server = await UiFixture.StartAsync(
            fixture,
            environment: new Dictionary<string, string> { ["HARNESS_FAKE_GRAPHIFY"] = graphify.Data }
        );
        var project = new JsonObject { ["project"] = UiWriteTests.ProjectId(fixture) }.ToJsonString();
        (await UiWriteTests.PostAsync(server.Client, "/api/graphify/sessions/start", project))["changed"]!
            .GetValue<int>()
            .Should()
            .Be(1);
        await ProviderFixture.EventuallyAsync(() => File.Exists(Path.Combine(graphify.Data, "sessions.json")));
        (await UiWriteTests.PostAsync(server.Client, "/api/graphify/sessions/start", project))["changed"]!
            .GetValue<int>()
            .Should()
            .Be(0);
        var first = SessionId(graphify);
        (await UiWriteTests.PostAsync(server.Client, "/api/graphify/sessions/restart", project))["changed"]!
            .GetValue<int>()
            .Should()
            .Be(1);
        await ProviderFixture.EventuallyAsync(() => SessionId(graphify) is { Length: > 0 } next && next != first);
        var next = SessionId(graphify);
        (
            await UiWriteTests.PostAsync(
                server.Client,
                "/api/graphify/sessions/stop",
                new JsonObject { ["session"] = next }.ToJsonString()
            )
        )["stopped"]!
            .GetValue<int>()
            .Should()
            .Be(1);
        (await UiWriteTests.PostAsync(server.Client, "/api/graphify/sessions/stop", project))["changed"]!
            .GetValue<int>()
            .Should()
            .Be(0);
        graphify.Calls().Where(c => c[0] is "stop").Select(c => c[1]).Should().Equal(first, next);
        var records = await File.ReadAllTextAsync(Path.Combine(fixture.Global, "graphify-sessions.json"), Token);
        records.Should().Contain("harness ui (by hand)");
    }

    private static string SessionId(GraphifyFixture fixture)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Data, "sessions.json")));
            var sessions = document.RootElement.GetProperty("sessions");
            return sessions.GetArrayLength() > 0 ? sessions[0].GetProperty("session_id").GetString()! : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
