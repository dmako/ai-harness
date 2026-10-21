using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Core.Workspaces;
using Harness.Infrastructure.Workspaces;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Workspaces;

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The disposed registries own fixture providers.")]
public sealed class EvidenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("adoption", "dotnet")]
    [InlineData("adoption", "empty")]
    [InlineData("dependencies", "dotnet")]
    [InlineData("adoption", "node")]
    [InlineData("dependencies", "node")]
    [InlineData("adoption", "go")]
    [InlineData("dependencies", "go")]
    [InlineData("concern", "dotnet")]
    [InlineData("concern-empty", "dotnet")]
    public async Task MultiRepositoryEvidenceMatchesFrozenGo(string mode, string language)
    {
        var oracle = Environment.GetEnvironmentVariable("HARNESS_GO_ECOSYSTEM_ORACLE");
        if (string.IsNullOrEmpty(oracle))
            throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_ECOSYSTEM_ORACLE using Ecosystem/build_oracle.py.");
        using var fixture = await WorkspaceFixture.CreateAsync();
        Seed(fixture, language);
        var before = fixture.Hashes();
        var reader = new WorkspaceEvidenceReader(fixture.Members);
        var text = mode is "concern-empty" ? "the and" : "pdf invoice generation";
        object actual;
        if (mode is "adoption")
            actual = await reader.AdoptionAsync("alpha", Token);
        else if (mode is "dependencies")
            actual = await reader.DependenciesAsync(Token);
        else
        {
            await using var current = Registry();
            await using var session = new WorkspaceSearchSession(new(fixture.Members[0], fixture.Members), current, (_, _) => Registry());
            actual = await session.ConcernAsync(text, false, Token);
        }
        var providers = fixture.Members.ToDictionary(member => member.Root, _ => new { ID = "names", Items = Items() }, StringComparer.Ordinal);
        var expected = await RunAsync(oracle, new { mode = mode.StartsWith("concern", StringComparison.Ordinal) ? "concern" : mode, members = fixture.Members.Select(member => new { project = member.Name, project_id = member.Id, root = member.Root }), library = "alpha", concern = text, global = Path.Combine(fixture.Global, "go"), providers });
        var json = JsonSerializer.SerializeToNode(actual);
        JsonNode.DeepEquals(json, expected).Should().BeTrue($"C#: {json}\nGo: {expected}");
        fixture.Hashes().Should().Equal(before);
    }
    [Fact]
    public async Task MissingProvidersAndUnreadableMembersRemainUnknownRatherThanNegativeFindings()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        Seed(fixture, "dotnet");
        var missing = fixture.Members[2] with { Root = Path.Combine(fixture.Members[2].Root, "missing") };
        WorkspaceMember[] members = [fixture.Members[0], fixture.Members[1], missing];
        var built = 0;
        await using var current = new ProviderRegistry();
        await using var search = new WorkspaceSearchSession(new(members[0], members), current, (_, _) => { built++; return new ProviderRegistry(); });
        var adoption = await search.AdoptionAsync("alpha", Token);
        built.Should().Be(0);
        adoption.Consumers.Should().ContainSingle().Which.Project.Should().Be("beta");
        adoption.NonConsumers.Should().NotContain("gamma");
        adoption.Notes.Should().Contain(note => note.Contains("gamma", StringComparison.Ordinal));
        var concern = await search.ConcernAsync("pdf invoice generation", false, Token);
        concern.Notes.Should().Contain(note => note.Contains("no provider offers symbol search", StringComparison.Ordinal));
        concern.Members.Should().OnlyContain(member => member.Code.Count == 0);
        concern.Members.Single(member => member.Project is "beta").Dependencies.Should().Contain(dependency => dependency.Package == "QuestPDF");
        await using var healthyCurrent = EmptyRegistry();
        await using var healthy = new WorkspaceSearchSession(new(fixture.Members[0], fixture.Members.Take(2).ToArray()), healthyCurrent, (_, _) => EmptyRegistry());
        var empty = await healthy.ConcernAsync("pdf", false, Token);
        empty.Members.Should().OnlyContain(member => member.Code.Count == 0);
        empty.Notes.Should().NotContain(note => note.Contains("no provider offers", StringComparison.Ordinal));
    }
    [Fact]
    public async Task StackComparisonsKeepPublishedVersionsSeparateFromVersionsMembersDependOn()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        Seed(fixture, "dotnet");
        var reader = new WorkspaceEvidenceReader(fixture.Members);
        var summary = await reader.ProjectStackAsync(fixture.Members[1].Id, Token);
        summary.Uses.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Package = "Acme.Core", MemberId = fixture.Members[0].Id, Declared = "2.0", Versions = new[] { "1.0" } });
        var workspace = await reader.WorkspaceStackAsync(Token);
        var core = workspace.Rows.Single(row => row.Kind is "core");
        core.Published.Should().Be("2.0");
        core.Differ.Should().BeTrue();
        core.Owner.Should().Be(fixture.Members[0].Id);
        core.Versions.Keys.Should().Equal(fixture.Members[1].Id);
        workspace.Stacks.Should().HaveCount(3);
        var dependencies = await reader.DependencySummaryAsync(Token);
        dependencies.Nodes.Single(node => node.Project is "alpha").Library.Should().BeTrue();
        dependencies.Nodes.Single(node => node.Project is "beta").Library.Should().BeFalse();
    }
    private static void Seed(WorkspaceFixture fixture, string language)
    {
        if (language is "empty")
            return;
        var library = fixture.Repositories[0];
        var consumer = fixture.Repositories[1];
        if (language is "dotnet")
        {
            library.Write("Core/Core.csproj", "<Project><PropertyGroup><PackageId>Acme.Core</PackageId><Version>2.0</Version><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            library.Write("Core/PdfInvoiceGenerator.cs", "namespace Acme.Core.Documents;\npublic class PdfInvoiceGenerator {}\npublic readonly record struct Revision(int N);\npublic static class Extensions {}\n");
            library.Write("Core/Extensions.cs", "namespace Microsoft.Extensions.DependencyInjection;\npublic static class FrameworkExtensions {}\n");
            library.Write("test/Core.Tests.csproj", "<Project/>");
            consumer.Write("Api/Api.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>
                <PackageReference Include="Acme.Core" Version="1.0" />
                <PackageReference Include="QuestPDF" Version="2024.1.0" />
                </ItemGroup></Project>
                """);
            consumer.Write("Api/Local.cs", "using Acme.Core.Documents;\npublic class PdfInvoiceGenerator {}\npublic class Extensions {}\n");
        }
        else if (language is "node")
        {
            library.Write("package.json", """{"name":"@acme/core","version":"2"}""");
            library.Write("src/core.ts", "export class PdfInvoiceGenerator {}\nexport interface IRevision {}\n");
            consumer.Write("package.json", """{"dependencies":{"@acme/core":"^1","pdfkit":"^0.15.0"},"devDependencies":{"typescript":"5"}}""");
            consumer.Write("src/local.ts", "import { PdfInvoiceGenerator } from '@acme/core';\nexport class PdfInvoiceGenerator {}\n");
        }
        else
        {
            library.Write("go.mod", "module example.com/core\ngo 1.27\n");
            library.Write("core.go", "package core\ntype PdfInvoiceGenerator struct {}\nfunc GenerateInvoice() {}\n");
            consumer.Write("go.mod", "module example.com/api\ngo 1.27\nrequire example.com/core v1.0.0\n");
            consumer.Write("api.go", "package api\nimport \"example.com/core\"\ntype PdfInvoiceGenerator struct {}\n");
        }
    }
    private static ProviderItem[] Items() =>
    [
        new() { Name = "InvoicePdfGenerator", Path = "src/InvoicePdfGenerator.cs", StartLine = 3, Provider = "names" },
        new() { Name = "InvoicePdfGeneratorTests", Path = "test/InvoicePdfGeneratorTests.cs", StartLine = 2, Provider = "names" },
        new() { Name = "Customer", Path = "src/Customer.cs", Provider = "names" },
    ];
    private static ProviderRegistry Registry()
    {
        var registry = new ProviderRegistry();
        registry.Register(new Providers.ProviderRegistryTests.Fake("names", [Capabilities.SymbolSearch], execute: (_, _) => Task.FromResult(new ProviderResult { Items = Items() })));
        return registry;
    }
    private static ProviderRegistry EmptyRegistry()
    {
        var registry = new ProviderRegistry();
        registry.Register(new Providers.ProviderRegistryTests.Fake("healthy", [Capabilities.SymbolSearch]));
        return registry;
    }
    [SuppressMessage("Security", "S4036:Searching OS commands in PATH is security-sensitive", Justification = "Only the explicitly configured frozen test oracle runs directly without a shell.")]
    private static async Task<JsonNode?> RunAsync(string executable, object input)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input));
            process.StandardInput.Close();
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
            process.ExitCode.Should().Be(0, await error);
            return JsonNode.Parse(await output);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
}
