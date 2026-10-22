using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using Xunit;

namespace Harness.Unit;

public sealed class CliTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-help")]
    [InlineData("help")]
    public async Task HelpWritesOnlyToStdout(string argument)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        (await Run([argument], output, error)).Should().Be(0);
        output.ToString().Should().Contain("version");
        error.ToString().Should().BeEmpty();
        output.ToString().Should().NotContain('\u001b'.ToString());
    }

    [Fact]
    public async Task BareInvocationShowsHelpAndHelpVersionShowsItsFlags()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        (await Run([], output, error)).Should().Be(0);
        output.ToString().Should().Contain("version");
        output.GetStringBuilder().Clear();
        (await Run(["help", "version"], output, error)).Should().Be(0);
        output.ToString().Should().Contain("--json");
        error.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("-v")]
    [InlineData("--version")]
    public async Task VersionUsesOneInjectedServiceAndPreservesTheJsonEnvelope(string argument)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var info = new BuildInformation(
            "test",
            "0123456789abcdef",
            "2026-10-20T00:00:00Z",
            true,
            ".NET test",
            "windows/amd64"
        );
        (
            await CliApplication.RunConfiguredAsync(
                [argument, "--json"],
                output,
                error,
                builder => builder.Services.AddSingleton(info),
                null,
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(0);
        using var json = JsonDocument.Parse(output.ToString());
        json.RootElement.EnumerateObject().Count().Should().Be(6);
        json.RootElement.GetProperty("version").GetString().Should().Be("test");
        json.RootElement.GetProperty("modified").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("go_version").GetString().Should().Be(".NET test");
        json.RootElement.GetProperty("date").GetString().Should().Be(info.Date);
        error.ToString().Should().BeEmpty();
    }

    [Theory]
    [InlineData("unknown-command")]
    [InlineData("version", "--unknown")]
    [InlineData("version", "unexpected")]
    public async Task InvalidInputReturnsUsageErrorOnStderr(params string[] arguments)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        (await Run(arguments, output, error)).Should().Be(2);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().NotBeEmpty();
    }

    [Fact]
    public void HumanVersionHasStableFormattingAndExplicitUnknownValues()
    {
        new BuildInformation("dev", "", "", false, ".NET test", "linux/arm64")
            .ToString()
            .Should()
            .Be("harness dev (commit unknown, built unknown, .NET test, linux/arm64)");
        new BuildInformation("v1", "0123456789abcdef", "date", true, ".NET test", "linux/arm64")
            .ToString()
            .Should()
            .Contain("0123456789ab-dirty");
    }

    [Fact]
    public async Task OperationalFailuresUseStderrAndExitOne()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        (
            await CliApplication.RunConfiguredAsync(
                ["fail"],
                output,
                error,
                null,
                configuration =>
                    configuration.AddDelegate<ProbeSettings>(
                        "fail",
                        (_, _, _) => throw new IOException("probe failure")
                    ),
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(1);
        output.ToString().Should().BeEmpty();
        error.ToString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task HostServicesAreSharedDisposedAndLoggingGoesToStderr()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var state = new ProbeState();
        (
            await CliApplication.RunConfiguredAsync(
                ["probe"],
                output,
                error,
                builder =>
                {
                    builder.Services.AddSingleton(state);
                    builder.Services.AddSingleton<ProbeResource>();
                },
                configuration => configuration.AddCommand<ProbeCommand>("probe"),
                TestContext.Current.CancellationToken
            )
        ).Should().Be(0);
        state.Disposals.Should().Be(1);
        state.Shared.Should().BeTrue();
        output.ToString().Should().BeEmpty();
        error.ToString().Should().Contain("probe message");
        await output.WriteAsync("writer remains owned by caller");
    }

    [Fact]
    [SuppressMessage(
        "Reliability",
        "CA2025:Do not pass disposables into unawaited tasks",
        Justification = "The running task is awaited in finally after cancellation, before either writer is disposed."
    )]
    public async Task CancellationReachesCommandsAndDisposesTheHost()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var state = new ProbeState();
        var running = CliApplication.RunConfiguredAsync(
            ["wait"],
            output,
            error,
            builder =>
            {
                builder.Services.AddSingleton(state);
                builder.Services.AddSingleton<ProbeResource>();
            },
            configuration => configuration.AddCommand<WaitCommand>("wait"),
            cancellation.Token
        );
        try
        {
            await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            (await running).Should().Be(130);
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
        state.Disposals.Should().Be(1);
        output.ToString().Should().BeEmpty();
    }

    private static Task<int> Run(string[] args, TextWriter output, TextWriter error) =>
        CliApplication.RunAsync(args, output, error, TestContext.Current.CancellationToken);

    internal sealed class ProbeState
    {
        public int Disposals { get; set; }
        public bool Shared { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class ProbeResource(ProbeState state) : IAsyncDisposable
    {
        public ProbeState State { get; } = state;

        public ValueTask DisposeAsync()
        {
            State.Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class ProbeSettings : CommandSettings;

    internal sealed class ProbeCommand(ProbeState state, ProbeResource resource, ILogger<ProbeCommand> logger)
        : Command<ProbeSettings>
    {
        public override int Execute(CommandContext context, ProbeSettings settings, CancellationToken cancellationToken)
        {
            state.Shared = ReferenceEquals(state, resource.State);
            logger.LogInformation("probe message");
            return 0;
        }
    }

    internal sealed class WaitCommand(ProbeState state, ProbeResource resource) : AsyncCommand<ProbeSettings>
    {
        public override async Task<int> ExecuteAsync(
            CommandContext context,
            ProbeSettings settings,
            CancellationToken cancellationToken
        )
        {
            state.Shared = ReferenceEquals(state, resource.State);
            state.Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
