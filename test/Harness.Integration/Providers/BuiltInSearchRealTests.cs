using System.Text.Json;
using AwesomeAssertions;
using Harness.Core.Providers;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Processes;
using Harness.Providers.Mcp;
using Harness.Providers.Packs;
using Harness.Providers.Search;
using Xunit;
using Root = Harness.Integration.Providers.ProviderRoutingTests.Root;

namespace Harness.Integration.Providers;

/// <summary>The built-in search against the real rg and fd on PATH; skipped where they are not installed.</summary>
public sealed class BuiltInSearchRealTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SearchToolset Tools(bool fd = true)
    {
        var found = SearchTools.Detected();
        if (!found.HasRg || fd && !found.HasFd)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "rg" + (fd ? " and fd" : "") + " not on PATH; " + SearchTools.InstallHint()
            );
        }

        return fd ? found : found with { Fd = "" };
    }

    private static Root Fixture()
    {
        var root = new Root();
        Directory.CreateDirectory(Path.Combine(root.Path, ".git"));
        root.Write(".gitignore", "generated/\n");
        root.Write(".ignore", "secret.txt\n");
        root.Write("src/Alpha.cs", "class Alpha\n{\n    void Run() => Alpha.Go(1);\n}\n");
        root.Write("src/Beta.cs", "class Beta { Alpha a; }\n");
        root.Write("docs/alpha-notes.md", "Alpha (in prose)\n");
        root.Write("generated/AlphaGen.cs", "class AlphaGen {}\n");
        root.Write("secret.txt", "Alpha\n");
        return root;
    }

    [Fact]
    public async Task TextSearchMatchesTheRgFdPackItemsSortedAndHonoursIgnoreFiles()
    {
        var tools = Tools(fd: false);
        using var root = Fixture();
        await using var provider = new RgFdProvider("search", tools, root.Path, new ProviderProcessFactory());
        var result = await provider.ExecuteAsync(new(Capabilities.TextSearch, "Alpha"), Token);
        result.Error.Should().BeNull();
        result.Truncated.Should().BeFalse();
        result
            .Items.Select(item => (item.Path, item.StartLine, item.Snippet))
            .Should()
            .Equal(
                ("docs/alpha-notes.md", 1, "Alpha (in prose)"),
                ("src/Alpha.cs", 1, "class Alpha"),
                ("src/Alpha.cs", 3, "void Run() => Alpha.Go(1);"),
                ("src/Beta.cs", 1, "class Beta { Alpha a; }")
            );
        // The rg-fd pack maps rg-fd-mcp's answer for the same matches to the same items.
        var pack = PackMapping("code.text_search");
        var body = JsonSerializer.SerializeToElement(
            new Dictionary<string, Dictionary<string, Dictionary<string, Line[]>>>
            {
                ["matches"] = result
                    .Items.GroupBy(item => Path.Combine(root.Path, item.Path!))
                    .ToDictionary(
                        group => group.Key,
                        group => new Dictionary<string, Line[]>
                        {
                            ["lines"] = group.Select(item => new Line(item.StartLine, item.Snippet!, true)).ToArray(),
                        }
                    ),
            },
            LineJson.Default.DictionaryStringDictionaryStringDictionaryStringLineArray
        );
        var mapped = McpMapping
            .Collect(body, pack.Items)
            .Entries.Where(entry => McpMapping.Matches(entry, pack.Where))
            .Select(entry => McpMapping.Item(entry, pack, Capabilities.TextSearch, root.Path))
            .ToArray();
        result.Items.Select(item => item with { Provider = "", Tool = "" }).Should().Equal(mapped);

        var scoped = await provider.ExecuteAsync(new(Capabilities.TextSearch, "Alpha.Go(", "src", 5), Token);
        scoped.Items.Should().ContainSingle().Which.Path.Should().Be("src/Alpha.cs");
        var limited = await provider.ExecuteAsync(new(Capabilities.TextSearch, "Alpha", Limit: 2), Token);
        limited.Items.Select(item => item.Path).Should().Equal("docs/alpha-notes.md", "src/Alpha.cs");
        limited.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task FileSearchWithFdAndWithRgFilesFindTheSameSortedFiles()
    {
        var tools = Tools();
        using var root = Fixture();
        await using var withFd = new RgFdProvider("search", tools, root.Path, new ProviderProcessFactory());
        await using var withRg = new RgFdProvider(
            "search",
            tools with
            {
                Fd = "",
            },
            root.Path,
            new ProviderProcessFactory()
        );
        var pack = PackMapping("code.file_search");
        foreach (var provider in new[] { withFd, withRg })
        {
            var result = await provider.ExecuteAsync(new(Capabilities.FileSearch, "alpha"), Token);
            result.Error.Should().BeNull();
            result.Items.Select(item => item.Path).Should().Equal("docs/alpha-notes.md", "src/Alpha.cs");
            var body = JsonSerializer.SerializeToElement(
                new Dictionary<string, Dictionary<string, string>[]>
                {
                    ["files"] = result
                        .Items.Select(item => new Dictionary<string, string>
                        {
                            ["file"] = Path.Combine(root.Path, item.Path!),
                        })
                        .ToArray(),
                },
                LineJson.Default.DictionaryStringDictionaryStringStringArray
            );
            var mapped = McpMapping
                .Collect(body, pack.Items)
                .Entries.Select(entry => McpMapping.Item(entry, pack, Capabilities.FileSearch, root.Path))
                .ToArray();
            result.Items.Select(item => item with { Provider = "", Tool = "" }).Should().Equal(mapped);
            var scoped = await provider.ExecuteAsync(new(Capabilities.FileSearch, "a", "src", 1), Token);
            scoped.Items.Select(item => item.Path).Should().Equal("src/Alpha.cs");
            scoped.Truncated.Should().BeTrue();
        }
    }

    [Fact]
    public async Task TheMcpRegistryOffersTheBuiltInSearchAsALightProvider()
    {
        Tools();
        using var root = Fixture();
        await using var registry = ConfiguredProviders.Build(
            new Dictionary<string, ProviderConfiguration>(),
            root.Path,
            new ProviderProcessFactory(),
            light: true,
            builtInSearch: true
        );
        List<string> observed = [];
        registry.SetObserver((result, _) => observed.Add(result.Provider + " " + result.Capability));
        var results = await registry.CallAsync(new(Capabilities.TextSearch, "Beta"), cancellationToken: Token);
        results.Should().ContainSingle().Which.Items.Should().ContainSingle().Which.Path.Should().Be("src/Beta.cs");
        observed.Should().Equal("search code.text_search");
    }

    private static CapabilityMapping PackMapping(string capability)
    {
        var directory = Path.Combine(Path.GetTempPath(), "harness-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var global = Path.Combine(directory, "config.toml");
            File.WriteAllText(global, "[providers.rg-fd]\ncommand = \"rg-fd-mcp\"\n");
            return ConfigurationLoader.LoadFiles(global, "").Value.Providers["rg-fd"].Capabilities[capability];
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}

internal sealed record Line(
    [property: System.Text.Json.Serialization.JsonPropertyName("line_num")] int LineNum,
    [property: System.Text.Json.Serialization.JsonPropertyName("content")] string Content,
    [property: System.Text.Json.Serialization.JsonPropertyName("is_match")] bool IsMatch
);

[System.Text.Json.Serialization.JsonSerializable(
    typeof(Dictionary<string, Dictionary<string, Dictionary<string, Line[]>>>)
)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>[]>))]
internal sealed partial class LineJson : System.Text.Json.Serialization.JsonSerializerContext;
