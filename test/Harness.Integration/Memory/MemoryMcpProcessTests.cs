using AwesomeAssertions;
using Harness.Infrastructure.Storage;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Integration.Memory;

public sealed class MemoryMcpProcessTests
{
    [Fact]
    public async Task TwoOfficialSdkClientsShareKnowledgeAndRecordTheirOwnIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "harness-memory-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var database = HarnessDatabase.Open(Path.Combine(root, ".harness", "harness.db"), cancellationToken: Token))
                database.SchemaVersion().Should().Be(19);
            await using var claude = await StartAsync(root, "claude-code");
            await using var codex = await StartAsync(root, "codex");
            var tools = await claude.ListToolsAsync(cancellationToken: Token);
            tools.Count.Should().Be(25);
            var created = await Call(claude, "memory_put", new { type = "decision", content = "Use JSON for task import", scope = "path:src/tasks", source_ref = "TASK-0009 review", confidence = (double?)null });
            var old = created.GetProperty("memory").GetProperty("id").GetString()!;
            created.GetProperty("memory").GetProperty("recorded_by").GetString().Should().Be("claude-code fixture");
            var rules = await Call(codex, "decisions_for", new { scope = "path:src/tasks/import.cs" });
            rules.GetProperty("memories")[0].GetProperty("id").GetString().Should().Be(old);
            var replaced = await Call(codex, "memory_supersede", new { id = old, content = "Use YAML for task import", reason = "hand-edited backlog" });
            var current = replaced.GetProperty("memory").GetProperty("id").GetString()!;
            replaced.GetProperty("memory").GetProperty("recorded_by").GetString().Should().Be("codex fixture");
            var history = await Call(claude, "memory_get", new { id = current, history = true });
            history.GetProperty("history").GetArrayLength().Should().Be(2);
            history.GetProperty("events")[0].GetProperty("actor").GetString().Should().Be("codex fixture");
            var proposal = await Call(codex, "memory_propose", new { type = "warning", content = "Do not deploy on Fridays", rationale = "unconfirmed suggestion" });
            var proposalId = proposal.GetProperty("proposal").GetProperty("id").GetString()!;
            var before = await Call(claude, "memory_search", new { types = new[] { "warning" } });
            before.GetProperty("memories").EnumerateArray().Should().BeEmpty();
            var rejected = await Call(claude, "memory_review", new { id = proposalId, action = "reject", note = "not a project rule" });
            rejected.GetProperty("proposal").GetProperty("decided_by").GetString().Should().Be("claude-code fixture");
            var pending = await Call(codex, "memory_proposals", new { });
            pending.GetProperty("proposals").EnumerateArray().Should().BeEmpty();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<System.Text.Json.JsonElement> Call(McpClient client, string name, object args)
    {
        var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(System.Text.Json.JsonSerializer.Serialize(args));
        var result = await client.CallToolAsync(name, values, cancellationToken: Token);
        result.IsError.Should().NotBe(true);
        result.StructuredContent.Should().NotBeNull();
        return result.StructuredContent.Value;
    }

    private static Task<McpClient> StartAsync(string root, string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
            directory = directory.Parent;
        directory.Should().NotBeNull();
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        var binary = Path.Combine(directory.FullName, "src", "Harness.Cli", "bin", Configuration, "net10.0", "harness.dll");
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["HARNESS_CONFIG_DIR"] = Path.Combine(root, "config");
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [binary, "mcp", "--project", root],
            WorkingDirectory = root,
            EnvironmentVariables = environment,
            InheritEnvironmentVariables = false,
        }), new McpClientOptions { ClientInfo = new Implementation { Name = name, Version = "fixture" }, ProtocolVersion = "2025-06-18" }, cancellationToken: Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
