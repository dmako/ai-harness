using AwesomeAssertions;
using Harness.Core.Architecture;
using Harness.Core.Providers;
using Harness.Infrastructure.Architecture;
using Xunit;

namespace Harness.Unit.Architecture;

/// <summary>Item-list (@(Name)) project references and ReferenceOutputAssembly=false (TASK-0137).</summary>
public sealed class ItemListReferenceTests
{
    private const string Library = """<Project Sdk="Microsoft.NET.Sdk" />""";

    /// <summary>The reported plugin host, with one more plugin from an imported .props and one removed.</summary>
    private const string PluginHost = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>
          <Import Project="plugins.props" />
          <ItemGroup>
            <PluginProject Include="..\Plugins\RCService.Plugins.Actions\RCService.Plugins.Actions.csproj" />
            <PluginProject Include="..\Plugins\RCService.Plugins.AutoAlignment\RCService.Plugins.AutoAlignment.csproj" />
            <PluginProject Include="..\Plugins\Old\Old.csproj" />
          </ItemGroup>
          <ItemGroup>
            <PluginProject Remove="..\Plugins\Old\Old.csproj" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="@(PluginProject)">
              <ReferenceOutputAssembly>False</ReferenceOutputAssembly>
              <Private>False</Private>
              <Targets>Publish;Build;DebugSymbolsProjectOutputGroup</Targets>
            </ProjectReference>
          </ItemGroup>
        </Project>
        """;

    private static Dictionary<string, string> Files() =>
        new()
        {
            ["Host/Host.csproj"] = PluginHost,
            ["Host/plugins.props"] = """
                <Project>
                  <ItemGroup>
                    <PluginProject Include="..\Plugins\RCService.Plugins.Extra\RCService.Plugins.Extra.csproj" />
                  </ItemGroup>
                </Project>
                """,
            // a plugin that uses the host's code: no circle, the host only builds it first
            ["Plugins/RCService.Plugins.Actions/RCService.Plugins.Actions.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup><ProjectReference Include="..\..\Host\Host.csproj" /></ItemGroup>
                </Project>
                """,
            ["Plugins/RCService.Plugins.AutoAlignment/RCService.Plugins.AutoAlignment.csproj"] = Library,
            ["Plugins/RCService.Plugins.Extra/RCService.Plugins.Extra.csproj"] = Library,
            ["Plugins/Old/Old.csproj"] = Library,
            ["Libs/A/A.csproj"] = Library,
            ["Libs/B/B.csproj"] = Library,
            ["Libs/C/C.csproj"] = Library,
            ["Libs/C/notes.txt"] = "",
            // a wildcard list with Exclude and Update, a transform, an unresolvable list and property
            ["Wild/Wild.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><LibRoot>..\Libs</LibRoot></PropertyGroup>
                  <ItemGroup>
                    <Lib Include="$(LibRoot)\**\*.csproj" Exclude="$(LibRoot)\C\*.csproj" />
                    <Lib Update="$(LibRoot)\B\B.csproj" ReferenceOutputAssembly="false" />
                  </ItemGroup>
                  <ItemGroup>
                    <ProjectReference Include="@(Lib->'%(FullPath)')" />
                    <ProjectReference Include="@(Undeclared);$(NoSuchProperty)\X.csproj;..\Gone\Gone.csproj" />
                  </ItemGroup>
                </Project>
                """,
            // transforms by %(Identity), %(Filename) and %(Extension), mixed with a literal path
            ["Named/Named.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Short Include="A;B" />
                    <Lib Include="..\Libs\C\C.csproj" />
                  </ItemGroup>
                  <ItemGroup>
                    <ProjectReference Include="@(Short->'..\Libs\%(Identity)\%(Identity).csproj')" />
                    <ProjectReference
                      Include="@(Lib->'..\Libs\C\%(Filename)%(Extension)');..\Wild\Wild.csproj"
                      ReferenceOutputAssembly="FALSE" />
                  </ItemGroup>
                </Project>
                """,
        };

