using System.Net;
using System.Text;
using Avalonia.Controls;
using AwesomeAssertions;
using Harness.Ui.Hosting;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>
/// The desktop app's server (TASK-0214): connecting to a harness ui already on the address, starting one where nothing
/// answers (with a fake harness), and the errors for a missing harness, a server that exits or never answers, a port held
/// by another program and an address on another computer.
/// </summary>
public sealed class UiServerTests : IDisposable
{
    private static readonly Uri Local = new("http://127.0.0.1:17477");
    private readonly string _app = Directory.CreateTempSubdirectory("harness-ui-server-").FullName;
    private readonly FakeApi _api = new();
    private readonly FakeStarter _starter;

    public UiServerTests() => _starter = new FakeStarter(_api);

    public void Dispose()
    {
        _api.Dispose();
        Directory.Delete(_app, recursive: true);
    }

    private string Harness => Path.Combine(_app, OperatingSystem.IsWindows() ? "harness.exe" : "harness");

    private UiServer Server() =>
        new(_api, _starter)
        {
            AppDirectory = _app,
            SearchPath = "",
            ProbeTimeout = TimeSpan.FromMilliseconds(200),
            StartTimeout = TimeSpan.FromMilliseconds(600),
            PollInterval = TimeSpan.FromMilliseconds(20),
        };

    [Fact]
    public async Task AHarnessUiAlreadyOnTheAddressIsUsedAndNothingStarts()
    {
        _api.Answer = Projects;
        using var server = Server();
        var status = new List<string>();

        await server.ConnectAsync(Local, new Inline(status), TestContext.Current.CancellationToken);

        _starter.Starts.Should().BeEmpty();
        server.Started.Should().BeNull();
        status.Should().Equal("Connecting to harness ui at http://127.0.0.1:17477…");
        _api.Requests.Should().ContainSingle().Which.Should().Be("http://127.0.0.1:17477/api/projects");
    }

    [Fact]
    public async Task WhereNothingAnswersItStartsHarnessUiBesideTheAppConnectsAndStopsItOnExit()
    {
        await File.WriteAllTextAsync(Harness, "", TestContext.Current.CancellationToken);
        _starter.OnStart = () => _api.Answer = Projects;
        var status = new List<string>();
        FakeProcess process;
        using (var server = Server())
        {
            await server.ConnectAsync(Local, new Inline(status), TestContext.Current.CancellationToken);

            var start = _starter.Starts.Should().ContainSingle().Subject;
            start.File.Should().Be(Harness);
            start.Arguments.Should().Equal("ui", "--no-open", "--addr", "127.0.0.1:17477");
            status.Should().Equal("Connecting to harness ui at http://127.0.0.1:17477…", "Starting harness ui at http://127.0.0.1:17477…");
            process = (FakeProcess)server.Started!;
            process.Stopped.Should().BeFalse();
        }
        process.Stopped.Should().BeTrue("a server the app started goes with it");
    }

