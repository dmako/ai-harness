using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Harness.Core.Architecture;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Storage;
using Harness.Providers.Registry;
using Xunit;

namespace Harness.Integration.Workspaces;

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Read-only database and provider fixtures are owned by using declarations/registries.")]
public sealed class ArchitectureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ApplicableKnowledgeUsesNarrowGroupsAndPreservesEveryRuleProvenance()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.Store.CreateAsync("one", Token);
        await fixture.Store.JoinAsync("one", fixture.Members.Select(member => member.Id).ToArray(), Token);
        var levels = new KnowledgeLevels(fixture.Lifecycle.GlobalConfigurationPath, fixture.Lifecycle.Registry);
        var workspaces = levels.Load();
        using (var own = HarnessDatabase.Open(fixture.Members[0].DatabasePath, cancellationToken: Token))
            new MemoryStore(own).Put(new() { Type = "constraint", Content = "group Domain = *.Core; forbid: Domain -> *.WebApi", Source = new(Ref: "architecture fixture") }, Token);
        using (var workspace = levels.OpenLevel(workspaces, new("workspace", "one"), fixture.Members[0].Id, Token))
            new MemoryStore(workspace).Put(new() { Type = "decision", Content = "group Domain = *.Other; only: Domain -> *.Models", Source = new(Ref: "workspace fixture") }, Token);
        using (var user = levels.OpenLevel(workspaces, MemoryScope.User, fixture.Members[0].Id, Token))
            new MemoryStore(user).Put(new() { Type = "convention", Content = "layers: WebApi > Core > Models", Source = new(Ref: "user fixture") }, Token);
        var hashes = fixture.Hashes();
        using var read = HarnessDatabase.OpenReadOnly(fixture.Members[0].DatabasePath);
        var rules = ArchitectureReader.RulesFor(new MemoryStore(read), levels, fixture.Members[0].Id, Token);
        rules.Groups.Should().ContainSingle().Which.Patterns.Should().Equal("*.Core");
        rules.Rules.Should().HaveCount(3);
        rules.Rules.Select(rule => rule.Reference.Scope).Should().BeEquivalentTo("project", "workspace:one", "user");
        var core = new BuildUnit { Name = "App.Core", Path = "Core.csproj" };
        core.References.Add("Web.csproj");
        var findings = rules.Check([core, new() { Name = "App.WebApi", Path = "Web.csproj", Kind = "entry" }]);
        findings.Should().HaveCount(3);
        findings.Should().Contain(finding => finding.Level == "problem" && finding.Rule!.Scope == "project");
        fixture.Hashes().Should().Equal(hashes);
    }
    [Fact]
    public async Task WorkspaceUsageAndUnknownMembersDoNotManufactureNegativeFindings()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        fixture.Repositories[0].Write("Core.csproj", "<Project><PropertyGroup><PackageId>Example.Core</PackageId></PropertyGroup></Project>");
        fixture.Repositories[1].Write("Host.csproj", "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"Example.Core\" Version=\"1\"/></ItemGroup></Project>");
        fixture.Repositories[2].Write("Other.csproj", "<Project/>");
        var hashes = fixture.Hashes();
        var reader = new ArchitectureReader(fixture.Members);
        var project = await reader.ProjectAsync(fixture.Members[0], null, true, Token);
        project.Architecture.Should().NotBeNull();
        project.Architecture.Findings.Should().NotContain(finding => finding.Kind == "unused");
        (await reader.SystemAsync(Token)).Nodes.Should().Contain(node => node.MemberId == fixture.Members[0].Id && node.Kind == "library");
        fixture.Repositories[1].Write("Host.csproj", "<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        project = await new ArchitectureReader(fixture.Members).ProjectAsync(fixture.Members[0], null, true, Token);
        project.Architecture!.Findings.Should().Contain(finding => finding.Kind == "unused" && finding.Level == "info");
        var missing = fixture.Members.ToArray();
        missing[2] = missing[2] with { Root = Path.Combine(missing[2].Root, "gone") };
        project = await new ArchitectureReader(missing).ProjectAsync(missing[0], null, true, Token);
        project.Notes.Should().NotBeEmpty();
        project.Architecture!.Findings.Should().NotContain(finding => finding.Kind == "unused");
        var quick = await new ArchitectureReader(fixture.Members).ProjectAsync(fixture.Members[0], null, false, Token);
        quick.Architecture!.Findings.Should().NotContain(finding => finding.Kind == "unused");
        fixture.Hashes().Should().Equal(hashes);
    }
    [Fact]
    public async Task ProviderCyclesRetainEvidenceAndHealthyEmptyDiffersFromUnavailable()
    {
        await using var registry = new ProviderRegistry();
        registry.Register(new Providers.ProviderRegistryTests.Fake("graph", [Capabilities.ArchCycles], execute: (_, _) => Task.FromResult(new ProviderResult { Items = [new() { Members = ["B", "A"], Path = "src/a.cs", StartLine = 3, Confidence = 0.8, Provider = "graph", Tool = "cycles" }, new() { Members = ["Self"] }] })));
        var answer = await ArchitectureProviders.CyclesAsync(registry, Token);
        answer.Answered.Should().BeTrue();
        var cycle = answer.Findings.Should().ContainSingle().Subject;
        cycle.Key.Should().Be("cycle:A|B");
        cycle.Evidence.Should().Equal("src/a.cs:3");
        cycle.Source.Should().Be(new FindingSource("graph", "cycles"));
        cycle.Basis.Should().Be("fact");
        cycle.Confidence.Should().BeApproximately(0.8, 0.001);
        await using var healthy = new ProviderRegistry();
        healthy.Register(new Providers.ProviderRegistryTests.Fake("empty", [Capabilities.ArchCycles]));
        var empty = await ArchitectureProviders.CyclesAsync(healthy, Token);
        empty.Answered.Should().BeTrue();
        empty.Findings.Should().BeEmpty();
        await using var unavailable = new ProviderRegistry();
        unavailable.Register(new Providers.ProviderRegistryTests.Fake("failed", [Capabilities.ArchCycles], execute: (_, _) => throw new IOException("fixture unavailable")));
        var failed = await ArchitectureProviders.CyclesAsync(unavailable, Token);
        failed.Answered.Should().BeFalse();
        failed.Notes.Should().NotBeEmpty();
    }
}
