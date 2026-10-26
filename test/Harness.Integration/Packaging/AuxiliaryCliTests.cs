using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Integration.Providers;
using Harness.Integration.Web;
using Xunit;

namespace Harness.Integration.Packaging;

[Collection("Provider processes")]
public sealed class AuxiliaryCliTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmbeddedDocsAndRootCommandTreeExposeSupportedEntryPoints()
    {
        using var fixture = new GraphifyFixture();
        var help = await RunAsync(fixture, "--help");
        help.Code.Should().Be(0);
        foreach (
            var name in new[]
            {
                "version",
                "init",
                "status",
                "doctor",
                "sync",
                "mcp",
                "ui",
                "install",
                "uninstall",
                "instructions",
                "hooks",
                "tasks",
                "proposals",
                "memory",
                "context",
                "projects",
                "workspace",
                "docs",
                "providers",
                "completion",
                "telemetry",
                "metrics",
                "report",
            }
        )
        {
            help.Output.Should().MatchRegex(@"(?m)^\s*(?:[\w-]+,\s*)*" + name + @"(?:\s|,|$)", name);
        }
        var list = await RunAsync(fixture, "docs");
        list.Code.Should().Be(0);
        list.Output.Should().Contain("Using harness:").And.Contain("Reference:").And.Contain("harness docs <section>");
        var guide = await RunAsync(fixture, "docs", "guide");
        guide.Code.Should().Be(0);
        guide.Output.Should().Contain("harness");
        var missing = await RunAsync(fixture, "docs", "no-such-doc-xyz");
        missing.Code.Should().Be(2);
        missing.Error.Should().Contain("no section");
    }

    [Fact]
    public async Task ProviderSessionCliListsAndControlsOnlyTheRequestedProjectOrPrefix()
    {
        using var fixture = new GraphifyFixture();
        fixture.Ready("borrowed-session");
        var command = Path.ChangeExtension(ProviderFixture.Worker, ".exe");
        if (!OperatingSystem.IsWindows())
        {
            command = Path.Combine(fixture.DirectoryRoot, "backend");
            await File.WriteAllTextAsync(
                command,
                "#!/bin/sh\nexec dotnet \"$HARNESS_GRAPHIFY_TEST_WORKER\" \"$@\"\n",
                Token
            );
            File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var configuration = Path.Combine(Path.GetDirectoryName(fixture.RecordsPath)!, "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configuration)!);
        await File.WriteAllTextAsync(
            configuration,
            "[providers.graphify-csharp]\npack = 'graphify-csharp'\nargs = "
                + JsonSerializer.Serialize(new[] { "--graphify", command })
                + "\n",
            Token
        );
        var list = await RunAsync(fixture, "providers", "--json");
        list.Code.Should().Be(0, list.Error);
        var sessions = JsonNode.Parse(list.Output)!["sessions"]!.AsArray();
        sessions.Should().ContainSingle();
        var session = sessions[0]!;
        session["id"]!.GetValue<string>().Should().Be("borrowed-session");
        session.AsObject().Should().NotContainKey("started_by");
        var stop = await RunAsync(fixture, "providers", "sessions", "stop", "borr");
        stop.Code.Should().Be(0, stop.Error);
        stop.Output.Should().Contain("Stopped borrowed");
        (await RunAsync(fixture, "providers", "sessions")).Output.Should().Contain("No graphify sessions.");
        (await RunAsync(fixture, "providers", "sessions", "stop", "missing")).Code.Should().Be(1);
        var start = await RunAsync(fixture, "providers", "sessions", "start", "--project", fixture.Root);
        start.Code.Should().Be(0, start.Error);
        await ProviderFixture.EventuallyAsync(() => fixture.Calls().Any(c => c.Length > 0 && c[0] is "watch"));
        await ProviderFixture.EventuallyAsync(() => fixture.Sessions()["sessions"]!.AsArray().Count > 0);
        var stopped = await RunAsync(fixture, "providers", "sessions", "stop", "--project", fixture.Root);
        stopped.Code.Should().Be(0, stopped.Error);
        stopped.Output.Should().Contain("Stopped 1 session(s)");
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(
        GraphifyFixture fixture,
        params string[] args
    )
    {
        using var process = new Process
        {
            StartInfo = new("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = fixture.Root,
            },
        };
        process.StartInfo.ArgumentList.Add(UiFixture.CliPath);
        foreach (var argument in args)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.Environment["HARNESS_CONFIG_DIR"] = Path.GetDirectoryName(fixture.RecordsPath);
        process.StartInfo.Environment["HARNESS_FAKE_GRAPHIFY"] = fixture.Data;
        process.StartInfo.Environment["HARNESS_GRAPHIFY_TEST_WORKER"] = ProviderFixture.Worker;
        process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(45), Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