    [Fact]
    public void ItemListsResolveProjectReferencesAndReferenceOutputAssemblyFalseIsBuildOrderOnly()
    {
        using var tree = new ReaderTree(Files());
        var build = new StaticMsBuild(tree.Repository());

        build
            .Evaluate("Host/Host.csproj")
            .References.Select(reference => (reference.Path, reference.BuildOrder, reference.Unresolved))
            .Should()
            .Equal(
                ("Plugins/RCService.Plugins.Extra/RCService.Plugins.Extra.csproj", true, false),
                ("Plugins/RCService.Plugins.Actions/RCService.Plugins.Actions.csproj", true, false),
                ("Plugins/RCService.Plugins.AutoAlignment/RCService.Plugins.AutoAlignment.csproj", true, false)
            );
        build
            .Evaluate("Wild/Wild.csproj")
            .References.Select(reference => (reference.Path, reference.BuildOrder, reference.Unresolved))
            .Should()
            .Equal(
                ("Libs/A/A.csproj", false, false),
                ("Libs/B/B.csproj", true, false),
                ("@(Undeclared)", false, true),
                ("$(NoSuchProperty)\\X.csproj", false, true),
                ("Gone/Gone.csproj", false, false)
            );
        build
            .Evaluate("Named/Named.csproj")
            .References.Select(reference => (reference.Path, reference.BuildOrder))
            .Should()
            .Equal(
                ("Libs/A/A.csproj", false),
                ("Libs/B/B.csproj", false),
                ("Libs/C/C.csproj", true),
                ("Wild/Wild.csproj", true)
            );
    }

    [Fact]
    public void BuildOrderReferencesStayOutOfLayersCyclesAndRulesButKeepTheirTargetsUsed()
    {
        using var tree = new ReaderTree(Files());
        var repository = tree.Repository();
        var build = new BuildReader(repository, new(repository)).Read()!;
        var host = build.Projects.Single(unit => unit.Name is "Host");
        host.References.Should().BeEmpty();
        host.BuildOrder.Should()
            .Equal(
                "Plugins/RCService.Plugins.Extra/RCService.Plugins.Extra.csproj",
                "Plugins/RCService.Plugins.Actions/RCService.Plugins.Actions.csproj",
                "Plugins/RCService.Plugins.AutoAlignment/RCService.Plugins.AutoAlignment.csproj"
            );

        var architecture = ArchitectureAnalyzer.Analyze(build, _ => false);
        var units = architecture.Projects.ToDictionary(unit => unit.Name);
        units["Host"].Layer.Should().Be(0, "a build-order reference does not lift the host above its plugins");
        units["RCService.Plugins.Actions"].Layer.Should().Be(1, "it references the host's code");
        units["RCService.Plugins.AutoAlignment"].BuildOrderBy.Should().Equal("Host/Host.csproj");
        units["RCService.Plugins.AutoAlignment"].ReferencedBy.Should().BeEmpty();

        var findings = architecture.Findings;
        findings.Should().NotContain(finding => finding.Kind == "cycle");
        findings
            .Where(finding => finding.Kind == "unused")
            .Select(finding => finding.Project)
            .Should()
            .BeEquivalentTo(
                ["Plugins/Old/Old.csproj", "Named/Named.csproj"],
                "C and Wild are built through build-order references"
            );
        findings
            .Where(finding => finding.Kind == "missing-project")
            .Select(finding => finding.Key)
            .Should()
            .Equal("missing-project:Wild/Wild.csproj|Gone/Gone.csproj");
        var unresolved = findings.Should().ContainSingle(finding => finding.Kind == "unresolved-reference").Subject;
        unresolved.Level.Should().Be("info");
        unresolved.Project.Should().Be("Wild/Wild.csproj");
        unresolved
            .Evidence.Should()
            .Equal("Wild/Wild.csproj:9 ($(NoSuchProperty)\\X.csproj)", "Wild/Wild.csproj:9 (@(Undeclared))");

        var rules = new BoundaryRules();
        rules.Add("forbid: Host -> RCService.*", new RuleReference("mem_1", Scope: "project", Type: "constraint"));
        rules.Check(architecture.Projects).Should().BeEmpty("build-order references are not code dependencies");
    }
}
