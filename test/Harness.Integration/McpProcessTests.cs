using System.Collections.Concurrent;
using Harness.Infrastructure.Storage;
using ModelContextProtocol.Client;
using Xunit;

namespace Harness.Integration;

public sealed class McpProcessTests
{
    [Fact]
    public async Task OfficialSdkPersistsStateAcrossProcessesAndDisposesStorageOnDisconnect()
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var binary = Binary();
            ConcurrentQueue<string> errors = new();
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["HARNESS_CONFIG_DIR"] = Path.Combine(root, "config");
            var options = new StdioClientTransportOptions
            {
                Command = "dotnet", Arguments = [binary, "mcp", "--project", root], WorkingDirectory = root,
                EnvironmentVariables = environment, InheritEnvironmentVariables = false, StandardErrorLines = errors.Enqueue,
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            };
            var path = Path.Combine(root, ".harness", "harness.db");
            await using (var client = await McpClient.CreateAsync(new StdioClientTransport(options), cancellationToken: TestContext.Current.CancellationToken))
            {
                Assert.Equal("harness", client.ServerInfo.Name);
                var ping = await client.CallToolAsync("ping", cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal("pong", ping.StructuredContent!.Value.GetProperty("message").GetString());
                Assert.False(File.Exists(path));
                var diagnostics = await client.CallToolAsync("diagnostics", cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(diagnostics.StructuredContent!.Value.TryGetProperty("db_error", out _));
                Assert.False(Directory.Exists(Path.Combine(root, ".harness")));
                var put = await client.CallToolAsync("state_put_test", new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = "Unicode 🧪", ["value"] = "žluťoučký" }, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Null(put.IsError);
            }

            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.True(exclusive.Length > 0);
            }

            await using (var client = await McpClient.CreateAsync(new StdioClientTransport(options), cancellationToken: TestContext.Current.CancellationToken))
            {
                var read = await client.CallToolAsync("state_get_test", new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = "Unicode 🧪" }, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(read.StructuredContent!.Value.GetProperty("found").GetBoolean());
                Assert.Equal("žluťoučký", read.StructuredContent.Value.GetProperty("value").GetString());
            }

            using var database = HarnessDatabase.OpenReadOnly(path);
            Assert.Equal(19, database.SchemaVersion());
            Assert.Equal("žluťoučký", database.GetDiagnostic("Unicode 🧪"));
            Assert.DoesNotContain(errors, line => line.Contains("Unhandled exception", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Binary()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        return Path.Combine(directory.FullName, "src", "Harness.Cli", "bin", Configuration, "net10.0", "harness.dll");
    }
}