    [Fact]
    public async Task HarnessIsFoundOnPathWhenItIsNotBesideTheApp()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_app, "bin")).FullName;
        var onPath = Path.Combine(bin, Path.GetFileName(Harness));
        await File.WriteAllTextAsync(onPath, "", TestContext.Current.CancellationToken);

        UiServer.Locate(Path.Combine(_app, "elsewhere"), "missing" + Path.PathSeparator + bin, OperatingSystem.IsWindows()).Should().Be(onPath);
        await File.WriteAllTextAsync(Harness, "", TestContext.Current.CancellationToken);
        UiServer.Locate(_app, bin, OperatingSystem.IsWindows()).Should().Be(Harness, "the one beside the app comes first");
        UiServer.Locate(Path.Combine(_app, "elsewhere"), "", OperatingSystem.IsWindows()).Should().BeNull();
    }

    [Fact]
    public async Task WithoutHarnessItSaysSoAndHowToStartIt()
    {
        using var server = Server();

        var failure = await Assert.ThrowsAsync<UiServerException>(() => server.ConnectAsync(Local, null, TestContext.Current.CancellationToken));

        failure.Message.Should().Contain("No harness ui answers at http://127.0.0.1:17477").And.Contain("not found")
            .And.Contain("harness ui --no-open --addr 127.0.0.1:17477");
        _starter.Starts.Should().BeEmpty();
    }

    [Fact]
    public async Task AServerThatExitsEarlyIsReportedWithItsExitCodeAndLastOutput()
    {
        await File.WriteAllTextAsync(Harness, "", TestContext.Current.CancellationToken);
        _starter.Exit = (3, ["harness ui on 127.0.0.1:17477", "error: address already in use"]);
        using var server = Server();

        var failure = await Assert.ThrowsAsync<UiServerException>(() => server.ConnectAsync(Local, null, TestContext.Current.CancellationToken));

        failure.Message.Should().Contain("exited with code 3").And.Contain("http://127.0.0.1:17477")
            .And.Contain($"Command: {Harness} ui --no-open --addr 127.0.0.1:17477").And.Contain("error: address already in use");
        server.Started.Should().BeNull();
    }

    [Fact]
    public async Task AServerThatNeverAnswersIsStoppedAndReported()
    {
        await File.WriteAllTextAsync(Harness, "", TestContext.Current.CancellationToken);
        _starter.Lines = ["still starting"];
        using var server = Server();

        var failure = await Assert.ThrowsAsync<UiServerException>(() => server.ConnectAsync(Local, null, TestContext.Current.CancellationToken));

        failure.Message.Should().Contain("did not answer at http://127.0.0.1:17477").And.Contain("Command: ").And.Contain("still starting");
        _starter.Processes.Should().ContainSingle().Which.Stopped.Should().BeTrue();
        server.Started.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "text/html", "<html>another app</html>")]
    [InlineData(HttpStatusCode.NotFound, "application/json", """{"error":"not found"}""")]
    [InlineData(HttpStatusCode.OK, "application/json", """{"items":[]}""")]
    public async Task AnotherProgramOnThePortIsNeitherUsedNorFoughtFor(HttpStatusCode code, string type, string body)
    {
        await File.WriteAllTextAsync(Harness, "", TestContext.Current.CancellationToken);
        _api.Answer = () => new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, type) };
        using var server = Server();

        (await server.ProbeAsync(Local, TestContext.Current.CancellationToken)).Should().Be(UiServerProbe.Foreign);
        var failure = await Assert.ThrowsAsync<UiServerException>(() => server.ConnectAsync(Local, null, TestContext.Current.CancellationToken));

        failure.Message.Should().Contain("Something other than harness ui answers at http://127.0.0.1:17477");
        _starter.Starts.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddressOnAnotherComputerIsNeverStarted()
    {
        await File.WriteAllTextAsync(Harness, "", TestContext.Current.CancellationToken);
        using var server = Server();

        var failure = await Assert.ThrowsAsync<UiServerException>(
            () => server.ConnectAsync(new Uri("http://10.1.2.3:17477"), null, TestContext.Current.CancellationToken)
        );

        failure.Message.Should().Contain("No harness ui answers at http://10.1.2.3:17477").And.Contain("not on this computer");
        _starter.Starts.Should().BeEmpty();
        UiServer.IsLoopback(new Uri("http://localhost:17477")).Should().BeTrue();
        UiServer.IsLoopback(new Uri("http://[::1]:17477")).Should().BeTrue();
        UiServer.IsLoopback(new Uri("http://127.0.0.2:17477")).Should().BeTrue();
    }

    [Fact]
    public Task TheWindowShowsWhatItIsDoingThenWhyItCouldNotConnect() => Ui.RunAsync(async () =>
    {
        using var http = new HttpClient(new FixtureApi()) { BaseAddress = Ui.Server };
        var gate = new TaskCompletionSource();
        var startup = new UiStartup
        {
            Api = new Api.HarnessApi(http),
            Host = new ViewModels.UiHostContext { Server = Ui.Server },
            Connect = async (status, _) =>
            {
                status.Report("Starting harness ui at http://127.0.0.1:17477…");
                await gate.Task;
                throw new UiServerException("harness ui exited with code 3 before it answered.\nCommand: harness ui --no-open");
            },
        };
        var host = new ContentControl();
        var window = Ui.Show(host, 800, 600);

        var shown = App.ShowAsync(startup, host);
        await Ui.UntilAsync(() => host.Content is StartupView { Status: "Starting harness ui at http://127.0.0.1:17477…" });
        gate.SetResult();
        await shown;

        var view = (StartupView)host.Content!;
        view.Status.Should().Be("Could not reach harness ui.");
        view.Error.Should().Contain("exited with code 3").And.Contain("Command: harness ui --no-open");
        UiChecks.Check(window).Should().BeEmpty();
        window.Close();
    });

    private static HttpResponseMessage Projects() =>
        new(HttpStatusCode.OK) { Content = new StringContent("""{"projects":[]}""", Encoding.UTF8, "application/json") };

    /// <summary>Reports at once (a Progress would post to a context the test does not run).</summary>
    private sealed class Inline(List<string> seen) : IProgress<string>
    {
        public void Report(string value) => seen.Add(value);
    }

    /// <summary>The address: refuses connections until <see cref="Answer"/> is set.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public Func<HttpResponseMessage>? Answer { get; set; }
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!.ToString());
            }
            return Answer is { } answer
                ? Task.FromResult(answer())
                : Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        }
    }

    private sealed class FakeStarter(FakeApi api) : IServerProcessStarter
    {
        public List<(string File, IReadOnlyList<string> Arguments)> Starts { get; } = [];
        public List<FakeProcess> Processes { get; } = [];
        public Action? OnStart { get; set; }
        public (int Code, string[] Lines)? Exit { get; set; }
        public string[] Lines { get; set; } = [];

        public IServerProcess Start(string file, IReadOnlyList<string> arguments)
        {
            _ = api;
            Starts.Add((file, arguments));
            OnStart?.Invoke();
            var process = new FakeProcess(Exit?.Lines ?? Lines) { HasExited = Exit is not null, ExitCode = Exit?.Code ?? 0 };
            Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess(string[] lines) : IServerProcess
    {
        public bool HasExited { get; init; }
        public int ExitCode { get; init; }
        public IReadOnlyList<string> LastLines => lines;
        public bool Stopped { get; private set; }

        public void Stop() => Stopped = true;

        public void Dispose()
        {
            // nothing to release
        }
    }
}
