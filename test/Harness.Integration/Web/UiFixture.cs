using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Web;

internal sealed class UiFixture : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _errors;
    public HttpClient Client { get; }
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HttpClient owns and disposes its handler.")]
    private UiFixture(Process process, Task<string> errors, string address)
    {
        _process = process;
        _errors = errors;
        Client = new(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(65) };
    }
    internal static string CliPath
    {
        get
        {
#if DEBUG
            const string Configuration = "Debug";
#else
            const string Configuration = "Release";
#endif
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Harness.Cli/bin", Configuration, "net10.0/harness.dll"));
        }
    }
    public static async Task<UiFixture> StartAsync(SyncFixture fixture, string? oracle = null, string? address = "127.0.0.1:0", IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(oracle ?? "dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = fixture.DirectoryRoot };
        if (oracle is null)
        { start.ArgumentList.Add(CliPath); }
        start.ArgumentList.Add("ui");
        if (address is not null)
        { start.ArgumentList.Add("--addr"); start.ArgumentList.Add(address); }
        start.ArgumentList.Add("--no-open");
        start.Environment["HARNESS_CONFIG_DIR"] = fixture.Global;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(fixture.DirectoryRoot, "absent.gitconfig");
        start.Environment["HOME"] = fixture.DirectoryRoot;
        start.Environment["USERPROFILE"] = fixture.DirectoryRoot;
        start.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(fixture.DirectoryRoot, "claude");
        start.Environment["CODEX_HOME"] = Path.Combine(fixture.DirectoryRoot, "codex");
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }
        var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            line.Should().StartWith("harness ui on ", process.HasExited ? await errors : "the listener should announce its bound address");
            return new(process, errors, line["harness ui on ".Length..].Split(' ')[0]);
        }
        catch
        {
            if (!process.HasExited)
            { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            process.Dispose();
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (!_process.HasExited)
        { _process.Kill(true); await _process.WaitForExitAsync(CancellationToken.None); }
        await _errors;
        _process.Dispose();
    }
}
