using AwesomeAssertions;
using Harness.Core.Architecture;
using Harness.Infrastructure.Architecture.Clones;
using Harness.Infrastructure.Architecture.Complexity;
using Harness.Infrastructure.Architecture.Metrics;
using Xunit;

namespace Harness.Unit.Architecture;

public sealed class MetricsReportTests
{
    [Fact]
    public void ReportMembersKeepTheirFileLineValuesAndProject()
    {
        using var tree = new ReaderTree(new() { ["placeholder.txt"] = "" });
        var app = Path.Combine(tree.Root, "src", "App", "Router.cs");
        var lib = Path.Combine(tree.Root, "src", "App.Lib", "Clock.cs");
        var outside = Path.Combine(Path.GetTempPath(), "Elsewhere.cs");
        // The shape Microsoft.CodeAnalysis.Metrics 5.6.0 writes (TASK-0040), trimmed to what is read.
        var report = Path.Combine(tree.Root, "report.xml");
        File.WriteAllText(
            report,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CodeMetricsReport Version="1.0">
              <Targets>
                <Target Name="App.csproj">
                  <Assembly Name="App, Version=1.0.0.0">
                    <Namespaces>
                      <Namespace Name="App">
                        <Types>
                          <NamedType Name="Router">
                            <Metrics><Metric Name="CyclomaticComplexity" Value="99" /></Metrics>
                            <Members>
                              <Method Name="(string Name, int Count) Router.Route(int[]? xs)" File="{app}" Line="12">
                                <Metrics>
                                  <Metric Name="MaintainabilityIndex" Value="41" />
                                  <Metric Name="CyclomaticComplexity" Value="27" />
                                  <Metric Name="ClassCoupling" Value="3" />
                                </Metrics>
                              </Method>
                              <Property Name="string Router.Label" File="{app}" Line="30">
                                <Metrics>
                                  <Metric Name="MaintainabilityIndex" Value="90" />
                                  <Metric Name="CyclomaticComplexity" Value="2" />
                                </Metrics>
                                <Accessors>
                                  <Method Name="string Router.Label.get" File="{app}" Line="30">
                                    <Metrics><Metric Name="CyclomaticComplexity" Value="2" /></Metrics>
                                  </Method>
                                </Accessors>
                              </Property>
                              <Field Name="int Router.count" File="{app}" Line="5">
                                <Metrics><Metric Name="CyclomaticComplexity" Value="1" /></Metrics>
                              </Field>
                              <Method Name="void Router.Moved()" File="{outside}" Line="3">
                                <Metrics><Metric Name="CyclomaticComplexity" Value="9" /></Metrics>
                              </Method>
                            </Members>
                          </NamedType>
                          <NamedType Name="Clock">
                            <Members>
                              <Method Name="T Clock.Tick&lt;T&gt;(T value)" File="{lib}" Line="7">
                                <Metrics><Metric Name="CyclomaticComplexity" Value="4" /></Metrics>
                              </Method>
                            </Members>
                          </NamedType>
                        </Types>
                      </Namespace>
                    </Namespaces>
                  </Assembly>
                </Target>
              </Targets>
            </CodeMetricsReport>
            """
        );

        var members = MetricsReport.Parse(report, tree.Root, ["src/App/App.csproj", "src/App.Lib/App.Lib.csproj"]);

        members
            .Should()
            .Equal(
                new MeasuredMember("src/App/Router.cs", 12, "Router.Route", "method", 27, 41, "src/App/App.csproj"),
                new MeasuredMember("src/App/Router.cs", 30, "Router.Label", "property", 2, 90, "src/App/App.csproj"),
                new MeasuredMember(
                    "src/App.Lib/Clock.cs",
                    7,
                    "Clock.Tick",
                    "method",
                    4,
                    0,
                    "src/App.Lib/App.Lib.csproj"
                )
            );
    }

    [Theory]
    [InlineData("void Program.Main(string[] args)", "Program.Main")]
    [InlineData(
        "List<CloneFunction> CloneExtractor.CFunctions(string path, List<CodeToken> tokens)",
        "CloneExtractor.CFunctions"
    )]
    [InlineData("(string Name, int Count) Samples.Tuple(int[]? xs)", "Samples.Tuple")]
    [InlineData("T Samples.Generic<T>(T? value)", "Samples.Generic")]
    [InlineData("Samples.Samples(bool a)", "Samples.Samples")]
    [InlineData("string ArchitectureFinding.Kind", "ArchitectureFinding.Kind")]
    [InlineData("int Box<T>.Get<U>(Func<T, U> map)", "Box.Get")]
    [InlineData("void Outer.Inner.Run()", "Inner.Run")]
    public void SignaturesNameTypeAndMemberAsTheCountDoes(string signature, string name) =>
        MetricsReport.Name(signature).Should().Be(name);

    [Fact]
    public void ExactValuesReplaceTheCountAtTheSamePlace()
    {
        var code =
            "namespace App;\npublic class Router\n{\n    public int Route(int a)\n    {\n"
            + string.Concat(Enumerable.Repeat("        if (a > 0) { a--; }\n", 13))
            + "        return a;\n    }\n\n    public int Small(int a) => a;\n}\n";
        using var tree = new ReaderTree(new() { ["src/Router.cs"] = code });
        var reader = new ComplexityReader(new CloneExtractor(tree.Repository()));
        var languages = new Dictionary<string, ComplexityThresholds> { ["csharp"] = new() };
        MeasuredMember[] exact =
        [
            new("src/Router.cs", 4, "Router.Route", "method", 30, 35, "src/App.csproj"),
            new("src/Router.cs", 21, "Router.Label", "property", 12, 70, "src/App.csproj"),
            new("src/obj/Generated.g.cs", 9, "Serializer.Read", "method", 80, 10, "src/App.csproj"),
        ];

        var counted = reader.Read(languages).Should().ContainSingle().Which;
        counted.Measure.Should().Be(14);
        counted.Source.Tool.Should().Be("internal/complexity");

        var measured = reader.Read(languages, exact);

        measured
            .Select(finding => (finding.Key, finding.Measure, finding.Level, finding.Source.Tool))
            .Should()
            .Equal(
                ("complexity:src/Router.cs#Router.Route", 30, "warn", ComplexityReader.Measured),
                ("complexity:src/Router.cs#Router.Label", 12, "info", ComplexityReader.Measured)
            );
        measured[0].Why.Should().Contain("Measured by Microsoft.CodeAnalysis.Metrics (maintainability index 35)");
        measured[0].Basis.Should().Be("fact");
        reader
            .Read(new Dictionary<string, ComplexityThresholds> { ["go"] = new() }, exact)
            .Should()
            .BeEmpty("exact values apply to C# only when C# is enabled");
    }
}
