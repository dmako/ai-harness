using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Infrastructure.Processes;
using Harness.Providers.Mcp;
using Xunit;

namespace Harness.Integration.Providers;

/// <summary>
/// A provider's command is found as the shell finds it: on Windows dnx and npx are batch shims (dnx.cmd, npx.cmd),
/// and npx.cmd has an extensionless POSIX script beside it.
/// </summary>
[Collection("Provider processes")]
public sealed class ProviderCommandTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("dnx", "--help", "Executes a tool from source")]
    [InlineData("npx", "--version", ".")]
    public async Task InstalledShimsOnPathStart(string command, string argument, string output)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        Assert.SkipUnless(
            path.Split(Path.PathSeparator).Any(directory => Found(directory, command)),
            command + " is not installed on this machine"
        );

        await using var process = new ProviderProcessFactory().Start(new(command, [argument]));
        using var reader = new StreamReader(process.Output);
        var text = await reader.ReadToEndAsync(Token);

        (await process.Completion.WaitAsync(TimeSpan.FromSeconds(60), Token)).Should().Be(0, text);
        text.Should().Contain(output);
    }

    [Fact]
    public async Task AProviderStartedThroughABatchShimOnPathAnswersAndKeepsItsArguments()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "batch shims are Windows'");
        using var fixture = new ProviderFixture();
        var shims = Path.Combine(fixture.Root, "shims");
        Directory.CreateDirectory(shims);
        // as Node installs npx: a POSIX script, and the .cmd Windows runs
        await File.WriteAllTextAsync(Path.Combine(shims, "fixture-dnx"), "#!/bin/sh\nexec dotnet \"$@\"\n", Token);
        await File.WriteAllTextAsync(Path.Combine(shims, "fixture-dnx.cmd"), "@echo off\r\ndotnet %*\r\n", Token);
        var literal = "space & pipe | caret ^ (paren) <angle> trailing\\";
        var options = fixture.Options("stderr") with
        {
            Process = new("fixture-dnx", [ProviderFixture.Worker, "stderr", fixture.Root, literal])
            {
                WorkingDirectory = fixture.Root,
                Environment = new Dictionary<string, string?>
                {
                    ["PATH"] = shims + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                },
            },
        };

        await using var client = await McpProcessClient.StartAsync(new ProviderProcessFactory(), options, Token);

        client.ServerVersion.Should().Be("provider-fixture 1.0");
        var result = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["literal"] = "x" }, Token);
        McpProcessTests.Body(result).GetProperty("process_arguments")[2].GetString().Should().Be(literal);
    }

    [Theory]
    [InlineData("100%")]
    [InlineData("say \"hi\"")]
    [InlineData("two\nlines")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ABatchShimRefusesArgumentsCmdWouldRewrite(string argument)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "batch shims are Windows'");

        FluentActions
            .Invoking(() => WindowsProviderProcess.CommandLine(@"C:\tools\dnx.cmd", ["NuGet.Mcp.Server", argument]))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*cannot carry a quote, % or line break*");
        WindowsProviderProcess
            .CommandLine(@"C:\tools\server.exe", [argument])
            .Application.Should()
            .Be(@"C:\tools\server.exe", "only cmd.exe rereads its command line");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ABatchShimRunsInCmdWithItsCommandLineQuotedOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "batch shims are Windows'");

        var (application, line) = WindowsProviderProcess.CommandLine(
            @"C:\Program Files\dotnet\dnx.cmd",
            ["NuGet.Mcp.Server", "--yes"]
        );

        application.Should().Be(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        line.Should().EndWith(@" /d /s /c """"C:\Program Files\dotnet\dnx.cmd"" ""NuGet.Mcp.Server"" ""--yes""""");
    }

    [Fact]
    public void WindowsLooksUpABareNameByPathextAndNeverAsAnExtensionlessFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "PATHEXT is Windows'");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.JS",
        };

        ProviderProcessFactory.Extensions("npx", environment).Should().Equal(".COM", ".EXE", ".BAT", ".CMD");
        ProviderProcessFactory.Extensions("dnx.cmd", environment).Should().Equal("");
        ProviderProcessFactory.Extensions("NuGet.Mcp.Server", environment).Should().NotContain("");
        ProviderProcessFactory
            .Extensions("npx", new Dictionary<string, string>())
            .Should()
            .Equal(".COM", ".EXE", ".BAT", ".CMD");
    }

    private static bool Found(string directory, string command) =>
        directory.Length > 0
        && (
            OperatingSystem.IsWindows()
                ? new[] { ".exe", ".cmd", ".bat" }.Any(extension =>
                    File.Exists(Path.Combine(directory, command + extension))
                )
                : File.Exists(Path.Combine(directory, command))
        );
}
