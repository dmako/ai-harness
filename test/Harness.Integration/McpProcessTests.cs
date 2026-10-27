using System.Collections.Concurrent;
using AwesomeAssertions;
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
                Command = "dotnet",
                Arguments = [binary, "mcp", "--project", root],
                WorkingDirectory = root,
                EnvironmentVariables = environment,
                InheritEnvironmentVariables = false,
                StandardErrorLines = errors.Enqueue,
                ShutdownTimeout = TimeSpan.FromSeconds(10),
            };
            var path = Path.Combine(root, ".harness", "harness.db");
            await using (
                var client = await McpClient.CreateAsync(
                    new StdioClientTransport(options),
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            {
                client.ServerInfo.Name.Should().Be("harness");
                var ping = await client.CallToolAsync("ping", cancellationToken: TestContext.Current.CancellationToken);
                ping.StructuredContent!.Value.GetProperty("message").GetString().Should().Be("pong");
                File.Exists(path).Should().BeFalse();
                var diagnostics = await client.CallToolAsync(
                    "diagnostics",
                    cancellationToken: TestContext.Current.CancellationToken
                );
                diagnostics.StructuredContent!.Value.TryGetProperty("db_error", out _).Should().BeTrue();
                Directory.Exists(Path.Combine(root, ".harness")).Should().BeFalse();
                var put = await client.CallToolAsync(
                    "state_put_test",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["key"] = "Unicode 🧪",
                        ["value"] = "žluťoučký",
                    },
                    cancellationToken: TestContext.Current.CancellationToken
                );
                put.IsError.Should().BeNull();
            }

            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                (exclusive.Length > 0).Should().BeTrue();
            }

            await using (
                var client = await McpClient.CreateAsync(
                    new StdioClientTransport(options),
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
            {
                var read = await client.CallToolAsync(
                    "state_get_test",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = "Unicode 🧪" },
                    cancellationToken: TestContext.Current.CancellationToken
                );
                read.StructuredContent!.Value.GetProperty("found").GetBoolean().Should().BeTrue();
                read.StructuredContent.Value.GetProperty("value").GetString().Should().Be("žluťoučký");
            }

            using var database = HarnessDatabase.OpenReadOnly(path);
            database.SchemaVersion().Should().Be(21);
            database.GetDiagnostic("Unicode 🧪").Should().Be("žluťoučký");
            errors.Should().NotContain(line => line.Contains("Unhandled exception", StringComparison.Ordinal));
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    private static string Binary()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull();
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        return Path.Combine(directory.FullName, "src", "Harness.Cli", "bin", Configuration, "net10.0", "harness.dll");
    }
}
