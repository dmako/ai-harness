using System.Text.Json;
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
        Assert.Equal(expected, new CapabilityInfo(capability, "fake").EvidenceOf());
        Assert.Equal("exact", new CapabilityInfo(capability, "fake", Evidence: "exact").EvidenceOf());
        Assert.Equal(expected, new CapabilityInfo(capability, "fake", Evidence: "").EvidenceOf());
    }

    [Theory]
    [InlineData("", "any.cs", true)]
    [InlineData("src", "src", true)]
    [InlineData("src/", "src/a.cs", true)]
    [InlineData("src", "src2/a.cs", false)]
    [InlineData("src", "other/a.cs", false)]
    [InlineData("src//", "src/a.cs", false)]
    public void PathPrefixesRespectDirectoryBoundaries(string prefix, string file, bool expected) =>
        Assert.Equal(expected, new ProviderRequest(Capabilities.TextSearch, Path: prefix).Covers(file));

    [Fact]
    public void CodePackageAndArchitectureModelsRetainAllFieldsAndWireNames()
    {
        Assert.True(Capabilities.IsPackage(Capabilities.PackageTypeDefinition));
        Assert.False(Capabilities.IsPackage(Capabilities.SymbolSearch));
        Assert.True(Capabilities.IsArchitecture(Capabilities.ArchCycles));
        Assert.False(Capabilities.IsArchitecture(Capabilities.Overview));
        Assert.Equal([Capabilities.ArchDependencies, Capabilities.ArchCycles, Capabilities.ArchChangeCoupling, Capabilities.ArchDuplicates, Capabilities.ArchDeadCode], Capabilities.Architecture);
        var request = new ProviderRequest(Capabilities.PackageTypeDefinition, Package: "Example.Package", Version: "1.2.3", Type: "Example.Type");
        Assert.Equal("Example.Package", request.Package);
        Assert.Equal("1.2.3", request.Version);
        Assert.Equal("Example.Type", request.Type);
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
        Assert.Equal("A.B", json.GetProperty("qualified_name").GetString());
        Assert.Equal(4, json.GetProperty("related").GetProperty("start_line").GetInt32());
        Assert.Equal(2, json.GetProperty("members").GetArrayLength());
        var roundtrip = Assert.IsType<ProviderItem>(JsonSerializer.Deserialize<ProviderItem>(json));
        Assert.Equal(item, roundtrip with { Members = item.Members });
        Assert.Equal("{\"kind\":\"\",\"provider\":\"\",\"tool\":\"\"}", JsonSerializer.Serialize(new ProviderItem()));
        Assert.Equal("{\"state\":\"idle\"}", JsonSerializer.Serialize(new ProviderHealth("idle")));
        Assert.Equal("{\"state\":\"idle\"}", JsonSerializer.Serialize(new ProviderHealth("idle", "", "")));
        Assert.Equal("{}", JsonSerializer.Serialize(new ProviderReference("", "")));
        Assert.Equal("{\"kind\":\"\",\"provider\":\"\",\"tool\":\"\"}", JsonSerializer.Serialize(new ProviderItem { Name = "", QualifiedName = "", Label = "", Path = "", Snippet = "", Members = [] }));
        Assert.False(JsonSerializer.SerializeToElement(new CapabilityInfo("x", "y", Evidence: "")).TryGetProperty("evidence", out _));
        var info = JsonSerializer.SerializeToElement(new CapabilityInfo(Capabilities.Context, "compiler", "high", "medium", "indexed", "project", "exact"));
        Assert.Equal("indexed", info.GetProperty("freshness").GetString());
        Assert.Equal("exact", info.GetProperty("evidence").GetString());
    }
}
