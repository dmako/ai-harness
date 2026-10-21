using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Providers.Registry;
using Xunit;
using Fake = Harness.Integration.Providers.ProviderRegistryTests.Fake;

namespace Harness.Integration.Providers;

public sealed class ProviderGoContractTests
{
    [Theory]
    [InlineData("metadata")]
    [InlineData("capabilities")]
    [InlineData("answers")]
    [InlineData("paths")]
    [InlineData("item")]
    [InlineData("empty_item")]
    [InlineData("health")]
    [InlineData("in_context")]
    public async Task MatchesFixtureCapturedFromPinnedGoRegistry(string section)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "providers-registry.json"), token);
        using var expected = JsonDocument.Parse(fixture);
        await using var registry = new ProviderRegistry();
        await using var z = Provider("z", [Capabilities.TextSearch]);
        await using var a = Provider("a", [Capabilities.SymbolSearch, Capabilities.TextSearch]);
        registry.Register(z);
        registry.Register(a);
        registry.ExcludeFromContext("z");
        string[] all = [Capabilities.SymbolSearch, Capabilities.TextSearch, Capabilities.FileSearch, Capabilities.Overview,
            Capabilities.Callers, Capabilities.References, Capabilities.Context, Capabilities.Impact, Capabilities.Flow,
            Capabilities.PackageSearch, Capabilities.PackageInfo, Capabilities.PackageTypes, Capabilities.PackageTypeDefinition,
            .. Capabilities.Architecture];
        var answers = await registry.CallAsync(new(Capabilities.TextSearch, Path: "src", Limit: 1), cancellationToken: token);
        (string Prefix, string File)[] paths = [("", "any.cs"), ("src", "src"), ("src/", "src/a.cs"), ("src", "src2/a.cs"), ("src", "other/a.cs")];
        object value = section switch
        {
            "metadata" => all.Select(capability => new { capability, evidence = Capabilities.DefaultEvidence(capability), package = Capabilities.IsPackage(capability), architecture = Capabilities.IsArchitecture(capability) }),
            "capabilities" => registry.Capabilities(),
            "answers" => answers.Select(result => new { provider = result.Provider, capability = result.Capability, items = result.Items, bytes = result.Bytes, truncated = result.Truncated }),
            "paths" => paths.Select(pair => new { prefix = pair.Prefix, file = pair.File, covers = new ProviderRequest("", Path: pair.Prefix).Covers(pair.File) }),
            "item" => new ProviderItem
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
            },
            "empty_item" => new ProviderItem(),
            "health" => new ProviderHealth("idle"),
            "in_context" => new Dictionary<string, bool> { ["z"] = registry.InContext("z"), ["a"] = registry.InContext("a") },
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };
        var actual = JsonSerializer.SerializeToElement(value);
        JsonElement.DeepEquals(expected.RootElement.GetProperty(section), actual).Should().BeTrue($"Go/{section}: {expected.RootElement.GetProperty(section)}\nC#/{section}: {actual}");
    }

    private static Fake Provider(string name, string[] capabilities) => new(name, capabilities, (request, _) =>
    {
        var matches = new[] { "src/a.cs", "src/b.cs", "src2/c.cs" }.Where(request.Covers).ToArray();
        var kept = request.Limit > 0 ? matches.Take(request.Limit) : matches;
        return Task.FromResult(new ProviderResult
        {
            Items = kept.Select(path => new ProviderItem { Kind = "match", Path = path, Provider = name, Tool = "find" }).ToArray(),
            Bytes = 123,
            Truncated = request.Limit > 0 && matches.Length > request.Limit,
        });
    }, "indexed", "medium");
}
