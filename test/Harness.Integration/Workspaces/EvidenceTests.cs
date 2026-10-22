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

[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The disposed registries own fixture providers."
)]
public sealed class EvidenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MissingProvidersAndUnreadableMembersRemainUnknownRatherThanNegativeFindings()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        Seed(fixture);
        var missing = fixture.Members[2] with { Root = Path.Combine(fixture.Members[2].Root, "missing") };
        WorkspaceMember[] members = [fixture.Members[0], fixture.Members[1], missing];
        var built = 0;
        await using var current = new ProviderRegistry();
        await using var search = new WorkspaceSearchSession(
            new(members[0], members),
            current,
            (_, _) =>
            {
                built++;
                return new ProviderRegistry();
            }
        );
        var adoption = await search.AdoptionAsync("alpha", Token);
        built.Should().Be(0);
        adoption.Consumers.Should().ContainSingle().Which.Project.Should().Be("beta");
        adoption.NonConsumers.Should().NotContain("gamma");
        adoption.Notes.Should().Contain(note => note.Contains("gamma", StringComparison.Ordinal));
        var concern = await search.ConcernAsync("pdf invoice generation", false, Token);
        concern
            .Notes.Should()
            .Contain(note => note.Contains("no provider offers symbol search", StringComparison.Ordinal));
        concern.Members.Should().OnlyContain(member => member.Code.Count == 0);
        concern
            .Members.Single(member => member.Project is "beta")
            .Dependencies.Should()
            .Contain(dependency => dependency.Package == "QuestPDF");
        await using var healthyCurrent = EmptyRegistry();
        await using var healthy = new WorkspaceSearchSession(
            new(fixture.Members[0], fixture.Members.Take(2).ToArray()),
            healthyCurrent,
            (_, _) => EmptyRegistry()
        );
        var empty = await healthy.ConcernAsync("pdf", false, Token);
        empty.Members.Should().OnlyContain(member => member.Code.Count == 0);
        empty.Notes.Should().NotContain(note => note.Contains("no provider offers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StackComparisonsKeepPublishedVersionsSeparateFromVersionsMembersDependOn()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        Seed(fixture);
        var reader = new WorkspaceEvidenceReader(fixture.Members);
        var summary = await reader.ProjectStackAsync(fixture.Members[1].Id, Token);
        summary
            .Uses.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new
                {
                    Package = "Acme.Core",
                    MemberId = fixture.Members[0].Id,
                    Declared = "2.0",
                    Versions = new[] { "1.0" },
                }
            );
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

    private static void Seed(WorkspaceFixture fixture)
    {
        var library = fixture.Repositories[0];
        var consumer = fixture.Repositories[1];

        library.Write(
            "Core/Core.csproj",
            "<Project><PropertyGroup><PackageId>Acme.Core</PackageId><Version>2.0</Version><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        library.Write(
            "Core/PdfInvoiceGenerator.cs",
            "namespace Acme.Core.Documents;\npublic class PdfInvoiceGenerator {}\npublic readonly record struct Revision(int N);\npublic static class Extensions {}\n"
        );
        library.Write(
            "Core/Extensions.cs",
            "namespace Microsoft.Extensions.DependencyInjection;\npublic static class FrameworkExtensions {}\n"
        );
        library.Write("test/Core.Tests.csproj", "<Project/>");
        consumer.Write(
            "Api/Api.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>
            <PackageReference Include="Acme.Core" Version="1.0" />
            <PackageReference Include="QuestPDF" Version="2024.1.0" />
            </ItemGroup></Project>
            """
        );
        consumer.Write(
            "Api/Local.cs",
            "using Acme.Core.Documents;\npublic class PdfInvoiceGenerator {}\npublic class Extensions {}\n"
        );
    }

    private static ProviderRegistry EmptyRegistry()
    {
        var registry = new ProviderRegistry();
        registry.Register(new Providers.ProviderRegistryTests.Fake("healthy", [Capabilities.SymbolSearch]));
        return registry;
    }
}
