using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Harness.Integration;

public sealed class CliProcessTests
{
    [Fact]
    public async Task ExecutableVersionIsMachineReadableOutsideTheRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        var binary = Path.Combine(directory.FullName, "src", "Harness.Cli", "bin", Configuration, "net10.0", "harness.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(binary);
        start.ArgumentList.Add("version");
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stderr);
            using var json = JsonDocument.Parse(await stdout);
            Assert.StartsWith(".NET ", json.RootElement.GetProperty("go_version").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("commit").ValueKind);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
