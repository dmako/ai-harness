using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Memory;
using Harness.Integration.Providers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Integration.Workspaces;

[Collection("Provider processes")]
public sealed class EvidenceProcessTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublicToolsKeepLocalMemoryProvenanceAndPartialProviderEvidence()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        fixture
            .Repositories[0]
            .Write("Core.csproj", "<Project><PropertyGroup><PackageId>Acme.Core</PackageId></PropertyGroup></Project>");
        fixture
            .Repositories[0]
            .Write(
                "PdfInvoiceGenerator.cs",
                "namespace Acme.Core;\npublic class PdfInvoiceGenerator {}\npublic static class Extensions {}\n"
            );
        fixture
            .Repositories[1]
            .Write(
                "Api.csproj",
                """<Project><ItemGroup><PackageReference Include="Acme.Core" Version="1"/><PackageReference Include="QuestPDF" Version="2"/></ItemGroup></Project>"""
            );
        fixture.Repositories[1].Write("Use.cs", "using Acme.Core;\n");
        fixture
            .Repositories[2]
            .Write("Local.cs", "public class PdfInvoiceGenerator {}\npublic static class Extensions {}\n");
        foreach (var repo in fixture.Repositories)
        {
            repo.Write(
                ".harness/config.toml",
                """
                [providers.heavy]
                type = 'harness'
                command = 'missing-ecosystem-heavy-fixture'
                context = false
                """
            );
        }

        await fixture.Store.CreateAsync("one", Token);
        await fixture.Store.JoinAsync("one", fixture.Members.Select(member => member.Id).ToArray(), Token);
        await using (var session = await fixture.SessionAsync(1))
        {
            await session.PutMemoryAsync(
                new()
                {
                    Type = "decision",
                    Content = "PDF invoice generation requires deterministic rendering",
                    Source = new MemorySource("user", "TASK-0020 fixture", "fixture"),
                },
                Token
            );
        }

        var foreignHash = Hash(fixture.Members[1].DatabasePath);
        var binary = Path.Combine(
            ProviderFixture.RepositoryRoot,
            "src",
            "Harness.Cli",
            "bin",
            Directory.GetParent(ProviderFixture.Worker)!.Parent!.Name,
            "net10.0",
            "harness.dll"
        );
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["HARNESS_CONFIG_DIR"] = fixture.Global;
        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(
                new()
                {
                    Command = "dotnet",
                    Arguments = [binary, "mcp", "--project", fixture.Members[0].Root],
                    WorkingDirectory = fixture.Members[0].Root,
                    EnvironmentVariables = environment,
                    InheritEnvironmentVariables = false,
                }
            ),
            cancellationToken: Token
        );
        var tools = await client.ListToolsAsync(cancellationToken: Token);
        foreach (var name in new[] { "library_adoption", "similar_concerns" })
        {
            ToolSchema.AssertMatches(tools.Single(tool => tool.Name == name));
        }
        var adoption = await client.CallToolAsync(
            "library_adoption",
            new Dictionary<string, object?> { ["library"] = "alpha", ["depth"] = "deep" },
            cancellationToken: Token
        );
        adoption.IsError.Should().BeNull();
        var report = adoption.StructuredContent!.Value;
        report.GetProperty("consumers")[0].GetProperty("project").GetString().Should().Be("beta");
        report
            .GetProperty("possible_local_implementations")
            .EnumerateArray()
            .Should()
            .ContainSingle()
            .Which.GetProperty("project")
            .GetString()
            .Should()
            .Be("gamma");
        report.GetProperty("notes").ToString().Should().Contain("may be unrelated");
        var comparison = await client.CallToolAsync(
            "similar_concerns",
            new Dictionary<string, object?> { ["concern"] = "pdf invoice generation" },
            cancellationToken: Token
        );
        comparison.IsError.Should().BeNull();
        var concern = comparison.StructuredContent!.Value;
        concern
            .GetProperty("notes")
            .ToString()
            .Should()
            .Contain("absence of code is not a negative finding")
            .And.Contain("not behaviour");
        var beta = concern
            .GetProperty("members")
            .EnumerateArray()
            .Single(member => member.GetProperty("project").GetString() == "beta");
        beta.GetProperty("decisions")[0].GetProperty("source_ref").GetString().Should().Be("TASK-0020 fixture");
        beta.GetProperty("dependencies")
            .EnumerateArray()
            .Should()
            .ContainSingle()
            .Which.GetProperty("provider")
            .GetString()
            .Should()
            .Be("manifest");
        var text = comparison.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which.Text;
        using var parsed = JsonDocument.Parse(text);
        JsonElement.DeepEquals(parsed.RootElement, concern).Should().BeTrue();
        foreach (
            var (name, args) in new[]
            {
                ("library_adoption", new Dictionary<string, object?> { ["library"] = "unknown" }),
                ("library_adoption", new Dictionary<string, object?> { ["library"] = "alpha", ["depth"] = "invalid" }),
                ("similar_concerns", new Dictionary<string, object?> { ["concern"] = " " }),
                ("similar_concerns", new Dictionary<string, object?> { ["concern"] = "pdf", ["depth"] = "invalid" }),
            }
        )
        {
            (await client.CallToolAsync(name, args, cancellationToken: Token)).IsError.Should().BeTrue();
        }

        Hash(fixture.Members[1].DatabasePath).Should().Be(foreignHash);
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
