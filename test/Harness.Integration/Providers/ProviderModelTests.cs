using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Xunit;

namespace Harness.Integration.Providers;

public sealed class ProviderModelTests
{
    [Theory]
    [InlineData(Capabilities.TextSearch, "exact")]
    [InlineData(Capabilities.FileSearch, "exact")]
    [InlineData(Capabilities.ArchChangeCoupling, "exact")]
    [InlineData(Capabilities.SymbolSearch, "inferred")]
    [InlineData(Capabilities.Callers, "inferred")]
    [InlineData(Capabilities.PackageInfo, "inferred")]
    [InlineData(Capabilities.ArchDuplicates, "inferred")]
    public void EvidenceDefaultsAndDeclarationsRemainDistinct(string capability, string expected)
    {
        new CapabilityInfo(capability, "fake").EvidenceOf().Should().Be(expected);
        new CapabilityInfo(capability, "fake", Evidence: "exact").EvidenceOf().Should().Be("exact");
        new CapabilityInfo(capability, "fake", Evidence: "").EvidenceOf().Should().Be(expected);
    }

    [Theory]
    [InlineData("", "any.cs", true)]
    [InlineData("src", "src", true)]
    [InlineData("src/", "src/a.cs", true)]
    [InlineData("src", "src2/a.cs", false)]
    [InlineData("src", "other/a.cs", false)]
    [InlineData("src//", "src/a.cs", false)]
    public void PathPrefixesRespectDirectoryBoundaries(string prefix, string file, bool expected) =>
        new ProviderRequest(Capabilities.TextSearch, Path: prefix).Covers(file).Should().Be(expected);

    [Fact]
    public void CodePackageAndArchitectureModelsRetainAllFieldsAndWireNames()
    {
        Capabilities.IsPackage(Capabilities.PackageTypeDefinition).Should().BeTrue();
        Capabilities.IsPackage(Capabilities.SymbolSearch).Should().BeFalse();
        Capabilities.IsArchitecture(Capabilities.ArchCycles).Should().BeTrue();
        Capabilities.IsArchitecture(Capabilities.Overview).Should().BeFalse();
        Capabilities.Architecture.Should().Equal(Capabilities.ArchDependencies, Capabilities.ArchCycles, Capabilities.ArchChangeCoupling, Capabilities.ArchDuplicates, Capabilities.ArchDeadCode);
        var request = new ProviderRequest(Capabilities.PackageTypeDefinition, Package: "Example.Package", Version: "1.2.3", Type: "Example.Type");
        request.Package.Should().Be("Example.Package");
        request.Version.Should().Be("1.2.3");
        request.Type.Should().Be("Example.Type");
        var item = new ProviderItem
        {
            Kind = "relation",
            Name = "A",
            QualifiedName = "A.B",
            Label = "project",
            Path = "A.csproj",
            StartLine = 2,
            EndLine = 3,
            Snippet = "reference",
            Score = 0.7,
            Depth = 1,
            Related = new("B", "B.csproj", 4),
            Members = ["A", "B"],
            Count = 8,
            Confidence = 0.9,
            Provider = "compiler",
            Tool = "dependencies",
        };
        var json = JsonSerializer.SerializeToElement(item);
        json.GetProperty("qualified_name").GetString().Should().Be("A.B");
        json.GetProperty("related").GetProperty("start_line").GetInt32().Should().Be(4);
        json.GetProperty("members").GetArrayLength().Should().Be(2);
        var roundtrip = JsonSerializer.Deserialize<ProviderItem>(json).Should().BeOfType<ProviderItem>().Which;
        (roundtrip with { Members = item.Members }).Should().Be(item);
        JsonSerializer.Serialize(new ProviderItem()).Should().Be("""{"kind":"","provider":"","tool":""}""");
        JsonSerializer.Serialize(new ProviderHealth("idle")).Should().Be("""{"state":"idle"}""");
        JsonSerializer.Serialize(new ProviderHealth("idle", "", "")).Should().Be("""{"state":"idle"}""");
        JsonSerializer.Serialize(new ProviderReference("", "")).Should().Be("""{}""");
        JsonSerializer.Serialize(new ProviderItem { Name = "", QualifiedName = "", Label = "", Path = "", Snippet = "", Members = [] }).Should().Be("""{"kind":"","provider":"","tool":""}""");
        JsonSerializer.SerializeToElement(new CapabilityInfo("x", "y", Evidence: "")).TryGetProperty("evidence", out _).Should().BeFalse();
        var info = JsonSerializer.SerializeToElement(new CapabilityInfo(Capabilities.Context, "compiler", "high", "medium", "indexed", "project", "exact"));
        info.GetProperty("freshness").GetString().Should().Be("indexed");
        info.GetProperty("evidence").GetString().Should().Be("exact");
    }
}
